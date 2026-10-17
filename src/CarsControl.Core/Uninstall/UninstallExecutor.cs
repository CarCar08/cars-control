using System.Diagnostics;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Storage;
using CarsControl.Core.Util;
using CarsControl.Core.Winget;
using WinPackageManager = Windows.Management.Deployment.PackageManager;

namespace CarsControl.Core.Uninstall;

public enum UninstallOutcome { Removed, StillInstalled, Failed, HandedOff, DryRun }

public sealed record UninstallResult(UninstallOutcome Outcome, string Message, int ExitCode = 0, bool RebootRequired = false)
{
    public bool Ok => Outcome is UninstallOutcome.Removed or UninstallOutcome.HandedOff or UninstallOutcome.DryRun;
}

/// <summary>Executes an <see cref="UninstallPlan"/> in the current process's account and confirms the
/// app is actually gone. Used directly (as you) for per-user apps and inside the helper for machine apps.</summary>
public static class UninstallExecutor
{
    public static async Task<UninstallResult> ExecuteAsync(AppEntry app, UninstallPlan plan, string? wingetPath, Action<string> line, bool dryRun, CancellationToken ct)
    {
        if (ProtectedApps.IsAlwaysProtected(app)) return new(UninstallOutcome.Failed, "Salad is permanently protected and cannot be changed.");
        switch (plan.Method)
        {
            case UninstallMethod.Msix:
                if (dryRun) return new(UninstallOutcome.DryRun, $"Remove-AppxPackage {app.MsixFullName}");
                var pm = new WinPackageManager();
                var op = await pm.RemovePackageAsync(app.MsixFullName!);
                return op.ExtendedErrorCode is null || op.ExtendedErrorCode.HResult == 0
                    ? new(UninstallOutcome.Removed, "Store app removed")
                    : new(UninstallOutcome.Failed, op.ErrorText ?? op.ExtendedErrorCode.Message, op.ExtendedErrorCode.HResult);

            case UninstallMethod.SteamHandoff:
                if (dryRun) return new(UninstallOutcome.DryRun, $"steam://uninstall/{plan.SteamAppId}");
                Process.Start(new ProcessStartInfo($"steam://uninstall/{plan.SteamAppId}") { UseShellExecute = true });
                // Round 2 reported "removed" the moment Steam's dialog opened. Wait until Steam has
                // actually removed it (its registry entry or app manifest disappears) before saying so.
                line("Confirm the uninstall in Steam's window…");
                var steamDeadline = DateTime.UtcNow + SteamWait;
                while (DateTime.UtcNow < steamDeadline)
                {
                    await Task.Delay(2000, ct);
                    if (!StillInstalled(app) || !SteamManifestExists(plan.SteamAppId!))
                        return new(UninstallOutcome.Removed, "Removed by Steam");
                }
                return new(UninstallOutcome.HandedOff, "Steam hasn't removed it yet — confirm or cancel it in Steam");

            case UninstallMethod.Winget:
                if (!WingetCli.IsValidId(app.WingetId!) || wingetPath is null) return new(UninstallOutcome.Failed, "winget unavailable");
                var args = WingetCli.UninstallArgs(app.WingetId!, null);
                if (dryRun) return new(UninstallOutcome.DryRun, $"\"{wingetPath}\" {string.Join(' ', args)}");
                var (outcome, raw) = await WingetCli.RunAsync(wingetPath, args, line, ct);
                return await ConfirmAsync(app, raw.ExitCode, outcome.Ok ? null : outcome.Message, outcome.RebootRequired, ct);

            case UninstallMethod.Msi:
            case UninstallMethod.Quiet:
            case UninstallMethod.DetectedSilent:
            case UninstallMethod.Interactive:
                var cmd = plan.Command!;
                if (dryRun) return new(UninstallOutcome.DryRun, cmd.ToString());
                var silent = plan.Method != UninstallMethod.Interactive;
                var exit = await RunAsync(cmd, silent, line, ct);
                var reboot = exit is 3010 or 1641;
                var failed = exit is not (0 or 3010 or 1641 or 1605) ? $"Uninstaller exited with {exit}" : null; // 1605 = already gone
                return await ConfirmAsync(app, exit, failed, reboot, ct);

            default:
                return new(UninstallOutcome.Failed, plan.Description);
        }
    }

    private static async Task<int> RunAsync(CommandLine cmd, bool silent, Action<string> line, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cmd.Exe, cmd.Args)
        {
            UseShellExecute = false,
            CreateNoWindow = silent,
            WorkingDirectory = Path.GetDirectoryName(cmd.Exe) is { Length: > 0 } wd && Directory.Exists(wd) ? wd : Environment.SystemDirectory,
        };
        line($"running {cmd}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Uninstaller did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(silent ? 20 : 60));
        await p.WaitForExitAsync(timeout.Token);
        return p.ExitCode;
    }

    /// <summary>Exit codes lie in both directions (NSIS uninstallers return before they finish; Epic's
    /// MSI reported success while breaking the app), so success = the ARP entry / package is gone.</summary>
    private static async Task<UninstallResult> ConfirmAsync(AppEntry app, int exitCode, string? failure, bool reboot, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if (!StillInstalled(app))
                return new(UninstallOutcome.Removed, reboot ? "Removed — restart to finish" : "Removed", exitCode, reboot);
            if (failure is not null) break;
            await Task.Delay(2000, ct);
        }
        return failure is not null
            ? new(UninstallOutcome.Failed, failure, exitCode)
            : new(UninstallOutcome.StillInstalled, "The uninstaller finished but the app is still registered", exitCode);
    }

    public static TimeSpan SteamWait { get; set; } = TimeSpan.FromMinutes(3);

    private static bool SteamManifestExists(string appId)
    {
        var roots = GameLibraries.SteamLibraryRoots();
        if (roots.Count == 0 || !appId.All(char.IsAsciiDigit)) return true; // can't tell: go by the registry entry
        return roots.Any(l => File.Exists(Path.Combine(l, "steamapps", $"appmanifest_{appId}.acf")));
    }

    public static bool StillInstalled(AppEntry app) =>
        app.Arp is { } loc ? ArpProvider.Exists(loc)
        : app.MsixFullName is { } full && new WinPackageManager().FindPackageForUser(string.Empty, full) is not null;
}
