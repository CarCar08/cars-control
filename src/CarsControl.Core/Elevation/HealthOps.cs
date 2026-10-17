using System.Security.Cryptography;
using CarsControl.Contracts;
using CarsControl.Core.Health;
using CarsControl.Core.Updates;
using CarsControl.Core.Util;
using Microsoft.Win32;

namespace CarsControl.Core.Elevation;

/// <summary>
/// The Health page's admin parts, inside the helper. Reads (drive counters, power requests, boot log, sensors)
/// return their data; the few changes (delete an old driver package, Windows Update settings, hide an update,
/// turn on System Protection, install PawnIO) accept only what's validated below, and run fixed programs.
/// </summary>
internal static class HealthOps
{
    private static string Pnputil => Path.Combine(Tools.System32, "pnputil.exe");
    private static string Powercfg => Path.Combine(Tools.System32, "powercfg.exe");

    public static async Task<OpResult> ExecuteAsync(HelperOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct, string? parentSid = null)
    {
        try
        {
            return op switch
            {
                DriveHealthOp => await ReadDrivesAsync(dryRun),
                PowerRequestsOp => await PowerRequestsAsync(dryRun, line, ct),
                BootPerformanceOp b => await BootTimesAsync(b, dryRun, ct),
                DriverDeleteOp d => await DeleteDriverAsync(d, dryRun, line, log, ct),
                WindowsUpdatePolicyOp p => ApplyPolicy(p, dryRun, log),
                HideUpdateOp h => await HideUpdateAsync(h, dryRun, log, ct),
                EnableSystemRestoreOp => await EnableRestoreAsync(dryRun, line, log, ct),
                SensorsReadOp => await ReadSensorsAsync(dryRun, log),
                InstallPawnIoOp => await InstallPawnIoAsync(dryRun, line, log, ct),
                CopyCrashDumpOp c => await CopyDumpAsync(c, dryRun, log, parentSid, ct),
                _ => OpResult.Reject("Not a health operation."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException
            or System.Management.ManagementException or HttpRequestException)
        {
            log.Write($"health op {op.GetType().Name} failed: {ex.Message}");
            return new OpResult(OpStatus.Failed, -1, ex.Message);
        }
    }

    // ---------- drives ----------

    private static Task<OpResult> ReadDrivesAsync(bool dryRun) =>
        dryRun
            ? Task.FromResult(new OpResult(OpStatus.DryRun, 0, "Would read each drive's temperature, wear, hours and error counters."))
            : Task.Run(() =>
            {
                var drives = DriveHealthReader.Read(includeCounters: true);
                return OpResult.Success($"Read {drives.Count} drives.", HelperJson.Serialize(drives));
            });

    // ---------- power requests ----------

    private static async Task<OpResult> PowerRequestsAsync(bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(Powercfg, ["/requests"]));
        var r = await ProcessRunner.RunAsync(Powercfg, ["/requests"], null, TimeSpan.FromSeconds(30), ct);
        if (r.TimedOut || r.ExitCode != 0) return new OpResult(OpStatus.Failed, r.ExitCode, $"powercfg failed: {r.Lines.LastOrDefault()}");
        return OpResult.Success("Read.", string.Join('\n', r.Lines));
    }

    // ---------- boot times ----------

    private static async Task<OpResult> BootTimesAsync(BootPerformanceOp op, bool dryRun, CancellationToken ct)
    {
        var days = Math.Clamp(op.Days, 1, 90);
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would read boot and shutdown times from {BootPerformance.Log} for the last {days} days.");
        var events = await WinEvents.ReadAsync(BootPerformance.Log, BootPerformance.Query(DateTime.UtcNow.AddDays(-days)), 600, ct);
        var report = BootPerformance.Build(events);
        return OpResult.Success($"{report.Boots.Count} boots, {report.Shutdowns.Count} shutdowns.", HelperJson.Serialize(report));
    }

    // ---------- driver store ----------

    private static async Task<OpResult> DeleteDriverAsync(DriverDeleteOp op, bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (!DriverStore.PublishedNamePattern.IsMatch(op.PublishedName)) return OpResult.Reject($"'{op.PublishedName}' isn't a published driver name (oemNN.inf).");
        var args = new[] { "/delete-driver", op.PublishedName.ToLowerInvariant() };
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, HelperOperations.Describe(Pnputil, args));

        // Decide again here, from Windows' own data: only an older copy of a driver that no device uses.
        var listing = await ProcessRunner.RunAsync(Pnputil, ["/enum-drivers"], null, TimeSpan.FromSeconds(60), ct);
        var (_, inUse) = await Task.Run(DriverStore.ReadInstalled, ct);
        var all = DriverStore.Parse(listing.Lines, inUse);
        var target = all.FirstOrDefault(p => string.Equals(p.PublishedName, op.PublishedName, StringComparison.OrdinalIgnoreCase));
        if (target is null) return new OpResult(OpStatus.NotFound, -1, $"{op.PublishedName} isn't in the driver store.");
        if (target.InUse) return OpResult.Reject($"{op.PublishedName} is in use by a device.");
        if (!DriverStore.Superseded(all).Any(p => string.Equals(p.PublishedName, target.PublishedName, StringComparison.OrdinalIgnoreCase)))
            return OpResult.Reject($"{op.PublishedName} is the newest copy of {target.OriginalName}, not an old one.");

        // No /uninstall and no /force: if Windows still needs it, pnputil refuses.
        var r = await ProcessRunner.RunAsync(Pnputil, args, line, TimeSpan.FromMinutes(2), ct);
        log.Write($"driver delete {target} ({op.PublishedName}): exit {r.ExitCode}");
        return r.ExitCode == 0
            ? OpResult.Success($"Removed {target}.")
            : new OpResult(OpStatus.Failed, r.ExitCode, $"Windows kept it: {r.Lines.LastOrDefault()}");
    }

