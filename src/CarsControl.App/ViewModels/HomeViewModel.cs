using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CarsControl.App.Converters;
using CarsControl.App.Services;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record DriveCard(string Letter, string Label, double UsedPercent, string UsedText, string FreeText, bool IsCritical, bool IsWarning,
    bool IsActive = false, string? Note = null)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>Accessible name (items controls announce ToString()).</summary>
    public override string ToString() => $"Drive {Letter} {Label}, {FreeText}";

    /// <summary>Updates a card list in place. Clearing and re-adding value-equal cards left their
    /// accessibility entries pointing at discarded controls, so screen readers (and the UI audit) could
    /// only reach the card that had changed.</summary>
    public static void Sync(ObservableCollection<DriveCard> target, IReadOnlyList<DriveCard> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i >= target.Count) target.Add(fresh[i]);
            else if (!target[i].Equals(fresh[i])) target[i] = fresh[i];
        }
        while (target.Count > fresh.Count) target.RemoveAt(target.Count - 1);
    }

    public static IEnumerable<DriveCard> ReadAll(Func<string, (bool Active, string? Note)>? extra = null)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderBy(d => d.Name))
        {
            var used = d.TotalSize - d.TotalFreeSpace;
            var pct = d.TotalSize == 0 ? 0 : used * 100.0 / d.TotalSize;
            var letter = d.Name.TrimEnd('\\');
            var (active, note) = extra?.Invoke(letter) ?? (false, null);
            yield return new DriveCard(letter, string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel, pct,
                $"{Format.Bytes(used)} of {Format.Bytes(d.TotalSize)}", $"{Format.Bytes(d.TotalFreeSpace)} free",
                pct >= 95, pct is >= 85 and < 95, active, note);
        }
    }
}

public sealed record ActivityRow(string When, string Title, string What, string Status, bool Failed);

