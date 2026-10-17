using System.Diagnostics;
using System.Text.RegularExpressions;
using CarsControl.Contracts;
using CarsControl.Core.Updates;
using CarsControl.Core.Util;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Elevation;

/// <summary>
/// The helper's allowlist. Every operation validates its identifiers, resolves the program to run
/// from fixed locations or the registry, and never executes caller-supplied text. In dry-run mode it
/// returns the exact command it would have run. In dev-unelevated mode only Ping and dry runs are
/// allowed, so the pipe protocol can be tested without admin rights.
/// </summary>
public sealed partial class HelperOperations(HelperLog log, string? parentProfile, bool devUnelevated = false, string? parentSid = null)
{
    private readonly UninstallOps _uninstall = new(log, parentProfile, parentSid);

    public delegate Task<OpResult> Handler(HelperOp op, bool dryRun, Action<string> line, CancellationToken ct);

    private string LogFile(string name) =>
        Path.Combine(log.LogDirectory, $"{Regex.Replace(name, @"[^A-Za-z0-9._-]", "_")}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public async Task<OpResult> ExecuteAsync(HelperOp op, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (devUnelevated && !dryRun && op is not PingOp)
            return OpResult.Reject("Dev mode (unelevated helper): only dry runs are allowed.");

        return op switch
        {
            PingOp => Ping(),
            WingetUpgradeOp u => await WingetAsync(u.Id, u.Version, upgrade: true, dryRun, line, ct),
            WingetInstallOp i => await WingetInstallAsync(i, dryRun, line, ct),
            StoreUpdateOp => await StoreUpdateAsync(dryRun, line, ct),
            WindowsUpdateInstallOp w => await WindowsUpdateAsync(w, dryRun, line, ct),
            WslUpdateOp => await RunFixedAsync(Tools.Wsl, ["--update"], "WSL", dryRun, line, ct),
            PipUpgradeOp p => await PipAsync(p.Package, dryRun, line, ct),
            CreateRestorePointOp r => await RestorePointAsync(r.Description, dryRun, line, ct),
            ArpUninstallOp or WingetUninstallOp or LeftoverScanOp or QuarantineOp or RestoreQuarantineOp
                => await _uninstall.ExecuteAsync(op, dryRun, line, ct),
            CleanupRunOp or CleanupEstimateOp => await CleanupOps.ExecuteAsync(op, dryRun, line, log, ct),
            StorageScanOp s => await StorageOps.ScanAsync(s, dryRun, line, log, parentSid, ct),
            WindowsUpdatePauseOp or PurgeStandbyListOp or ServiceControlOp => GamingOps.Execute(op, dryRun, line, log),
            TweakOp t => TweakOps.Execute(t, dryRun, log, parentSid),
            RemoveOldWindowsOp r => await OldWindowsOps.ExecuteAsync(r, dryRun, line, log, ct),
            RemoveMovedGameOp g => await GameMoveOps.RemoveOldAsync(g, dryRun, line, log, ct),
            StartupEntryOp or ServiceStartTypeOp or ScheduledTaskToggleOp => await StartupOps.ExecuteAsync(op, dryRun, line, log, ct),
            DriveHealthOp or PowerRequestsOp or BootPerformanceOp or DriverDeleteOp or WindowsUpdatePolicyOp or HideUpdateOp or EnableSystemRestoreOp or SensorsReadOp or InstallPawnIoOp or CopyCrashDumpOp
                => await HealthOps.ExecuteAsync(op, dryRun, line, log, ct, parentSid),
            _ => OpResult.Reject($"Unknown operation {op.GetType().Name}"),
        };
    }

    private static OpResult Ping()
    {
        var hello = new HelperHello(
            typeof(HelperOperations).Assembly.GetName().Version?.ToString() ?? "0",
            Environment.UserName,
            Native.IsElevated(),
            Environment.ProcessId);
        return OpResult.Success("pong", HelperJson.Serialize(hello));
    }

    private async Task<OpResult> WingetAsync(string id, string? version, bool upgrade, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (Inventory.ProtectedApps.IsAlwaysProtectedName(id)) return OpResult.Reject("Salad is permanently protected and cannot be changed.");
        if (!WingetCli.IsValidId(id)) return OpResult.Reject($"Invalid winget id '{id}'.");
        if (version is not null && !WingetCli.IsValidVersion(version)) return OpResult.Reject($"Invalid version '{version}'.");

        var winget = WingetCli.Locate(parentProfile);
        if (winget is null) return new OpResult(OpStatus.Failed, -1, "winget.exe was not found for the admin account.");

        var logPath = LogFile(id);
        var args = upgrade ? WingetCli.UpgradeArgs(id, version, logPath) : WingetCli.UninstallArgs(id, logPath);
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, Describe(winget, args));

