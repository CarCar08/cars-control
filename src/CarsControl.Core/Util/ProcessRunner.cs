using System.Diagnostics;
using System.Text;

namespace CarsControl.Core.Util;

/// <summary>Lines = stdout and stderr interleaved (for logs). Stdout = stdout only, raw (for parsing JSON).</summary>
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Lines, bool TimedOut, string Stdout = "")
{
    public string Tail(int n = 12) => string.Join(Environment.NewLine, Lines.TakeLast(n));
}

/// <summary>Runs a console tool without a window and streams its output line by line. Progress
/// redraws (\r) and winget's block-character progress bars are collapsed so logs stay readable.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.SystemDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var lines = new List<string>();
        var stdout = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Handle(string? data)
        {
            if (data is null) return;
            foreach (var piece in data.Split('\r'))
            {
                var clean = Clean(piece);
                if (clean.Length == 0) continue;
                lock (lines) lines.Add(clean);
                onLine?.Invoke(clean);
            }
        }
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data);
            Handle(e.Data);
        };
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) timeoutCts.CancelAfter(t);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (!timedOut) throw;
        }

        proc.WaitForExit(); // flush async output
        return new ProcessResult(timedOut ? -1 : proc.ExitCode, lines, timedOut, stdout.ToString());
    }

    /// <summary>Drops spinner/progress-bar noise: lines that are only block chars, slashes or percentages.</summary>
    internal static string Clean(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return "";
        if (t.All(c => c is '█' or '▒' or '░' or '-' or '\\' or '|' or '/' or ' ')) return "";
        // "████▒▒▒  12.3 MB / 40.1 MB" → keep just the numbers
        var firstText = t.TakeWhile(c => c is '█' or '▒' or '░' or ' ').Count();
        return firstText > 0 && firstText < t.Length ? t[firstText..].Trim() : t;
    }
}
