using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Core.Backup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed record FleetRow(FleetEntry Entry)
{
    public string Device => Entry.Status.Device + (Entry.IsThisPc ? " (this PC)" : "");
    public string Level => Entry.Stale ? "Info" : Entry.Status.HealthWarnings > 0 || Entry.Status.BlueScreensLastWeek > 0 ? "Watch" : "Good";
    public string Seen => Entry.IsThisPc ? "just now" : Converters.Format.Ago(Entry.Status.AtUtc.ToLocalTime()) + (Entry.Stale ? " (not seen for a while)" : "");
    public string Summary => Entry.Summary;
    public string Details => $"C.A.R.S. {Entry.Status.AppVersion} · {Entry.Status.Windows} · up {(Entry.Status.UptimeHours >= 48 ? $"{Entry.Status.UptimeHours / 24:0} days" : $"{Entry.Status.UptimeHours:0} h")}";
    public string Space => Entry.Status.TotalBytes <= 0 ? "" : $"{Converters.Format.Bytes(Entry.Status.FreeBytes)} free of {Converters.Format.Bytes(Entry.Status.TotalBytes)}";
    public double UsedPercent => Entry.Status.TotalBytes <= 0 ? 0 : 100.0 * (Entry.Status.TotalBytes - Entry.Status.FreeBytes) / Entry.Status.TotalBytes;
    public string Maintenance => Entry.Status.LastMaintenanceUtc is { } m ? $"Maintenance {Converters.Format.Ago(m.ToLocalTime())}" : "No maintenance yet";
    public string Backup => Entry.Status.LastBackupUtc is { } b ? $"Backup {Converters.Format.Ago(b.ToLocalTime())}" + (Entry.Status.LastBackupResult == "problems" ? " (had problems)" : "") : "No backups";
    public override string ToString() => $"{Device}: {Summary}";
}

/// <summary>
/// Backup & sync › Your setup and Your PCs. Your setup is a small file of choices (never passwords or tokens) that can
/// travel through a cloud folder or by hand; Your PCs lists a status line from every PC that shares the folder.
/// </summary>
public sealed partial class SetupSyncViewModel : ObservableObject
{
    private readonly SyncService _sync;
    private readonly AppSettings _settings;
    private bool _loading;

    public ObservableCollection<FleetRow> Fleet { get; } = [];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _preferences = true;
    [ObservableProperty] private bool _ignored = true;
    [ObservableProperty] private bool _protectedApps = true;
    [ObservableProperty] private bool _apps = true;
    [ObservableProperty] private bool _alerts;
    [ObservableProperty] private bool _publishStatus = true;
    [ObservableProperty] private string _folderText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _stateLevel = "Info";
    [ObservableProperty] private string _fleetText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private bool _hasFolder;

