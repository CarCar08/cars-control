using System.Diagnostics;
using System.IO;

namespace CarsControl.App.Services;

/// <summary>Timing log at %LOCALAPPDATA%\CarsControl\logs\perf.log, so speed claims can be checked
/// with numbers: startup phases, inventory, update checks, scans and page switches.</summary>
public static class PerfLog
{
    private static readonly object Gate = new();
    private static readonly string Path = App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_PERF_LOG") is { } devPath && System.IO.Path.IsPathFullyQualified(devPath)
        ? devPath : System.IO.Path.Combine(Core.Util.UserData.Root, "logs", "perf.log");
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    static PerfLog()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 2_000_000) File.Delete(Path);
            Write($"---- session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} {(App.IsDevInstance ? "(dev)" : "")}");
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    /// <summary>Milliseconds since the process started, labelled.</summary>
    public static void Mark(string what) => Write($"{(DateTime.Now - ProcessStart).TotalMilliseconds,8:F0} ms since start  {what}");

    /// <summary>using (PerfLog.Time("inventory")) { … } logs the duration.</summary>
    public static IDisposable Time(string what) => new Timer(what);

    public static void Duration(string what, TimeSpan elapsed) => Write($"{elapsed.TotalMilliseconds,8:F0} ms  {what}");
    public static void Memory(string what)
    {
        using var process = Process.GetCurrentProcess();
        Write($"memory {what}: working={process.WorkingSet64 / 1048576.0:F1} MiB, private={process.PrivateMemorySize64 / 1048576.0:F1} MiB, managed={GC.GetTotalMemory(false) / 1048576.0:F1} MiB, heap={GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0:F1} MiB, gen2={GC.CollectionCount(2)}");
    }

    private static void Write(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}"); }
            catch { /* ignore */ }
        }
    }

    private sealed class Timer(string what) : IDisposable
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public void Dispose() => Duration(what, _sw.Elapsed);
    }
}
