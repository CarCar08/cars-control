using System.IO;
using System.Text.Json;
using System.Windows;
using CarsControl.Core.Inventory;
using CarsControl.Core.Models;

namespace CarsControl.App.Services;

/// <summary>Holds the latest inventory snapshot for every page. Loads in the background, then
/// measures install-folder sizes and reports each one as it lands.</summary>
public sealed class InventoryStore
{
    private readonly InventoryService _service = new();
    private CancellationTokenSource? _cts;

    public ProtectedApps Protected
    {
        get => _service.Protected;
        set => _service.Protected = value;
    }

    public InventorySnapshot? Current { get; private set; }
    public bool IsLoading { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread after a new snapshot is loaded.</summary>
    public event Action? Updated;

    /// <summary>Raised on the UI thread when an app's measured size arrives.</summary>
    public event Action<AppEntry>? SizeMeasured;

    public event Action<bool>? LoadingChanged;

    private static string CachePath => Path.Combine(Core.Util.UserData.Root, "inventory.json");

    /// <summary>Shows the last scan immediately on startup (the live scan takes ~5-10 s), then refreshes.</summary>
    public bool LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            var snap = JsonSerializer.Deserialize<InventorySnapshot>(File.ReadAllText(CachePath));
            if (snap is null || snap.Apps.Count == 0) return false;
            Current = snap;
            Updated?.Invoke();
            return true;
        }
        catch
        {
            return false; // stale format after an upgrade: just rescan
        }
    }

    private void SaveCache(InventorySnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snap with { Winget = [] }));
        }
        catch
        {
            // Cache is a convenience only.
        }
    }

    private Task? _inFlight;
    public void Stop() => _cts?.Cancel();

    /// <summary>Starts a scan, or joins the one already running (the Refresh page used to get an empty
    /// snapshot when it asked while the startup scan was in progress).</summary>
    public Task RefreshAsync()
    {
        if (App.IsDemo) return Task.CompletedTask; // screenshots keep the seeded demo app list
        if (_inFlight is { IsCompleted: false }) return _inFlight;
        return _inFlight = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetLoading(true);
        try
        {
            var previous = Current?.Apps;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fresh = await _service.LoadAsync(ct);
            var carried = InventoryService.CarryOverSizes(fresh.Apps, previous);
            PerfLog.Duration($"inventory scan ({fresh.Apps.Count} apps, {carried} sizes reused)", sw.Elapsed);
            Current = fresh;
            LastError = Current.WingetError;
            Updated?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            Updated?.Invoke();
        }
        finally
        {
            SetLoading(false);
        }

        if (Current is { } snap)
        {
            var dispatcher = Application.Current.Dispatcher;
            BackgroundTasks.Run(Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var measured = 0;
                await InventoryService.MeasureSizesAsync(snap.Apps, a =>
                {
                    Interlocked.Increment(ref measured);
                    if (!dispatcher.HasShutdownStarted && !App.IsShuttingDown) dispatcher.BeginInvoke(() => SizeMeasured?.Invoke(a));
                }, ct);
                PerfLog.Duration($"folder sizes ({measured} measured)", sw.Elapsed);
                SaveCache(snap);
            }, ct), "inventory folder sizes");
        }
    }

    private void SetLoading(bool value)
    {
        IsLoading = value;
        LoadingChanged?.Invoke(value);
    }
}
