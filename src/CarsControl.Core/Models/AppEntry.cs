namespace CarsControl.Core.Models;

/// <summary>Who an install belongs to. Drives where an operation runs: User = in-process as you,
/// Machine = the elevated helper (as the admin account). See <see cref="Operations.OperationRouter"/>.</summary>
public enum InstallScope { Unknown, User, Machine }

public enum AppSource { Arp, Msix }

/// <summary>Which mechanism keeps an app current. Only Winget/Store/Office/Wsl/WindowsUpdate are
/// driven by C.A.R.S.; the rest are shown so you know why an app is not in "Update all".</summary>
public enum UpdateChannel
{
    None,
    Winget,
    Store,
    SelfUpdating,
    Launcher,
    Game,
    Office,
    Wsl,
    Driver,
    System,
}

/// <summary>Where an ARP entry lives in the registry. Hive+View+KeyName uniquely identify it, and it
/// is what the elevated helper receives instead of a command line.</summary>
public enum ArpHive { LocalMachine64, LocalMachine32, CurrentUser }

public sealed record ArpLocation(ArpHive Hive, string KeyName)
{
    public InstallScope Scope => Hive == ArpHive.CurrentUser ? InstallScope.User : InstallScope.Machine;

    public override string ToString() => $"{Hive}\\{KeyName}";
}

/// <summary>One installed program or Store app, merged from ARP, MSIX and winget correlation.</summary>
public sealed class AppEntry
{
    public required string Key { get; init; }
    public required string Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public InstallScope Scope { get; set; }
    public AppSource Source { get; init; }

    // ARP (Add/Remove Programs) data
    public ArpLocation? Arp { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? MsiProductCode { get; init; }
    public bool NoRemove { get; init; }
    public string? DisplayIcon { get; init; }

    // MSIX data
    public string? MsixFullName { get; init; }
    public string? MsixFamilyName { get; init; }
    public string? MsixLogoPath { get; init; }

    public string? InstallLocation { get; set; }
    public DateTime? InstallDate { get; set; }

    /// <summary>Registry EstimatedSize (unreliable) until the background folder scan replaces it.</summary>
    public long? SizeBytes { get; set; }
    public bool SizeIsMeasured { get; set; }

    /// <summary>When the install folder was last measured; fresh measurements are carried across
    /// scans instead of re-reading ~900 GB of game folders on every launch.</summary>
    public DateTime? SizeMeasuredAt { get; set; }

    // winget correlation
    public string? WingetId { get; set; }
    public string? WingetSource { get; set; }
    public string? AvailableVersion { get; set; }

    public UpdateChannel Channel { get; set; }
    public bool IsProtected { get; set; }

    public bool HasUpdate => AvailableVersion is not null;

    public bool CanUninstall =>
        !NoRemove && (Source == AppSource.Msix || UninstallString is not null || MsiProductCode is not null || WingetId is not null);

    public override string ToString() => $"{Name} {Version} [{Scope}/{Source}] {Key}";
}