    // ---------- Windows Update settings ----------

    private static OpResult ApplyPolicy(WindowsUpdatePolicyOp op, bool dryRun, HelperLog log)
    {
        var plan = WindowsUpdatePolicy.Plan(op.Setting, op.Value, DateTime.UtcNow);
        if (!plan.Valid) return OpResult.Reject(plan.Error ?? "Not allowed.");

        using var ux = Registry.LocalMachine.OpenSubKey(WindowsUpdatePolicy.UxSettings);
        if (op.Setting == "pause-until" && ux?.GetValue(WindowsUpdatePolicy.GamingMarker) is not null)
            return OpResult.Reject("Gaming mode is pausing Windows Update right now; change it after you stop playing.");
        if (dryRun)
            return new OpResult(OpStatus.DryRun, 0, string.Join(Environment.NewLine, plan.Writes.Select(w => w.Value is null ? $@"delete HKLM\{w.SubKey}\{w.Name}" : $@"set HKLM\{w.SubKey}\{w.Name} = {w.Value}")));

        foreach (var w in plan.Writes)
        {
            using var key = Registry.LocalMachine.CreateSubKey(w.SubKey, writable: true);
            if (w.Value is null) key.DeleteValue(w.Name, throwOnMissingValue: false);
            else key.SetValue(w.Name, w.Value, w.Kind);
        }
        log.Write($"windows update setting {op.Setting} = {op.Value}");
        return OpResult.Success("Done.");
    }

    private static async Task<OpResult> HideUpdateAsync(HideUpdateOp op, bool dryRun, HelperLog log, CancellationToken ct)
    {
        if (!WindowsUpdateApi.IsValidUpdateId(op.UpdateId)) return OpResult.Reject("Invalid Windows Update id.");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would {(op.Hide ? "hide" : "show")} update {op.UpdateId}.");
        var found = await WindowsUpdateApi.SetHiddenAsync(op.UpdateId, op.Hide, ct);
        if (!found) return new OpResult(OpStatus.NotFound, -1, "Windows Update doesn't list that update any more.");
        log.Write($"update {op.UpdateId} {(op.Hide ? "hidden" : "shown")}");
        return OpResult.Success(op.Hide ? "Hidden: Windows won't offer it again." : "Visible again.");
    }

    // ---------- System Protection ----------

