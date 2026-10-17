using System.Windows.Media;
using CarsControl.App.Converters;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CarsControl.App.ViewModels;

/// <summary>One row in Apps. Kept across rescans (the entry is swapped in place) so the list doesn't
/// jump, and the icon is only loaded when a row is actually shown (the grid virtualizes).</summary>
public sealed partial class AppItemViewModel(AppEntry entry) : ObservableObject
{
    /// <summary>Set once by AppsViewModel; called the first time a row's icon is needed.</summary>
    public static Func<AppEntry, Task<ImageSource?>>? IconProvider { get; set; }

    public AppEntry Entry { get; private set; } = entry;

    public void Update(AppEntry fresh)
    {
        var iconChanged = fresh.DisplayIcon != Entry.DisplayIcon || fresh.MsixLogoPath != Entry.MsixLogoPath;
        Entry = fresh;
        if (iconChanged) _iconRequested = false;
        OnPropertyChanged(string.Empty); // every bound property may have changed
    }

    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? "";
    public string Version => Entry.Version ?? "";

    /// <summary>Details panel: games and some apps don't report a version (the label used to sit over an empty line).</summary>
    public string VersionDisplay => string.IsNullOrWhiteSpace(Entry.Version) ? "Not reported by the app" : Entry.Version;
    public string? AvailableVersion => Entry.AvailableVersion;
    public bool HasUpdate => Entry.HasUpdate && Entry.Channel == UpdateChannel.Winget;
    public bool IsProtected => Entry.IsProtected;

    /// <summary>Protect = a guard against uninstalling by accident (bulk or right-click). Updates still run.</summary>
    public string ProtectText => Entry.IsProtected ? "Unprotect" : "Protect from uninstall";

    public string ScopeText => Entry.Scope switch
    {
        InstallScope.User => "Just me",
        InstallScope.Machine => "All users",
        _ => "—",
    };

    public string KindText => Entry.Source == AppSource.Msix ? "Store app" : Entry.Channel == UpdateChannel.Game ? "Game" : "Program";
    public string ChannelText => ChannelClassifier.Describe(Entry.Channel);

    public long SizeBytes => Entry.SizeBytes ?? -1;
    public string SizeText => Format.Bytes(Entry.SizeBytes);
    public string SizeTooltip => Entry.SizeIsMeasured ? "Measured from the install folder" : "Estimate reported by the installer";

    public DateTime InstallDateSort => Entry.InstallDate ?? DateTime.MinValue;
    public string InstallDateText => Entry.InstallDate?.ToString("yyyy-MM-dd") ?? "—";

    /// <summary>Protected apps can't be ticked until unlocked.</summary>
    public bool CanSelect => Entry.CanUninstall && !Entry.IsProtected;

    public string SelectTooltip =>
        Entry.IsProtected ? "Protected — right-click › Unprotect to allow uninstalling"
        : !Entry.CanUninstall ? "This app doesn't offer an uninstaller"
        : "Select for bulk uninstall";

    // ---- details panel ----

    public string? InstallLocation => Entry.InstallLocation;
    public bool HasInstallLocation => !string.IsNullOrEmpty(Entry.InstallLocation);
    public string? WingetId => Entry.WingetId;
    public bool HasWingetId => Entry.WingetId is not null;
    public string SourceText => Entry.Source == AppSource.Msix ? $"Microsoft Store app · {Entry.MsixFamilyName}" : $"Installed program (listed in Windows' Apps) · {Entry.Arp}";
    public string UninstallText => UninstallPlanner.Plan(Entry) is { } p ? p.Description + (p.Elevated ? " (admin)" : "") : "—";

    public string UpdateText => Entry.Channel switch
    {
        UpdateChannel.Winget when Entry.HasUpdate => $"winget: {Entry.Version} → {Entry.AvailableVersion}",
        UpdateChannel.Winget => "winget: up to date",
        UpdateChannel.Launcher => "Updates itself (a launcher) — C.A.R.S. leaves it alone on purpose",
        UpdateChannel.Game => "Updated by its launcher",
        UpdateChannel.SelfUpdating => "Has its own updater",
        UpdateChannel.Store => "Microsoft Store",
        UpdateChannel.Office => "Microsoft 365 (Refresh › Always checked)",
        UpdateChannel.Wsl => "wsl --update (Refresh page)",
        UpdateChannel.Driver => "Vendor tool / driver",
        UpdateChannel.System => "Windows",
        _ => "Not tracked by any update source",
    };

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _actionStatus;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _lastAction;

    private ImageSource? _icon;
    private bool _iconRequested;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested && IconProvider is { } load)
            {
                _iconRequested = true;
                _ = LoadIconAsync(load);
            }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    private async Task LoadIconAsync(Func<AppEntry, Task<ImageSource?>> load)
    {
        var img = await load(Entry);
        if (img is null) return;
        _icon = img;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    /// <summary>DataGrid rows use ToString() as their accessible name (screen readers, UI automation).</summary>
    public override string ToString() => Name;

    public void NotifySizeChanged()
    {
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeTooltip));
    }
}
