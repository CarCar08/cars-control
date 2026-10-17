using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Util;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Updates;

public sealed record UpdateRunResult(
    OpStatus Status,
    string Message,
    bool Elevated,
    int ExitCode = 0,
    bool RebootRequired = false,
    IReadOnlyList<string>? Warnings = null,
    string? LogPath = null,
    CatalogMismatch? Mismatch = null)
{
    public bool Ok => Status is OpStatus.Succeeded or OpStatus.DryRun;
}

/// <summary>
/// Runs one update through the right executor. Machine-wide and system items go to the elevated helper;
/// per-user items run here, as you. If the helper can't see a winget package (0x8A150014 — the
/// UniGetUI failure mode for Oh My Posh/LocalSend), it is retried unelevated.
/// </summary>
public sealed class UpdateExecutor(IHelperChannel helper)
{
    public bool DryRun { get; set; }

    public static string UserLogDirectory
    {
        get
        {
            var dir = Path.Combine(Util.UserData.Root, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<UpdateRunResult> RunAsync(UpdateCandidate c, Action<string> onLine, CancellationToken ct)
    {
        if (ProtectedApps.IsAlwaysProtectedName(c.Title) || ProtectedApps.IsAlwaysProtectedName(c.PackageId)
            || c.App is { } protectedApp && ProtectedApps.IsAlwaysProtected(protectedApp))
            return new(OpStatus.Rejected, "Salad is permanently protected; use its own updater when you choose.", false);
        switch (c.Source)
        {
            case UpdateSource.Winget:
                var before = c.App?.Arp is { } loc ? ArpProvider.ReadOne(loc) : null;
                var result = c.NeedsAdmin ? await WingetViaHelperAsync(c, onLine, ct) : await WingetAsUserAsync(c.PackageId!, onLine, ct);
                return result.Status == OpStatus.Succeeded ? Verify(c, before, result) : result;

            case UpdateSource.Store:
                return FromHelper(await SendAdminAsync(new StoreUpdateOp(), onLine, ct));
            case UpdateSource.Wsl:
                // As you, never via the helper: see UpdateSources.Wsl (0x80073D19 when elevated as the admin account).
                return await RunUserAsync(Tools.Wsl, ["--update"], "WSL", onLine, ct);
            case UpdateSource.Pip:
                if (!Tools.IsSafePackageName(c.PackageId!) || Tools.MachinePython() is not { } python) return new(OpStatus.Rejected, "Invalid pip package", false);
                var pip = c.NeedsAdmin
                    ? FromHelper(await SendAdminAsync(new PipUpgradeOp(c.PackageId!), onLine, ct))
                    : await RunUserAsync(python, PipInstall.UpgradeArgs(c.PackageId!, user: true), "pip", onLine, ct, "Updated");
                return pip.Status == OpStatus.Succeeded ? await VerifyPipAsync(c, python, pip, ct) : pip;
            case UpdateSource.WindowsUpdate:
                return await WindowsUpdatesAsync([c], onLine, ct);

            case UpdateSource.Office:
                return await RunUserAsync(Tools.OfficeC2RClient, ["/update", "user", "displaylevel=false", "forceappshutdown=false"], "Office", onLine, ct,
                    successMessage: "Office is checking for updates in the background");
            case UpdateSource.Npm:
                if (!Tools.IsSafePackageName(c.PackageId!) || Tools.Npm() is not { } npm) return new(OpStatus.Rejected, "Invalid npm package", false);
                return await RunUserAsync(Tools.Cmd, ["/d", "/c", npm, "install", "-g", $"{c.PackageId}@latest"], "npm", onLine, ct);
            case UpdateSource.PsModule:
                if (!Tools.IsSafePackageName(c.PackageId!) || Tools.Pwsh() is not { } pwsh) return new(OpStatus.Rejected, "Invalid module", false);
                return await RunUserAsync(pwsh,
                    ["-NoProfile", "-NonInteractive", "-Command", $"Update-PSResource -Name '{c.PackageId}' -Scope CurrentUser -TrustRepository -AcceptLicense -ErrorAction Stop"],
                    "PowerShell module", onLine, ct);
        }
        return new(OpStatus.Rejected, $"Unsupported source {c.Source}", false);
    }

    /// <summary>All selected Windows updates install in one WUA session (one search, one download, one install).</summary>
    public async Task<UpdateRunResult> WindowsUpdatesAsync(IReadOnlyList<UpdateCandidate> items, Action<string> onLine, CancellationToken ct)
    {
        var ids = items.Select(i => i.WuUpdateId!).ToList();
        return FromHelper(await SendAdminAsync(new WindowsUpdateInstallOp(ids), onLine, ct));
    }

    /// <summary>A dry run must not cost a UAC prompt: unless an admin session is already open (or the
    /// dev-unelevated helper is in use), admin operations describe themselves without starting the helper.</summary>
    private Task<OpResult> SendAdminAsync(HelperOp op, Action<string> onLine, CancellationToken ct)
    {
        if (DryRun && !helper.IsConnected && !HelperClient.DevUnelevated)
            return Task.FromResult(new OpResult(OpStatus.DryRun, 0, "[admin helper] " + DescribeOp(op)));
        return helper.SendAsync(op, onLine, DryRun, ct);
    }

    public static string DescribeOp(HelperOp op) => op switch
    {
        WingetUpgradeOp u => $"winget upgrade --id {u.Id} --exact --silent" + (u.Version is null ? "" : $" --version {u.Version}"),
        WingetUninstallOp u => $"winget uninstall --id {u.Id} --exact --silent",
        StoreUpdateOp => "Store: MDM UpdateScanMethod (scan + install all Store app updates)",
        WslUpdateOp => "wsl.exe --update",
        PipUpgradeOp p => $"python -m pip install --upgrade {p.Package}",
        WindowsUpdateInstallOp w => $"Windows Update: download + install {w.UpdateIds.Count} update(s)",
        CreateRestorePointOp r => $"Checkpoint-Computer -Description \"{r.Description}\"",
        _ => op.GetType().Name,
    };

    private async Task<UpdateRunResult> WingetViaHelperAsync(UpdateCandidate c, Action<string> onLine, CancellationToken ct)
    {
        var r = await SendAdminAsync(new WingetUpgradeOp(c.PackageId!), onLine, ct);
        if (r.Status != OpStatus.NotFound) return FromHelper(r);
        onLine("The admin account can't see this install — retrying as you (per-user app).");
        return await WingetAsUserAsync(c.PackageId!, onLine, ct);
    }

    private async Task<UpdateRunResult> WingetAsUserAsync(string id, Action<string> onLine, CancellationToken ct)
    {
        if (!WingetCli.IsValidId(id)) return new(OpStatus.Rejected, $"Invalid winget id '{id}'", false);
        var winget = WingetCli.Locate();
        if (winget is null) return new(OpStatus.Failed, "winget.exe not found for your account", false);
        var logPath = Path.Combine(UserLogDirectory, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var args = WingetCli.UpgradeArgs(id, null, logPath);
        if (DryRun) return new(OpStatus.DryRun, HelperOperations.Describe(winget, args), false);

        var (outcome, raw) = await WingetCli.RunAsync(winget, args, onLine, ct);
        var status = outcome.Ok ? OpStatus.Succeeded : outcome.Failure == WingetFailure.NotFoundForAccount ? OpStatus.NotFound : OpStatus.Failed;
        return new(status, outcome.Message, false, raw.ExitCode, outcome.RebootRequired, LogPath: logPath);
    }

    private async Task<UpdateRunResult> RunUserAsync(string exe, IReadOnlyList<string> args, string what, Action<string> onLine, CancellationToken ct, string? successMessage = null)
    {
        if (DryRun) return new(OpStatus.DryRun, HelperOperations.Describe(exe, args), false);
        if (!File.Exists(exe)) return new(OpStatus.Failed, $"{what}: {exe} not found", false);
        var raw = await ProcessRunner.RunAsync(exe, args, onLine, TimeSpan.FromMinutes(20), ct);
        return raw.ExitCode == 0
            ? new(OpStatus.Succeeded, successMessage ?? "Updated", false)
            : new(OpStatus.Failed, $"{what} failed (exit {raw.ExitCode}): {raw.Lines.LastOrDefault()}", false, raw.ExitCode);
    }

    /// <summary>pip says "success" even when it upgraded a different copy than the one you use, so ask
    /// pip (as you, which sees your copy first) which version is actually in effect now.</summary>
    private static async Task<UpdateRunResult> VerifyPipAsync(UpdateCandidate c, string python, UpdateRunResult result, CancellationToken ct)
    {
        var show = await ProcessRunner.RunAsync(python, ["-m", "pip", "show", "--disable-pip-version-check", c.PackageId!], timeout: TimeSpan.FromMinutes(1), ct: ct);
        var now = PipInstall.ParseShowVersion(show.Lines);
        if (now is null || c.AvailableVersion is null || !VersionCompare.IsNewer(c.AvailableVersion, now))
            return result with { Message = now is null ? result.Message : $"Updated to {now}" };
        return new(OpStatus.Failed,
            $"pip finished, but the copy you use is still {now}{(c.Location is null ? "" : $" ({c.Location})")} — a different copy was updated.",
            result.Elevated, result.ExitCode);
    }

    private static UpdateRunResult FromHelper(OpResult r) =>
        new(r.Status, r.Message ?? r.Status.ToString(), true, r.ExitCode, r.RebootRequired, LogPath: r.LogPath);

    /// <summary>"Success" isn't trusted blindly: Epic's 2026-10-07 update reported success while deleting
    /// its own exe. Re-read the ARP entry and check the version moved and the app's exe still exists.</summary>
    internal static UpdateRunResult Verify(UpdateCandidate c, AppEntry? before, UpdateRunResult result)
    {
        if (c.App?.Arp is not { } loc) return result;
        var after = ArpProvider.ReadOne(loc);
        var warnings = new List<string>();

        var icon = after?.DisplayIcon ?? before?.DisplayIcon;
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(icon))
            warnings.Add($"Reported success, but {Path.GetFileName(icon)} is missing — the app may be broken.");

        CatalogMismatch? mismatch = null;
        if (after is not null && before is not null && after.Version == before.Version && c.AvailableVersion is not null && after.Version != c.AvailableVersion)
        {
            // Why? Most often winget's catalog itself is wrong: it offers a new version but downloads the old installer.
            mismatch = WingetLog.CheckFile(result.LogPath, c.AvailableVersion);
            var how = SelfUpdatingApps.HowToUpdate(c.PackageId);
            warnings.Add(mismatch is not null
                ? mismatch.Explanation + (how is null ? " Skip this version, or update it from the app itself." : " " + how + " Or skip this version.")
                : $"The installer said it worked, but {c.Title} is still {after.Version}." + (how is null ? " It may finish after a restart, or the app may need to be closed first." : " " + how));
        }

        return warnings.Count == 0 ? result : result with { Warnings = warnings, Mismatch = mismatch };
    }
}
