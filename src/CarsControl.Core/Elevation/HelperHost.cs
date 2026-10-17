using System.IO.Pipes;
using System.Text;
using CarsControl.Contracts;

namespace CarsControl.Core.Elevation;

/// <summary>
/// Main loop of CarsControl.Helper.exe (runs elevated, as the admin account). It connects to the pipe the app
/// created, verifies the pipe really belongs to its parent C.A.R.S. Control process in the same folder,
/// then executes allowlisted operations one at a time. It exits when the app exits, the pipe closes,
/// or after the idle timeout.
/// </summary>
public static class HelperHost
{
    public const string AppExeName = "CarsControl.exe";

    public sealed record Args(string PipeName, int ParentPid, int IdleMinutes, bool DevUnelevated);

    public static Args? ParseArgs(string[] argv)
    {
        string? pipe = null;
        int parent = 0, idle = 15;
        var dev = false;
        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--pipe" when i + 1 < argv.Length: pipe = argv[++i]; break;
                case "--parent" when i + 1 < argv.Length: int.TryParse(argv[++i], out parent); break;
                case "--idle-minutes" when i + 1 < argv.Length: int.TryParse(argv[++i], out idle); break;
                case "--dev-unelevated": dev = true; break;
            }
        }
        if (pipe is null || !HelperJson.IsValidPipeName(pipe) || parent <= 0) return null;
        return new Args(pipe, parent, Math.Clamp(idle, 1, 240), dev);
    }

    public static async Task<int> RunAsync(string[] argv)
    {
        var args = ParseArgs(argv);
        if (args is null) return 2;

        var log = new HelperLog();
        var elevated = Native.IsElevated();
        if (!elevated && !args.DevUnelevated)
        {
            log.Write("refusing: not elevated");
            return 3;
        }

        await using var pipe = new NamedPipeClientStream(".", args.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(15_000);
        }
        catch (TimeoutException)
        {
            log.Write("pipe connect timed out");
            return 4;
        }

        // 1) The pipe's server must be the process that launched us...
        var serverPid = Native.PipeServerPid(pipe.SafePipeHandle);
        if (serverPid != args.ParentPid)
        {
            log.Write($"refusing: pipe server pid {serverPid} != parent {args.ParentPid}");
            return 5;
        }
        // 2) ...and that process must be CarsControl.exe from our own (admin-only-writable) folder.
        var expected = Path.Combine(AppContext.BaseDirectory, AppExeName);
        var actual = Native.ImagePath(args.ParentPid);
        if (!string.Equals(Path.GetFullPath(expected), actual is null ? null : Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"refusing: parent image '{actual}' is not '{expected}'");
            return 6;
        }

        var parentUser = Native.ProcessUser(args.ParentPid);
        var parentProfile = parentUser is null ? null : Native.ProfilePath(parentUser);
        var ops = new HelperOperations(log, parentProfile, devUnelevated: args.DevUnelevated, parentSid: parentUser?.Value);
        log.Write($"connected: parent {args.ParentPid} ({actual}), parent profile {parentProfile}, elevated={elevated}, dev={args.DevUnelevated}");

        using var lifetime = new CancellationTokenSource();
        _ = WatchParentAsync(args.ParentPid, lifetime);

        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(pipe, utf8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1);

        async Task Send(HelperEvent ev)
        {
            await writeLock.WaitAsync();
            try { await writer.WriteLineAsync(HelperJson.Serialize(ev)); }
            catch (IOException) { lifetime.Cancel(); }
            finally { writeLock.Release(); }
        }

        while (!lifetime.IsCancellationRequested)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                idle.CancelAfter(TimeSpan.FromMinutes(args.IdleMinutes));
                try
                {
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException)
                {
                    log.Write(lifetime.IsCancellationRequested ? "parent exited" : $"idle for {args.IdleMinutes} min, exiting");
                    break;
                }
                catch (IOException)
                {
                    break;
                }
            }
            if (line is null) break; // app closed the pipe

            HelperRequest? req;
            try
            {
                req = HelperJson.Deserialize<HelperRequest>(line);
            }
            catch (Exception ex)
            {
                log.Write($"bad frame: {ex.Message}");
                continue;
            }
            if (req is null) continue;

            log.Write($"op {req.Id}: {req.Op.GetType().Name} dryRun={req.DryRun}");
            OpResult result;
            try
            {
                result = await ops.ExecuteAsync(
                    req.Op,
                    req.DryRun,
                    text => _ = Send(new HelperEvent(req.Id, HelperEvent.Line, text)),
                    lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                result = new OpResult(OpStatus.Cancelled, -1, "Cancelled");
            }
            catch (Exception ex)
            {
                result = new OpResult(OpStatus.Failed, -1, ex.Message);
            }
            log.Write($"op {req.Id} -> {result.Status} {result.ExitCode} {result.Message}");
            await Send(new HelperEvent(req.Id, HelperEvent.Done, Result: result));
        }

        if (elevated) StorageOps.CleanupExports(); // drive listings don't outlive the session
        Health.LhmSensors.Close();
        log.Write("helper exiting");
        return 0;
    }

    private static async Task WatchParentAsync(int pid, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = System.Diagnostics.Process.GetProcessById(pid);
            await parent.WaitForExitAsync();
        }
        catch
        {
            // Parent already gone.
        }
        lifetime.Cancel();
    }
}

/// <summary>Helper log: the secured ProgramData\CarsControl\logs when elevated, %TEMP% otherwise.
/// An unelevated helper must never create the ProgramData folder: you would then own it.</summary>
public sealed class HelperLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public HelperLog()
    {
        string dir;
        if (Native.IsElevated())
        {
            dir = SecureDataDir.Ensure("logs");
        }
        else
        {
            dir = Path.Combine(Path.GetTempPath(), "CarsControl", "logs");
            Directory.CreateDirectory(dir);
        }
        LogDirectory = dir;
        _path = Path.Combine(dir, $"helper-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogDirectory { get; }

    public void Write(string message)
    {
        lock (_gate)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}"); }
            catch { /* logging must never break an operation */ }
        }
    }
}
