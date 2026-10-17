using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CarsControl.Contracts;

namespace CarsControl.Core.Elevation;

public sealed class HelperCancelledException() : Exception("Admin approval was cancelled.");

public sealed class HelperUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Unattended work asked for the admin helper but no approved session is open. Nothing was
/// shown to anyone: the work waits for approval instead of putting a UAC prompt on an empty room.</summary>
public sealed class HelperSessionClosedException() : Exception("No admin session is open.");

/// <summary>Sends allowlisted operations to the elevated helper. Implemented by <see cref="HelperClient"/>;
/// tests substitute a fake.</summary>
public interface IHelperChannel
{
    bool IsConnected { get; }
    Task<OpResult> SendAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default);
}

/// <summary>
/// App side of the helper connection. The first admin operation launches CarsControl.Helper.exe with
/// "runas" (one UAC prompt asking for an administrator's credentials); later operations reuse the same helper
/// until it idles out or the app exits. The pipe is created here, by the unelevated app, with an ACL
/// that only admits you and Administrators, under a random per-session name.
/// </summary>
public sealed class HelperClient : IHelperChannel, IAsyncDisposable
{
    public const string HelperExeName = "CarsControl.Helper.exe";

    /// <summary>Set CARS_HELPER_UNELEVATED=1 to launch the helper without UAC; it then only accepts dry runs.</summary>
    public static bool DevUnelevated => Environment.GetEnvironmentVariable("CARS_HELPER_UNELEVATED") == "1";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _helper;
    private int _nextId;

    public int IdleMinutes { get; set; } = 15;
    public string HelperPath { get; init; } = Path.Combine(AppContext.BaseDirectory, HelperExeName);
    public HelperHello? Hello { get; private set; }

    public bool IsConnected => _pipe is { IsConnected: true } && _helper is { HasExited: false };

    /// <summary>Raised when the helper starts or goes away (UI shows an "admin session active" chip).</summary>
    public event Action<bool>? ConnectionChanged;

    public async Task<OpResult> SendAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            try
            {
                return await SendCoreAsync(op, onLine, dryRun, ct);
            }
            catch (IOException ex)
            {
                Teardown();
                throw new HelperUnavailableException("The admin helper disconnected.", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Like <see cref="SendAsync"/>, but only over a session that's already open: it never starts
    /// the helper, so it never shows a UAC prompt. Throws <see cref="HelperSessionClosedException"/> otherwise.</summary>
    public async Task<OpResult> SendIfConnectedAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!IsConnected) throw new HelperSessionClosedException();
            try
            {
                return await SendCoreAsync(op, onLine, dryRun, ct);
            }
            catch (IOException ex)
            {
                Teardown();
                throw new HelperUnavailableException("The admin helper disconnected.", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Keeps an approved session from idling out (the helper exits after <see cref="IdleMinutes"/>
    /// without a request). Does nothing when no session is open.</summary>
    public async Task<bool> PingIfConnectedAsync()
    {
        try
        {
            return (await SendIfConnectedAsync(new PingOp())).Ok;
        }
        catch (Exception ex) when (ex is HelperSessionClosedException or HelperUnavailableException)
        {
            return false;
        }
    }

    /// <summary>A channel for unattended work: uses the open session, never prompts.</summary>
    public IHelperChannel ExistingSessionOnly => new ExistingSession(this);

    private sealed class ExistingSession(HelperClient client) : IHelperChannel
    {
        public bool IsConnected => client.IsConnected;

        public Task<OpResult> SendAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default) =>
            client.SendIfConnectedAsync(op, onLine, dryRun, ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        Teardown();

        if (!File.Exists(HelperPath)) throw new HelperUnavailableException($"{HelperExeName} is missing next to the app.");

        var name = "CarsControl." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        _pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        var dev = DevUnelevated;
        var psi = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = true,
            Verb = dev ? "" : "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--pipe {name} --parent {Environment.ProcessId} --idle-minutes {IdleMinutes}" + (dev ? " --dev-unelevated" : ""),
        };

        try
        {
            _helper = Process.Start(psi) ?? throw new HelperUnavailableException("The admin helper did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Teardown();
            throw new HelperCancelledException();
        }
        catch (Win32Exception ex)
        {
            Teardown();
            throw new HelperUnavailableException($"Could not start the admin helper: {ex.Message}", ex);
        }

        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await _pipe.WaitForConnectionAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var code = _helper.HasExited ? _helper.ExitCode : (int?)null;
                Teardown();
                throw new HelperUnavailableException($"The admin helper didn't connect{(code is null ? "" : $" (exit code {code})")}.");
            }
        }

        // The client on the other end must be the process we just launched.
        var clientPid = Native.PipeClientPid(_pipe.SafePipeHandle);
        if (clientPid != _helper.Id)
        {
            Teardown();
            throw new HelperUnavailableException($"Unexpected process {clientPid} connected to the admin pipe.");
        }

        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(_pipe, utf8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true };

        var pong = await SendCoreAsync(new PingOp(), null, false, ct);
        Hello = pong.Data is null ? null : HelperJson.Deserialize<HelperHello>(pong.Data);
        if (Hello is null || (!Hello.Elevated && !dev))
        {
            Teardown();
            throw new HelperUnavailableException("The admin helper is not running elevated.");
        }

        _helper.EnableRaisingEvents = true;
        _helper.Exited += (_, _) => ConnectionChanged?.Invoke(false);
        ConnectionChanged?.Invoke(true);
    }

    private async Task<OpResult> SendCoreAsync(HelperOp op, Action<string>? onLine, bool dryRun, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        await _writer!.WriteLineAsync(HelperJson.Serialize(new HelperRequest(id, op, dryRun)).AsMemory(), ct);
        while (true)
        {
            var line = await _reader!.ReadLineAsync(ct) ?? throw new IOException("Helper closed the pipe.");
            var ev = HelperJson.Deserialize<HelperEvent>(line);
            if (ev is null || ev.Id != id) continue;
            if (ev.Kind == HelperEvent.Done) return ev.Result ?? new OpResult(OpStatus.Failed, -1, "Empty result");
            if (ev.Text is not null) onLine?.Invoke(ev.Text);
        }
    }

    private void Teardown()
    {
        var wasConnected = _helper is { HasExited: false };
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
        _helper?.Dispose();
        _helper = null;
        Hello = null;
        if (wasConnected) ConnectionChanged?.Invoke(false);
    }

    public ValueTask DisposeAsync()
    {
        Teardown(); // closing the pipe makes the helper exit
        return ValueTask.CompletedTask;
    }
}