    private static Task<OpResult> EnableRestoreAsync(bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        // Fixed script, C: only; nothing from the caller goes into it.
        const string script = "$ErrorActionPreference='Stop'; Enable-ComputerRestore -Drive 'C:\\'; 'System Protection is on for C:.'";
        log.Write("enable system restore on C:");
        return HelperOperations.RunFixedAsync(Tools.WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], "System Protection", dryRun, line, ct);
    }

    // ---------- sensors ----------

    private static Task<OpResult> ReadSensorsAsync(bool dryRun, HelperLog log)
    {
        if (dryRun) return Task.FromResult(new OpResult(OpStatus.DryRun, 0, "Would read CPU, board and fan sensors with LibreHardwareMonitor."));
        if (!PawnIo.IsInstalled()) return Task.FromResult(new OpResult(OpStatus.Failed, -1, "The PawnIO driver isn't installed, so CPU and board sensors can't be read.", Data: "pawnio-missing"));
        return Task.Run(() =>
        {
            var sensors = LhmSensors.Read();
            log.Write($"sensors: {sensors.Count} readings");
            return OpResult.Success($"{sensors.Count} sensors.", HelperJson.Serialize(sensors));
        });
    }

    // ---------- crash dumps ----------

    /// <summary>Copies one minidump so the debugger can read it as the user. Only a Windows minidump name, only from
    /// %SystemRoot%\Minidump, never a link, never anything big; the copy goes to a folder only the user and admins can read.</summary>
    private static async Task<OpResult> CopyDumpAsync(CopyCrashDumpOp op, bool dryRun, HelperLog log, string? parentSid, CancellationToken ct)
    {
        if (!DumpFiles.IsValidName(op.FileName)) return OpResult.Reject($"'{op.FileName}' isn't a Windows minidump name.");
        var source = Path.Combine(DumpFiles.MinidumpFolder, op.FileName);
        var info = new FileInfo(source);
        if (!info.Exists) return new OpResult(OpStatus.NotFound, -1, $"{op.FileName} isn't in the crash dump folder any more.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return OpResult.Reject("That dump is a link, not a file.");
        if (info.Length > DumpFiles.MaxCopyBytes) return OpResult.Reject("That file is too big to be a minidump.");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"Would copy {source} to ProgramData\\CarsControl\\dumps (readable by you and administrators).");

        var dir = SecureDataDir.EnsurePrivate("dumps", parentSid);
        foreach (var old in Directory.EnumerateFiles(dir, "*.dmp").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)))
            try { File.Delete(old); } catch (IOException) { /* in use: next time */ }
        var target = Path.Combine(dir, op.FileName);
        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            await src.CopyToAsync(dst, ct);
        log.Write($"crash dump {op.FileName} copied for analysis");
        return OpResult.Success("Copied.", target);
    }

    // ---------- PawnIO ----------

    private static async Task<OpResult> InstallPawnIoAsync(bool dryRun, Action<string> line, HelperLog log, CancellationToken ct)
    {
        if (dryRun)
            return new OpResult(OpStatus.DryRun, 0, $"Would download {PawnIo.DownloadUrl} ({PawnIo.Size / 1024} KB), check it is SHA-256 {PawnIo.Sha256}, then run it with {string.Join(' ', PawnIo.SilentArgs)}.");
        if (PawnIo.IsInstalled()) return OpResult.Success("PawnIO is already installed.", PawnIo.InstalledVersion());

        var dir = SecureDataDir.Ensure("downloads");
        var file = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            line("Downloading PawnIO…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CarsControl-Helper");
                using var response = await http.GetAsync(PawnIo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } len && len != PawnIo.Size) return new OpResult(OpStatus.Failed, -1, "The download isn't the expected size; nothing was installed.");
                await using var net = await response.Content.ReadAsStreamAsync(ct);
                await using var disk = File.Create(file);
                await net.CopyToAsync(disk, ct);
            }

            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct))).ToLowerInvariant();
            if (hash != PawnIo.Sha256)
            {
                log.Write($"pawnio: hash mismatch ({hash})");
                return new OpResult(OpStatus.Failed, -1, "The download doesn't match the SHA-256 this C.A.R.S. was built with; nothing was installed.");
            }

            line("Checked: it's the official PawnIO 2.2.0. Installing…");
            var r = await ProcessRunner.RunAsync(file, PawnIo.SilentArgs, line, TimeSpan.FromMinutes(3), ct);
            log.Write($"pawnio install: exit {r.ExitCode}, installed={PawnIo.IsInstalled()}");
            // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED.
            if (r.ExitCode is 0 or 3010 && PawnIo.IsInstalled())
                return new OpResult(OpStatus.Succeeded, 0, r.ExitCode == 3010 ? "PawnIO installed; restart to finish." : "PawnIO installed.", RebootRequired: r.ExitCode == 3010);
            return new OpResult(OpStatus.Failed, r.ExitCode, $"PawnIO's installer didn't finish (exit {r.ExitCode}): {r.Lines.LastOrDefault()}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* in use: cleared next time */ }
        }
    }
}