    public SetupSyncViewModel(SyncService sync, AppSettings settings)
    {
        _sync = sync;
        _settings = settings;
        _sync.Changed += () => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var s = _settings.Store.Current.Sync;
            Enabled = s.Enabled;
            Preferences = s.Preferences;
            Ignored = s.Ignored;
            ProtectedApps = s.Protected;
            Apps = s.Apps;
            Alerts = s.Alerts;
            PublishStatus = s.PublishStatus;
        }
        finally
        {
            _loading = false;
        }
        var folder = _sync.Folder;
        HasFolder = folder is not null;
        FolderText = folder ?? "Proton Drive isn't installed on this PC. Choose a folder in any cloud drive, or use files by hand below.";
        await ShowStateAsync();
        await ShowFleetAsync();
    }

    private async Task ShowStateAsync()
    {
        if (!HasFolder) { StateText = "Not set up."; StateLevel = "Info"; return; }
        try
        {
            var (action, remote) = await _sync.CheckAsync();
            (StateText, StateLevel) = action switch
            {
                SyncAction.InSync => ($"In sync{(_settings.Store.Current.Sync.LastSyncUtc is { } t ? $", last on {t.ToLocalTime():d MMM, h:mm tt}" : "")}.", "Good"),
                SyncAction.Push when remote is null => ("Nothing is in the folder yet. Send this PC's setup to start.", "Info"),
                SyncAction.Push => ("This PC has changes the folder doesn't.", "Watch"),
                SyncAction.Pull => ($"{remote?.Device} saved a newer setup ({Converters.Format.Ago(remote!.SavedUtc.ToLocalTime())}).", "Watch"),
                SyncAction.Conflict => ($"Both this PC and {remote?.Device} changed their setup since they last matched. Pick which to keep below.", "Bad"),
                _ => ("", "Info"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StateText = "The folder isn't reachable right now.";
            StateLevel = "Watch";
        }
    }

    private async Task ShowFleetAsync()
    {
        Fleet.Clear();
        foreach (var e in await _sync.ReadFleetAsync()) Fleet.Add(new FleetRow(e));
        FleetText = !HasFolder ? "Needs the sync folder: Proton Drive, or a folder you choose on the Your setup tab."
            : Fleet.Count == 0 ? "No PC has shared its status yet. Turn on “Share this PC's status” and it appears here (and on your other PCs)."
            : $"{Fleet.Count} PC{(Fleet.Count == 1 ? "" : "s")} share this folder.";
    }

    // ---------- settings ----------

    private void Save(Action<Core.Settings.SyncSettings> change)
    {
        if (!_loading) _settings.Store.Update(s => change(s.Sync));
    }

    partial void OnEnabledChanged(bool value)
    {
        Save(s => s.Enabled = value);
        if (value && !_loading) _ = EnableAsync();
    }

    private async Task EnableAsync()
    {
        if (_sync.Folder is null) { Notice("Choose a sync folder first.", NoticeKind.Warning); return; }
        await _sync.PublishStatusAsync();
        await RefreshAsync();
    }

    partial void OnPreferencesChanged(bool value) => Save(s => s.Preferences = value);
    partial void OnIgnoredChanged(bool value) => Save(s => s.Ignored = value);
    partial void OnProtectedAppsChanged(bool value) => Save(s => s.Protected = value);
    partial void OnAppsChanged(bool value) => Save(s => s.Apps = value);
    partial void OnAlertsChanged(bool value) => Save(s => s.Alerts = value);

    partial void OnPublishStatusChanged(bool value)
    {
        Save(s => s.PublishStatus = value);
        if (value && !_loading) _ = _sync.PublishStatusAsync().ContinueWith(_ => Platform.OnUiThread(() => _ = ShowFleetAsync()));
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (Platform.PickFolder("Choose the folder to sync through (inside Proton Drive, for example)") is not { } p) return;
        _settings.Store.Update(s => s.Sync.Folder = p);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseProtonFolderAsync()
    {
        _settings.Store.Update(s => s.Sync.Folder = null);
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFolder() => Platform.ShowInFileManager(_sync.Folder);

    // ---------- push, pull ----------

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!HasFolder) { Notice("There's no sync folder yet.", NoticeKind.Warning); return; }
        var (action, remote) = await _sync.CheckAsync();
        if (action is SyncAction.Pull or SyncAction.Conflict && remote is not null
            && !await Dialogs.ConfirmAsync($"{remote.Device} saved a setup {Converters.Format.Ago(remote.SavedUtc.ToLocalTime())} that this PC doesn't have.\n\nSending this PC's setup replaces it. Send anyway?", "Replace it", danger: true)) return;
        IsBusy = true;
        try
        {
            var r = await _sync.PushAsync();
            Notice(r.Message, r.Ok ? NoticeKind.Success : NoticeKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task GetAsync()
    {
        if (!HasFolder) { Notice("There's no sync folder yet.", NoticeKind.Warning); return; }
        var remote = await _sync.ReadRemoteAsync();
        if (remote is null) { Notice("There's no setup in the folder yet.", NoticeKind.Info); return; }
        await TakeOverAsync(remote);
    }

    private async Task TakeOverAsync(SetupBundle remote)
    {
        var parts = SetupSync.PartsOf(_settings.Store.Current.Sync) & Present(remote);
        if (parts == SetupParts.None) { Notice("That setup has nothing of what you chose to sync.", NoticeKind.Info); return; }
        var what = string.Join(", ", new[] { parts.HasFlag(SetupParts.Preferences) ? "preferences" : null, parts.HasFlag(SetupParts.Ignored) ? "ignored updates" : null,
            parts.HasFlag(SetupParts.Protected) ? "protected apps" : null, parts.HasFlag(SetupParts.Alerts) ? "phone alerts" : null }.Where(x => x is not null));
        if (!await Dialogs.ConfirmAsync($"Take over the setup saved by {remote.Device} ({remote.SavedUtc.ToLocalTime():d MMM yyyy, h:mm tt}, C.A.R.S. {remote.AppVersion})?\n\nThis replaces this PC's {what}. Its app list ({remote.Content.Apps?.Count ?? 0} apps) is for installing, not applied. Passwords, tokens and this PC's own holds are never touched.", "Take over")) return;
        IsBusy = true;
        try
        {
            var r = await _sync.PullAsync(remote, parts);
            Notice(r.Message, r.Ok ? NoticeKind.Success : NoticeKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private static SetupParts Present(SetupBundle b) =>
        (b.Content.Preferences is not null ? SetupParts.Preferences : 0) | (b.Content.Ignored is not null ? SetupParts.Ignored : 0) | (b.Content.ProtectedApps is not null ? SetupParts.Protected : 0) | (b.Content.Alerts is not null ? SetupParts.Alerts : 0);

    // ---------- by hand ----------

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        var path = Platform.PickSaveFile("Save this PC's setup", $"CARS setup {SyncService.DeviceName} {DateTime.Now:yyyy-MM-dd}.json", "C.A.R.S. setup (*.json)|*.json");
        if (path is null) return;
        try
        {
            await SetupBundles.WriteAsync(path, _sync.BuildLocal());
            Notice($"Saved {Path.GetFileName(path)}. It holds choices only: no passwords or tokens.", NoticeKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice("Couldn't save: " + ex.Message, NoticeKind.Error);
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = Platform.PickOpenFile("Open a C.A.R.S. setup", "C.A.R.S. setup (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        var bundle = await SetupBundles.ReadAsync(path);
        if (bundle is null) { Notice("That isn't a C.A.R.S. setup file (or it's from a newer version).", NoticeKind.Error); return; }
        await TakeOverAsync(bundle);
    }

    private void Notice(string text, NoticeKind kind)
    {
        Banner = text;
        BannerKind = kind;
    }
}
