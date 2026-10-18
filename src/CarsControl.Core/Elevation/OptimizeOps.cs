using System.Diagnostics;
using System.Security.Cryptography;
using CarsControl.Contracts;
using CarsControl.Core.Optimize;
using CarsControl.Core.Util;

namespace CarsControl.Core.Elevation;

/// <summary>Intel PresentMon, fetched on demand for "Test it on this PC" (MIT licence; not shipped in the installer).</summary>
public static class PresentMon
{
    public const string Version = "2.6.0";
    public const string DownloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    public const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const long Size = 980_320;
    public const string SessionName = "CarsControlFrameTest";
}

/// <summary>
/// The Optimization page's frame test, inside the helper (reading frame times needs Windows' graphics event tracing, which is
/// admin-only). Takes only a process id and a duration; PresentMon is the pinned release, checked against its SHA-256 before every
/// run, kept in ProgramData\CarsControl\tools (admins write, users read). The CSV never leaves the helper: only the summary does.
/// </summary>
internal static class OptimizeOps
{
    public const int MinSeconds = 10, MaxSeconds = 120;

    public static async Task<OpResult> CaptureAsync(FrameCaptureOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (op.Seconds is < MinSeconds or > MaxSeconds) return OpResult.Reject($"A frame test lasts {MinSeconds}–{MaxSeconds} seconds.");
        if (op.ProcessId <= 4) return OpResult.Reject("That isn't a game's process.");
        string name;
        try
        {
            using var p = Process.GetProcessById(op.ProcessId);
            if (p.SessionId == 0) return OpResult.Reject("That's a Windows service, not a game.");
            name = p.ProcessName;
        }
        catch (ArgumentException)
        {
            return OpResult.Reject("The game isn't running any more.");
        }
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would record {op.Seconds} s of frame times from {name} (PID {op.ProcessId}) with PresentMon {PresentMon.Version}.");

        var exe = await EnsurePresentMonAsync(line, log, ct);
        if (exe is null) return new OpResult(OpStatus.Failed, -1, "PresentMon couldn't be downloaded or didn't match its SHA-256; nothing was recorded.");

        var csv = Path.Combine(SecureDataDir.Ensure("captures"), $"frames-{Guid.NewGuid():N}.csv");
        try
        {
            line($"Recording {name} for {op.Seconds} seconds…");
            log.Write($"frame capture: {name} pid {op.ProcessId}, {op.Seconds} s");
            string[] args =
            [
                "--process_id", op.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--output_file", csv,
                "--timed", op.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--terminate_after_timed", "--terminate_on_proc_exit", "--no_console_stats",
                "--session_name", PresentMon.SessionName, "--stop_existing_session",
            ];
            var r = await ProcessRunner.RunAsync(exe, args, null, TimeSpan.FromSeconds(op.Seconds + 45), ct);
            if (!File.Exists(csv)) return new OpResult(OpStatus.Failed, r.ExitCode, $"PresentMon recorded nothing (exit {r.ExitCode}): {r.Lines.LastOrDefault()}");
            var summary = FrameStats.Summarize(FrameStats.ParseCsv(await File.ReadAllLinesAsync(csv, ct), op.ProcessId));
            if (summary is null) return new OpResult(OpStatus.Failed, 0, "Too few frames were recorded. Stay in the game (not a menu or loading screen) for the whole time.");
            log.Write($"frame capture: {summary.Describe()}");
            return OpResult.Success(summary.Describe(), HelperJson.Serialize(summary));
        }
        finally
        {
            try { File.Delete(csv); } catch (IOException) { /* cleared next time */ }
        }
    }

    /// <summary>The verified PresentMon executable, downloading it the first time. Null when it can't be had safely.</summary>
    private static async Task<string?> EnsurePresentMonAsync(Action<string> line, HelperLog log, CancellationToken ct)
    {
        var exe = Path.Combine(SecureDataDir.Ensure("tools"), $"PresentMon-{PresentMon.Version}-x64.exe");
        if (File.Exists(exe) && await HashMatchesAsync(exe, ct)) return exe;

        line($"Downloading PresentMon {PresentMon.Version} (about 1 MB, once)…");
        var temp = exe + ".download";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CarsControl-Helper");
                using var response = await http.GetAsync(PresentMon.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } len && len != PresentMon.Size) return null;
                await using var net = await response.Content.ReadAsStreamAsync(ct);
                await using var disk = File.Create(temp);
                await net.CopyToAsync(disk, ct);
            }
            if (!await HashMatchesAsync(temp, ct))
            {
                log.Write("presentmon: hash mismatch, discarded");
                return null;
            }
            File.Move(temp, exe, overwrite: true);
            return exe;
        }
        catch (HttpRequestException ex)
        {
            log.Write($"presentmon download failed: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Write("presentmon download timed out");
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* gone */ }
        }
    }

    private static async Task<bool> HashMatchesAsync(string file, CancellationToken ct) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct))).Equals(PresentMon.Sha256, StringComparison.OrdinalIgnoreCase);
}
