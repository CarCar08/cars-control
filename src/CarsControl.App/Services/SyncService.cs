using System.IO;
using System.Windows.Threading;
using CarsControl.Core.Alerts;
using CarsControl.Core.Backup;
using CarsControl.Core.Data;
using CarsControl.Core.Health;
using CarsControl.Core.Inventory;
using CarsControl.Core.Storage;
using CarsControl.Core.Winget;

namespace CarsControl.App.Services;

public sealed record SyncOutcome(bool Ok, string Message);

/// <summary>
/// Keeps your setup and a short status of this PC in one cloud folder (Proton Drive's "C.A.R.S." folder by default).
/// The cloud client moves the files; this only reads and writes ordinary files in that folder. It never overwrites a
/// setup that changed on both PCs: that's shown as a conflict for you to settle.
/// </summary>
public sealed class SyncService
{
    private readonly AppSettings _settings;
    private readonly CarsDb _db;
    private readonly InventoryStore _inventory;
    private readonly UpdateCenter _updates;
    private readonly MaintenanceService _maintenance;
    private readonly GameMode _game;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();

    public SyncService(AppSettings settings, CarsDb db, InventoryStore inventory, UpdateCenter updates, MaintenanceService maintenance, GameMode game)
    {
        _settings = settings;
        _db = db;
        _inventory = inventory;
        _updates = updates;
        _maintenance = maintenance;
        _game = game;
        _timer.Tick += async (_, _) => await AutoAsync();
    }

    public event Action? Changed;

    public string? LastMessage { get; private set; }

    public static string DeviceName => App.PcName;

    private Core.Settings.SyncSettings S => _settings.Store.Current.Sync;

    /// <summary>The folder in use: the one you chose, or Proton Drive's C.A.R.S. folder.</summary>
    public string? Folder => !string.IsNullOrWhiteSpace(S.Folder) ? S.Folder : SetupSync.DefaultFolder(CloudSync.Roots());

    public string SetupPath => Path.Combine(Folder ?? "", SetupSync.SetupFileName);

    public void Start()
    {
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_SYNC") != "1") return;
        _timer.Start();
        BackgroundTasks.Run(FirstSyncAsync(), "initial sync");
    }

