using CarsControl.Core.Models;

namespace CarsControl.Core.Uninstall;

public enum UninstallMethod { Msix, SteamHandoff, Msi, Quiet, Winget, DetectedSilent, Interactive, None }

/// <summary>How one app will be removed and where it runs.</summary>
public sealed record UninstallPlan(UninstallMethod Method, bool Elevated, string Description, CommandLine? Command = null, string? SteamAppId = null)
{
    public bool IsSilent => Method is UninstallMethod.Msix or UninstallMethod.Msi or UninstallMethod.Quiet or UninstallMethod.Winget or UninstallMethod.DetectedSilent;
}

/// <summary>
/// Chooses the most reliable way to remove an app silently, in this order:
/// MSIX → Steam hand-off → MSI product code → vendor QuietUninstallString → winget (knows silent
/// switches from its manifests) → detected installer family (Inno/NSIS/Squirrel) → interactive.
/// Machine-wide entries run in the helper; per-user (HKCU/MSIX) entries always run as you.
/// The helper re-plans from its own registry read and never trusts a command from the app.
/// </summary>
public static class UninstallPlanner
{
    public static UninstallPlan Plan(AppEntry a)
    {
        if (Inventory.ProtectedApps.IsAlwaysProtected(a)) return new(UninstallMethod.None, false, "Salad is permanently protected and cannot be changed.");
        var elevated = a.Scope == InstallScope.Machine;

        if (a.Source == AppSource.Msix)
            return new(UninstallMethod.Msix, false, "Remove Store app (as you)");

        if (a.Arp?.KeyName is { } key && key.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
            return new(UninstallMethod.SteamHandoff, false, "Hand off to Steam", SteamAppId: key["Steam App ".Length..].Trim());

        if (a.MsiProductCode is { } code)
            return new(UninstallMethod.Msi, elevated, "Windows Installer, silent",
                new CommandLine(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {code} /qn /norestart"));

        if (UninstallCommand.Parse(a.QuietUninstallString) is { } quiet)
        {
            // Inno's vendor-supplied "/SILENT" still lets message boxes through (e.g. "delete settings?").
            if (UninstallCommand.Detect(quiet) == InstallerKind.Inno && !quiet.Args.Contains("/SUPPRESSMSGBOXES", StringComparison.OrdinalIgnoreCase))
                quiet = quiet with { Args = $"{quiet.Args} /SUPPRESSMSGBOXES /NORESTART".Trim() };
            return new(UninstallMethod.Quiet, elevated, "Vendor's silent uninstaller", quiet);
        }

        if (a.WingetId is not null)
            return new(UninstallMethod.Winget, elevated, $"winget uninstall (silent)");

        if (UninstallCommand.Parse(a.UninstallString) is { } cmd)
        {
            var kind = UninstallCommand.Detect(cmd);
            if (UninstallCommand.MakeSilent(cmd, kind) is { } silent)
                return new(UninstallMethod.DetectedSilent, elevated, $"{kind} uninstaller, silent", silent);
            return new(UninstallMethod.Interactive, elevated, "Opens the app's own uninstaller — you click through it", cmd);
        }

        return new(UninstallMethod.None, elevated, "No uninstaller registered");
    }
}