        var (outcome, raw) = await WingetCli.RunAsync(winget, args, line, ct);
        var status = outcome.Ok ? OpStatus.Succeeded
            : outcome.Failure == WingetFailure.NotFoundForAccount ? OpStatus.NotFound
            : OpStatus.Failed;
        return new OpResult(status, raw.ExitCode, outcome.Message, outcome.RebootRequired, logPath);
    }

    private async Task<OpResult> WingetInstallAsync(WingetInstallOp op, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (Inventory.ProtectedApps.IsAlwaysProtectedName(op.Id)) return OpResult.Reject("Salad is permanently protected and cannot be changed.");
        if (!WingetCli.IsValidId(op.Id)) return OpResult.Reject($"Invalid winget id '{op.Id}'.");
        if (!WingetCli.IsValidSource(op.Source)) return OpResult.Reject($"Invalid source '{op.Source}'.");
        if (op.Version is not null && !WingetCli.IsValidVersion(op.Version)) return OpResult.Reject($"Invalid version '{op.Version}'.");

        var winget = WingetCli.Locate(parentProfile);
        if (winget is null) return new OpResult(OpStatus.Failed, -1, "winget.exe was not found for the admin account.");

        var logPath = LogFile("install-" + op.Id);
        var args = WingetCli.InstallArgs(op.Id, op.Source, "machine", op.Version, logPath);
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, Describe(winget, args));

        var (outcome, raw) = await WingetCli.RunAsync(winget, args, line, ct);
        var status = outcome.Ok ? OpStatus.Succeeded : outcome.Failure == WingetFailure.NotFoundForAccount ? OpStatus.NotFound : OpStatus.Failed;
        return new OpResult(status, raw.ExitCode, outcome.Message, outcome.RebootRequired, logPath);
    }

    private static Task<OpResult> StoreUpdateAsync(bool dryRun, Action<string> line, CancellationToken ct)
    {
        // The MDM bridge's UpdateScanMethod makes the Store scan for and install all app updates.
        // It fails from your account ("Access to a CIM resource was not available"), hence the helper.
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "Get-CimInstance -Namespace 'root\\cimv2\\mdm\\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' | " +
            "Invoke-CimMethod -MethodName UpdateScanMethod | Out-Null; 'Store update scan started.'";
        return RunFixedAsync(Tools.WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], "Store", dryRun, line, ct);
    }

    private static async Task<OpResult> WindowsUpdateAsync(WindowsUpdateInstallOp w, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (w.UpdateIds.Count == 0 || w.UpdateIds.Any(id => !WindowsUpdateApi.IsValidUpdateId(id)))
            return OpResult.Reject("Invalid Windows Update id.");
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, $"WUA: download + install {w.UpdateIds.Count} update(s): {string.Join(", ", w.UpdateIds)}");

        var r = await WindowsUpdateApi.InstallAsync(w.UpdateIds, line, ct);
        return new OpResult(r.Ok ? OpStatus.Succeeded : OpStatus.Failed, r.ResultCode, r.Message, r.RebootRequired);
    }

    private static Task<OpResult> PipAsync(string package, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (!Tools.IsSafePackageName(package) || package.StartsWith('@')) return Task.FromResult(OpResult.Reject($"Invalid package name '{package}'."));
        var python = Tools.MachinePython();
        if (python is null) return Task.FromResult(new OpResult(OpStatus.Failed, -1, "No machine-wide Python found."));
        return RunFixedAsync(python, ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--no-input", package], "pip", dryRun, line, ct);
    }

    private static Task<OpResult> RestorePointAsync(string description, bool dryRun, Action<string> line, CancellationToken ct)
    {
        if (!SafeDescription().IsMatch(description)) return Task.FromResult(OpResult.Reject("Invalid restore point description."));
        // The description travels in an environment variable, never inside the script text.
        const string script =
            "$ErrorActionPreference='Stop'; Checkpoint-Computer -Description $env:CARS_RP_DESC -RestorePointType MODIFY_SETTINGS; 'Restore point created.'";
        return RunFixedAsync(Tools.WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], "Restore point", dryRun, line, ct,
            env: new Dictionary<string, string> { ["CARS_RP_DESC"] = description });
    }

    internal static async Task<OpResult> RunFixedAsync(
        string exe, IReadOnlyList<string> args, string what, bool dryRun, Action<string> line, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        if (dryRun) return new OpResult(OpStatus.DryRun, 0, Describe(exe, args));
        if (!File.Exists(exe)) return new OpResult(OpStatus.Failed, -1, $"{what}: {exe} not found.");

        ProcessResult raw;
        if (env is null)
        {
            raw = await ProcessRunner.RunAsync(exe, args, line, timeout ?? TimeSpan.FromMinutes(30), ct);
        }
        else
        {
            foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
            try { raw = await ProcessRunner.RunAsync(exe, args, line, timeout ?? TimeSpan.FromMinutes(30), ct); }
            finally { foreach (var k in env.Keys) Environment.SetEnvironmentVariable(k, null); }
        }

        if (raw.TimedOut) return new OpResult(OpStatus.Failed, -1, $"{what} timed out.");
        return raw.ExitCode == 0
            ? new OpResult(OpStatus.Succeeded, 0, raw.Lines.LastOrDefault() ?? $"{what} done.")
            : new OpResult(OpStatus.Failed, raw.ExitCode, $"{what} failed (exit {raw.ExitCode}): {raw.Lines.LastOrDefault()}");
    }

    public static string Describe(string exe, IEnumerable<string> args) =>
        $"\"{exe}\" " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    [GeneratedRegex(@"^[A-Za-z0-9 .,:()_-]{1,64}$")]
    private static partial Regex SafeDescription();
}