    private async Task FirstSyncAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(3), _lifetime.Token);
        if (!_lifetime.IsCancellationRequested) await AutoAsync();
    }

    public void Stop() { _timer.Stop(); _lifetime.Cancel(); }

    // ---------- the local setup ----------

    public SetupBundle BuildLocal(SetupParts? parts = null) =>
        SetupBundles.Build(_settings.Store.Current, _db.IgnoreRules(), AppsFromInventory(), parts ?? SetupSync.PartsOf(S), DeviceName, AppVersion);

    public IReadOnlyList<AppListEntry> AppsFromInventory() =>
        _inventory.Current?.Apps.Where(a => a.WingetId is not null && a.WingetSource is "winget" or "msstore").Select(a => new AppListEntry(a.WingetId!, a.Name, a.WingetSource!)).DistinctBy(a => a.Id).ToList() ?? [];

    public static string AppVersion => typeof(SyncService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

    public ApplyReport Apply(SetupBundle bundle, SetupParts parts)
    {
        ApplyReport report = null!;
        _settings.Store.Update(s => report = SetupBundles.Apply(s, bundle, parts, i => _db.SetIgnore(i.Key, i.Version)));
        _updates.IgnoreRulesChanged();
        return report;
    }

    // ---------- push, pull, auto ----------

    public async Task<SyncOutcome> PushAsync(CancellationToken ct = default)
    {
        if (Folder is not { } folder) return new(false, "No sync folder yet: Proton Drive isn't installed, or pick a folder.");
        await _gate.WaitAsync(ct);
        try
        {
            var bundle = BuildLocal();
            await SetupBundles.WriteAsync(SetupPath, bundle, ct);
            var hash = SetupBundles.ContentHash(bundle.Content);
            _settings.Store.Update(s => { s.Sync.LastHash = hash; s.Sync.LastSyncUtc = DateTime.UtcNow; });
            return Done(true, $"Sent this PC's setup to {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(false, "Couldn't write to the sync folder: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SetupBundle?> ReadRemoteAsync(CancellationToken ct = default) =>
        Folder is null ? null : await SetupBundles.ReadAsync(SetupPath, ct);

    public async Task<SyncOutcome> PullAsync(SetupBundle remote, SetupParts parts, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var report = Apply(remote, parts);
            // What was applied now matches the folder.
            _settings.Store.Update(s => { s.Sync.LastHash = SetupBundles.ContentHash(remote.Content); s.Sync.LastSyncUtc = DateTime.UtcNow; });
            _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "sync", "setup:pull", "Setup from " + remote.Device, "Backup & sync", null, null, false, "Succeeded", 0,
                report.AnythingChanged ? "Took over: " + string.Join(", ", report.Changed) : "Nothing was different", report.Skipped.Count > 0 ? "Skipped: " + string.Join("; ", report.Skipped) : null));
            return Done(true, report.AnythingChanged ? $"Took over the setup from {remote.Device}: {string.Join(", ", report.Changed)}." : $"{remote.Device}'s setup matches this PC's.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What a sync would do right now, without doing it.</summary>
    public async Task<(SyncAction Action, SetupBundle? Remote)> CheckAsync(CancellationToken ct = default)
    {
        var remote = await ReadRemoteAsync(ct);
        var local = SetupBundles.ContentHash(BuildLocal().Content);
        var action = SetupSync.Decide(local, S.LastHash, remote is null ? null : SetupBundles.ContentHash(remote.Content));
        return (action, remote);
    }

    private async Task AutoAsync()
    {
        if (!S.Enabled || _game.IsOn || _lifetime.IsCancellationRequested) return;
        try
        {
            if (Folder is null) return;
            var (action, remote) = await CheckAsync();
            switch (action)
            {
                case SyncAction.Push: await PushAsync(); break;
                case SyncAction.Pull when remote is not null: await PullAsync(remote, SetupSync.PartsOf(S)); break;
                case SyncAction.Conflict: Done(false, "This PC and another changed their setup since the last sync. Open Backup & sync › Your setup to choose which to keep."); break;
            }
            if (S.PublishStatus) await PublishStatusAsync();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("sync", ex);
        }
    }

    private SyncOutcome Done(bool ok, string message)
    {
        LastMessage = message;
        Platform.OnUiThread(() => Changed?.Invoke());
        return new SyncOutcome(ok, message);
    }

    // ---------- your PCs ----------

    public async Task PublishStatusAsync(CancellationToken ct = default)
    {
        if (Folder is not { } folder) return;
        try
        {
            var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            var week = await CrashLog.ReadAsync(TimeSpan.FromDays(7), ct);
            var drives = await Task.Run(() => DriveHealthReader.Read(false), ct);
            var warnings = drives.Count(d => DriveHealthRules.Evaluate(d).Level != DriveLevel.Good);
            var specs = await Task.Run(SystemSpecsReader.Read, ct);
            var jobs = _settings.Store.Current.Backup.Jobs.Where(j => j.LastRunUtc is not null).OrderByDescending(j => j.LastRunUtc).FirstOrDefault();
            var status = new FleetStatus(DeviceName, AppVersion, DateTime.UtcNow, $"{specs.WindowsEdition} {specs.WindowsVersion}", _updates.Found.Count, system.AvailableFreeSpace, system.TotalSize, warnings,
                week.BlueScreens.Count, _maintenance.State.LastRun?.ToUniversalTime(), jobs?.LastRunUtc, jobs?.LastOk == true ? "ok" : jobs is null ? null : "problems", specs.Uptime.TotalHours);
            await FleetFiles.WriteAsync(folder, status, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder isn't reachable right now.
        }
    }

    public Task<IReadOnlyList<FleetEntry>> ReadFleetAsync(CancellationToken ct = default) =>
        Folder is { } f ? FleetFiles.ReadAllAsync(f, DeviceName, DateTime.UtcNow, ct) : Task.FromResult<IReadOnlyList<FleetEntry>>([]);
}