/// <summary>Dashboard. Every number comes from the same service the matching page uses, so Home can't
/// disagree with Refresh, Storage or Cleanup any more.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>This computer's name (was hard-coded once).</summary>
    public string PcName { get; } = App.PcName;

    private readonly InventoryStore _store;
    private readonly UpdateCenter _updates;
    private readonly StorageViewModel _storage;
    private readonly CleanupViewModel _cleanup;
    private readonly CarsDb _db;
    private readonly MaintenanceService _maintenance;
    private readonly GameMode _game;
    private readonly AttentionService _attention;

    /// <summary>"Needs attention", worst first: approvals waiting, low disk space, failures…</summary>
    public ObservableCollection<AttentionItem> Attention => _attention.Items;

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];

    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _appDetail = "Scanning…";
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private string _updateDetail = "Not checked yet";
    [ObservableProperty] private string _updateChecked = "";
    [ObservableProperty] private string _reclaimable = "—";
    [ObservableProperty] private string _reclaimableDetail = "Measuring…";
    [ObservableProperty] private string _gameCount = "—";
    [ObservableProperty] private string _gamesDetail = "Reading game libraries…";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _adminSessionActive;
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private string _maintenanceLast = "";
    [ObservableProperty] private string _maintenanceNext = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunMaintenanceCommand))]
    private bool _maintenanceRunning;

    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private bool _isGaming;
    [ObservableProperty] private string _gamingText = "";

    public HomeViewModel(InventoryStore store, UpdateCenter updates, StorageViewModel storage, CleanupViewModel cleanup, CarsDb db, HelperClient helper,
        MaintenanceService maintenance, GameMode game, AttentionService attention)
    {
        _maintenance = maintenance;
        _game = game;
        _attention = attention;
        _attention.Changed += () => AttentionCount = _attention.Items.Count;
        AttentionCount = _attention.Items.Count;
        _maintenance.Changed += RecalculateMaintenance;
        _store = store;
        _updates = updates;
        _storage = storage;
        _cleanup = cleanup;
        _db = db;
        _store.Updated += RecalculateApps;
        _store.LoadingChanged += v => IsLoading = v || _updates.IsChecking;
        _updates.Changed += RecalculateUpdates;
        _cleanup.EstimatesChanged += RecalculateReclaimable;
        helper.ConnectionChanged += a => Platform.OnUiThread(() => AdminSessionActive = a);
        RecalculateApps();
        RecalculateUpdates();
        RecalculateMaintenance();
    }

    private void RecalculateMaintenance()
    {
        MaintenanceRunning = _maintenance.IsRunning;
        MaintenanceLast = _maintenance.LastText;
        MaintenanceNext = _maintenance.NextText;
        IsGaming = _game.IsOn;
        GamingText = _game.DetectedGame is { } g ? $"Gaming mode · {g}" : _game.IsOn ? "Gaming mode · on" : "Gaming mode";
    }

    private bool CanRunMaintenance() => !MaintenanceRunning;

    [RelayCommand]
    private async Task RunAttentionAsync(AttentionItem? item)
    {
        if (item?.Action is { } action) await action();
    }

    /// <summary>The second button: "Dismiss" for items that can be forgotten, otherwise "Snooze".</summary>
    [RelayCommand]
    private void SecondaryAttention(AttentionItem? item)
    {
        if (item is null) return;
        if (item.Secondary is { } secondary) secondary();
        else _attention.Snooze(item);
    }

    /// <summary>The Home chip: on/off by hand. Off while a game runs snoozes it for that game.</summary>
    [RelayCommand]
    private void ToggleGaming() => _game.SetManual(!_game.IsOn);

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private Task RunMaintenanceAsync() => _maintenance.RunAsync("Maintenance (run now)", unattended: false);

    [ObservableProperty] private bool _showVpn;
    [ObservableProperty] private bool _vpnOk;
    [ObservableProperty] private string _vpnText = "";
    private System.Windows.Threading.DispatcherTimer? _vpnTimer;

    /// <summary>The VPN chip: only when Proton VPN is on this PC, since "your traffic isn't going through it" matters there.</summary>
    public async Task RefreshVpnAsync()
    {
        var adapters = await Task.Run(Core.Health.NetworkStatus.Adapters); // off the UI thread: enumerating adapters takes a moment
        var installed = Extras.ProtonVpn(adapters);
        ShowVpn = installed;
        if (!installed) return;
        var status = Core.Health.NetworkStatus.Vpn(adapters);
        VpnOk = status.Level == Core.Health.VpnLevel.Carrying;
        VpnText = status.Level switch { Core.Health.VpnLevel.Carrying => "VPN on", Core.Health.VpnLevel.ConnectedNotCarrying => "VPN: not carrying traffic", _ => "VPN off" };
    }

    private void OnVpnTick(object? sender, EventArgs e) => BackgroundTasks.Run(RefreshVpnAsync(), "VPN chip");

    /// <summary>Home is no longer on screen: stop the once-a-minute look at the network.</summary>
    public void OnHidden() => _vpnTimer?.Stop();

    [RelayCommand]
    private void OpenVpn() => Navigator.Go("health", "network");

    /// <summary>Called when Home is shown: refresh cheap things, and start the slower reads once.</summary>
    public async Task OnShownAsync()
    {
        var vpn = RefreshVpnAsync();
        _vpnTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _vpnTimer.Tick -= OnVpnTick;
        _vpnTimer.Tick += OnVpnTick;
        _vpnTimer.Start();
        RecalculateMaintenance(); // "next" and "last" are relative to now
        var drives = RefreshDrivesAsync();
        LoadActivity();
        if (!_storage.ReportsLoaded) await _storage.LoadReportsAsync();
        RecalculateGames();
        await drives;
        await RefreshDrivesAsync(); // again with the largest folders from the storage reports
        await vpn;
        await _cleanup.EnsureEstimatesAsync();
    }

    [RelayCommand]
    private async Task RefreshDrivesAsync()
    {
        // Reading a drive that has spun down (a backup hard disk) can block for seconds: never on the UI thread.
        var read = await DriveSnapshot.ReadAsync();
        var cards = read.Select(c => c with { Note = _storage.LargestFolder(c.Letter) }).ToList();
        DriveCard.Sync(Drives, cards);
        UpdateDriveAttention(cards);
    }

    /// <summary>A drive over 95% full is critical, over 85% a warning. The key includes the 5% step, so a
    /// snoozed warning comes back if the drive keeps filling.</summary>
    private void UpdateDriveAttention(IReadOnlyList<DriveCard> cards)
    {
        foreach (var d in cards)
        {
            var step = (int)(d.UsedPercent / 5) * 5;
            var key = $"drive:{d.Letter}";
            _attention.Set(key, d.IsCritical || d.IsWarning
                ? new AttentionItem
                {
                    Key = $"{key}:{step}",
                    Title = d.IsCritical ? $"Drive {d.Letter} is almost full" : $"Drive {d.Letter} is getting full",
                    Detail = $"{d.FreeText}" + (d.HasNote ? $" · {d.Note}" : "") + ". Storage shows what's taking the space.",
                    Level = d.IsCritical ? AttentionLevel.Critical : AttentionLevel.Warning,
                    Icon = Wpf.Ui.Controls.SymbolRegular.HardDrive20,
                    ActionText = "Free up space",
                    Action = () =>
                    {
                        Navigator.Go("storage", "files");
                        return Task.CompletedTask;
                    },
                }
                : null);
        }
    }

    private void RecalculateApps()
    {
        var snap = _store.Current;
        if (snap is null) return;
        AppCount = snap.Apps.Count;
        AppDetail = $"{snap.Apps.Count(a => a.Scope == InstallScope.User)} just for you · {snap.Apps.Count(a => a.Scope == InstallScope.Machine)} for all users";
    }

    private void RecalculateUpdates()
    {
        var found = _updates.Found;
        UpdateCount = found.Count;
        IsLoading = _store.IsLoading || _updates.IsChecking;
        UpdateDetail = _updates.IsChecking && found.Count == 0 ? "Checking…"
            : found.Count == 0 ? "Everything is up to date"
            : Summarize(found);
        var admin = found.Count(f => f.NeedsAdmin);
        UpdateChecked = (_updates.IsChecking ? "checking every source…"
                            : _updates.LastChecked is { } t ? $"all sources checked {Format.Ago(t)}"
                            : "apps checked · other sources still checking")
                        + (admin > 0 ? $" · {admin} need admin" : "");
    }

    /// <summary>"LocalSend, WSL · 3 Windows updates · 1 PowerShell module" — app names for apps, counts for
    /// sources whose titles are long (Windows Update titles run to a paragraph).</summary>
    internal static string Summarize(IReadOnlyList<Core.Updates.UpdateCandidate> found)
    {
        var parts = new List<string>();
        var named = found.Where(f => f.Source is Core.Updates.UpdateSource.Winget or Core.Updates.UpdateSource.Wsl).Select(f => ShortName(f.Title)).ToList();
        if (named.Count > 0) parts.Add(string.Join(", ", named.Take(2)) + (named.Count > 2 ? $" +{named.Count - 2}" : ""));
        void Count(Core.Updates.UpdateSource s, string one, string many)
        {
            var n = found.Count(f => f.Source == s);
            if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}");
        }
        Count(Core.Updates.UpdateSource.WindowsUpdate, "Windows update", "Windows updates");
        Count(Core.Updates.UpdateSource.PsModule, "PowerShell module", "PowerShell modules");
        Count(Core.Updates.UpdateSource.Pip, "Python package", "Python packages");
        Count(Core.Updates.UpdateSource.Npm, "npm package", "npm packages");
        return string.Join(" · ", parts);
    }

    /// <summary>"LocalSend version 1.17.0" → "LocalSend".</summary>
    private static string ShortName(string title)
    {
        var i = title.IndexOf(" version ", StringComparison.OrdinalIgnoreCase);
        var t = i > 0 ? title[..i] : title;
        return t.Length > 28 ? t[..27] + "…" : t;
    }

    private void RecalculateReclaimable()
    {
        var (bytes, items, rules) = _cleanup.DefaultReclaimable();
        Reclaimable = Format.Bytes(bytes);
        ReclaimableDetail = rules == 0 ? "Nothing to clean" : $"{items:N0} items in {rules} safe categories";
    }

    private void RecalculateGames()
    {
        var (count, bytes, stale) = _storage.GameTotals();
        GameCount = count.ToString();
        GamesDetail = $"{Format.Bytes(bytes)} · {Format.Bytes(stale)} not played in 90+ days";
    }

    private void LoadActivity()
    {
        Activity.Clear();
        foreach (var h in _db.ListHistory(5))
            Activity.Add(new ActivityRow(Format.Ago(h.Started), h.Title,
                h.Kind switch { "update" => "Update", "uninstall" => "Uninstall", "cleanup" => "Cleanup", "leftovers" => "Leftovers", "restore" => "Restore", "maintenance" => "Maintenance", "gaming" => "Gaming", "privacy" => "Privacy", "health" => "Health", "startup" => "Startup", "backup" => "Backup", "sync" => "Sync", "install" => "Install", var k => k },
                h.Status, h.Status is "Failed" or "Rejected"));
        HasActivity = Activity.Count > 0;
    }
}
