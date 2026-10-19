using CarsControl.Core.Models;

namespace CarsControl.Core.Updates;

public enum UpdateSource { Winget, WindowsUpdate, Store, Office, Wsl, Npm, Pip, PsModule }

/// <summary>One thing "Update all" can do. Key doubles as the ignore-rule key (e.g. "winget:Google.Chrome").</summary>
public sealed class UpdateCandidate
{
    public required string Key { get; init; }
    public required UpdateSource Source { get; init; }
    public required string Title { get; init; }
    public string? CurrentVersion { get; init; }
    public string? AvailableVersion { get; init; }

    /// <summary>True when it must run in the elevated helper (machine-wide install or system component).</summary>
    public bool NeedsAdmin { get; init; }

    /// <summary>Ticked by default in "Update all". Drivers and pip start unticked.</summary>
    public bool SelectedByDefault { get; init; } = true;

    /// <summary>Why it is unticked or anything else worth knowing before updating.</summary>
    public string? Note { get; init; }

    /// <summary>Not persisted with saved check results; re-linked from the inventory on load.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AppEntry? App { get; set; }

    /// <summary>Store and Office rows are "go check" actions, not discovered updates, so they are
    /// never counted as updates found.</summary>
    public bool IsCheckAction => Source is UpdateSource.Store or UpdateSource.Office;
    public string? PackageId { get; init; }   // winget id / npm, pip or module name
    public string? WuUpdateId { get; init; }

    /// <summary>pip: the site-packages folder the outdated copy is in (decides admin vs. as-you).</summary>
    public string? Location { get; init; }
    public bool IsDriver { get; init; }

    /// <summary>An optional Windows Update item (BrowseOnly): Windows itself never installs it on its own. Listed apart,
    /// unticked, and never counted as an update found or included in "Update everything" or maintenance.</summary>
    public bool IsOptional { get; init; }
    public long? DownloadBytes { get; init; }

    public string VersionText => (CurrentVersion, AvailableVersion) switch
    {
        ({ } c, { } a) => $"{c} → {a}",
        (null, { } a) => $"→ {a}",
        ({ } c, null) => c,
        _ => "",
    };
}

public sealed record SourceCheck(UpdateSource Source, IReadOnlyList<UpdateCandidate> Items, string? Error, TimeSpan Elapsed);
