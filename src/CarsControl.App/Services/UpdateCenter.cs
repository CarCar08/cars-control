using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CarsControl.Core.Data;
using CarsControl.Core.Updates;

namespace CarsControl.App.Services;

/// <summary>Result of the latest check for one source.</summary>
public sealed class SourceState
{
    public UpdateSource Source { get; init; }
    public List<UpdateCandidate> Items { get; set; } = [];
    public string? Error { get; set; }
    public DateTime? CheckedAt { get; set; }
    public double ElapsedMs { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChecking { get; set; }
}

public sealed record IgnoredItem(string Key, string Title, string Rule);

/// <summary>
/// One source of truth for update results (Home, Refresh, tray, toasts used to count differently: Home
/// said 2 while Refresh said 10). Results persist in cars.db and each source has its own freshness:
///   winget / WSL: straight from the inventory scan (re-derived whenever the inventory refreshes)
///   Windows Update: 30 min · pip, npm, PowerShell modules: 6 h · Store / Office: always-present check actions
/// A background check runs ~20 s after launch when results are over an hour old, then every N hours;
/// it only notifies, it never installs.
/// </summary>
public sealed class UpdateCenter
{
    private readonly InventoryStore _inventory;
    private readonly CarsDb _db;
    private readonly AppSettings _settings;
    private readonly GameMode _game;
    private readonly Dictionary<UpdateSource, SourceState> _states = new();
    private bool _skippedForGame;
    private DispatcherTimer? _timer;
    private Task? _inFlight;

    private const string SettingKey = "update_results_v1";

    public static readonly UpdateSource[] Order =
        [UpdateSource.Winget, UpdateSource.WindowsUpdate, UpdateSource.Wsl, UpdateSource.PsModule, UpdateSource.Npm, UpdateSource.Pip, UpdateSource.Store, UpdateSource.Office];

    private static TimeSpan Ttl(UpdateSource s) => s switch
    {
        UpdateSource.WindowsUpdate => TimeSpan.FromMinutes(30),
        UpdateSource.Pip or UpdateSource.Npm or UpdateSource.PsModule => TimeSpan.FromHours(6),
        _ => TimeSpan.FromMinutes(5),
    };

    public static string SourceTitle(UpdateSource s) => s switch
    {
        UpdateSource.Winget => "Apps (winget)",
        UpdateSource.WindowsUpdate => "Windows Update",
        UpdateSource.Wsl => "WSL",
        UpdateSource.PsModule => "PowerShell modules",
        UpdateSource.Npm => "npm (global)",
        UpdateSource.Pip => "Python packages (pip)",
        UpdateSource.Store => "Microsoft Store",
        UpdateSource.Office => "Microsoft 365",
        _ => s.ToString(),
    };

    /// <summary>Raised on the UI thread whenever any source's results or checking state change.</summary>
    public event Action? Changed;

    public UpdateCenter(InventoryStore inventory, CarsDb db, AppSettings settings, GameMode game)
    {
        _inventory = inventory;
        _db = db;
        _settings = settings;
        _game = game;
        // Background checks wait while you play; one that was skipped runs right after.
        _game.Changed += async () =>
        {
            if (_game.IsOn || !_skippedForGame || _timer is not { IsEnabled: true }) return;
            _skippedForGame = false;
            await CheckAsync(force: false);
            NotifyIfNew();
        };
        foreach (var s in Order) _states[s] = new SourceState { Source = s };
        Load();
        _inventory.Updated += DeriveFromInventory;
        if (_inventory.Current is not null) DeriveFromInventory();
    }

    public SourceState State(UpdateSource s) => _states[s];

    public bool IsChecking => _states.Values.Any(s => s.IsChecking);

    /// <summary>When every real source was last checked (the oldest of them), or null if never.</summary>
    public DateTime? LastChecked
    {
        get
        {
            var real = _states.Values.Where(s => s.Source is not (UpdateSource.Store or UpdateSource.Office)).ToList();
            return real.All(s => s.CheckedAt is not null) ? real.Min(s => s.CheckedAt) : null;
        }
    }

    /// <summary>Discovered updates that aren't ignored (what every count in the app shows).</summary>
    public IReadOnlyList<UpdateCandidate> Found
    {
        get
        {
            var rules = _db.IgnoreRules();
            return Order.SelectMany(s => _states[s].Items)
                .Where(c => !c.IsCheckAction && !c.IsOptional && !CarsDb.IsIgnored(rules, c.Key, c.AvailableVersion))
                .ToList();
        }
    }

    /// <summary>Optional Windows Update items (mostly drivers Windows itself wouldn't install): shown apart, never counted.</summary>
    public IReadOnlyList<UpdateCandidate> Optional
    {
        get
        {
            var rules = _db.IgnoreRules();
            return Order.SelectMany(s => _states[s].Items).Where(c => c.IsOptional && !CarsDb.IsIgnored(rules, c.Key, c.AvailableVersion)).ToList();
        }
    }

    public IReadOnlyList<UpdateCandidate> CheckActions => [.. UpdateSources.Store(), .. UpdateSources.Office()];

    public IReadOnlyList<IgnoredItem> Ignored
    {
        get
        {
            var rules = _db.IgnoreRules();
            return Order.SelectMany(s => _states[s].Items)
                .Where(c => CarsDb.IsIgnored(rules, c.Key, c.AvailableVersion))
                .Select(c => new IgnoredItem(c.Key, c.Title, rules[c.Key].IsPermanent ? "Ignored permanently" : $"Skipping {rules[c.Key].Version}"))
                .ToList();
        }
    }

    public IEnumerable<UpdateCandidate> ItemsFor(UpdateSource s)
    {
        var rules = _db.IgnoreRules();
        return _states[s].Items.Where(c => !c.IsOptional && !CarsDb.IsIgnored(rules, c.Key, c.AvailableVersion));
    }

    /// <summary>Runs the stale sources (all of them when forced). Concurrent callers join the running check.</summary>
    public Task CheckAsync(bool force)
    {
        if (_inFlight is { IsCompleted: false }) return _inFlight;
        return _inFlight = CheckCoreAsync(force);
    }

    private async Task CheckCoreAsync(bool force)
    {
        var total = Stopwatch.StartNew();
        bool Stale(UpdateSource s) => force || _states[s].CheckedAt is not { } t || DateTime.Now - t > Ttl(s);

        var tasks = new List<Task>();
        if (Stale(UpdateSource.Winget) || Stale(UpdateSource.Wsl))
            tasks.Add(Run([UpdateSource.Winget, UpdateSource.Wsl], async () =>
            {
                await _inventory.RefreshAsync(); // DeriveFromInventory fills both from the new snapshot
            }));
        if (Stale(UpdateSource.WindowsUpdate)) tasks.Add(RunSource(UpdateSource.WindowsUpdate, () => UpdateSources.WindowsUpdateAsync(CancellationToken.None)));
        if (Stale(UpdateSource.PsModule)) tasks.Add(RunSource(UpdateSource.PsModule, () => UpdateSources.PsModulesAsync(CancellationToken.None)));
        if (Stale(UpdateSource.Npm)) tasks.Add(RunSource(UpdateSource.Npm, () => UpdateSources.NpmAsync(CancellationToken.None)));
        if (Stale(UpdateSource.Pip)) tasks.Add(RunSource(UpdateSource.Pip, () => UpdateSources.PipAsync(CancellationToken.None)));

        await Task.WhenAll(tasks);
        Save();
        PerfLog.Duration($"update check ({(force ? "forced" : "stale sources only")}, {tasks.Count} job(s), {Found.Count} found)", total.Elapsed);
    }

    private async Task Run(UpdateSource[] sources, Func<Task> work)
    {
        foreach (var s in sources) _states[s].IsChecking = true;
        RaiseChanged();
        try
        {
            await work();
        }
        finally
        {
            foreach (var s in sources) _states[s].IsChecking = false;
            RaiseChanged();
        }
    }

    private async Task RunSource(UpdateSource source, Func<Task<IReadOnlyList<UpdateCandidate>>> check)
    {
        var st = _states[source];
        st.IsChecking = true;
        RaiseChanged();
        var result = await UpdateSources.Timed(source, check);
        st.IsChecking = false;
        st.ElapsedMs = result.Elapsed.TotalMilliseconds;
        if (result.Error is null)
        {
            st.Items = result.Items.ToList();
            st.Error = null;
            st.CheckedAt = DateTime.Now;
        }
        else
        {
            st.Error = result.Error; // keep the last good items
        }
        PerfLog.Duration($"check {source} ({st.Items.Count} item(s){(result.Error is null ? "" : ", error")})", result.Elapsed);
        RaiseChanged();
    }

    /// <summary>winget and WSL updates come from the inventory scan, so they follow it automatically.</summary>
    private void DeriveFromInventory()
    {
        if (_inventory.Current is not { } snap) return;
        _states[UpdateSource.Winget].Items = UpdateSources.Winget(snap).ToList();
        _states[UpdateSource.Winget].CheckedAt = snap.TakenAt;
        _states[UpdateSource.Winget].Error = _inventory.LastError;
        _states[UpdateSource.Wsl].Items = UpdateSources.Wsl(snap).ToList();
        _states[UpdateSource.Wsl].CheckedAt = snap.TakenAt;
        RaiseChanged();
    }

    /// <summary>Drops items that were just updated, so counts drop immediately (the next scan confirms).</summary>
    public void MarkDone(IEnumerable<UpdateCandidate> done)
    {
        var keys = done.Select(d => d.Key).ToHashSet();
        foreach (var st in _states.Values) st.Items.RemoveAll(i => keys.Contains(i.Key));
        Save();
        RaiseChanged();
    }

    /// <summary>Call after changing ignore rules.</summary>
    public void IgnoreRulesChanged() => RaiseChanged();

    private void RaiseChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Changed?.Invoke();
        else d.BeginInvoke(() => Changed?.Invoke());
    }

    // ---------- persistence ----------

    private void Load()
    {
        try
        {
            if (_db.GetSetting(SettingKey) is not { Length: > 0 } json) return;
            var saved = JsonSerializer.Deserialize<List<SourceState>>(json);
            if (saved is null) return;
            foreach (var s in saved.Where(s => _states.ContainsKey(s.Source) && s.Source is not (UpdateSource.Winget or UpdateSource.Wsl)))
            {
                // pip results saved before round 4 lack the install location that decides admin vs. as-you; re-check them.
                if (s.Source == UpdateSource.Pip && s.Items.Any(i => i.Location is null)) continue;
                _states[s.Source] = s;
            }
        }
        catch
        {
            // Old format: start fresh.
        }
    }

    private void Save()
    {
        try
        {
            var toSave = _states.Values.Where(s => s.Source is not (UpdateSource.Winget or UpdateSource.Wsl)).ToList();
            _db.SetSetting(SettingKey, JsonSerializer.Serialize(toSave));
        }
        catch
        {
            // Persistence is a convenience.
        }
    }

    // ---------- background ----------

    public void StartBackground()
    {
        _timer?.Stop();
        var first = LastChecked is { } t && DateTime.Now - t < TimeSpan.FromHours(1) ? TimeSpan.FromHours(_settings.CheckIntervalHours) : TimeSpan.FromSeconds(20);
        _timer = new DispatcherTimer { Interval = first };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(_settings.CheckIntervalHours);
            if (_game.IsOn)
            {
                _skippedForGame = true;
                return;
            }
            await CheckAsync(force: false);
            NotifyIfNew();
        };
        _timer.Start();
    }

    public void StopBackground() => _timer?.Stop();

    /// <summary>One toast per new set of updates (never repeats for the same set). Drivers are left out.</summary>
    public void NotifyIfNew()
    {
        var items = Found.Where(i => !i.IsDriver).ToList();
        var signature = string.Join("|", items.Select(i => $"{i.Key}@{i.AvailableVersion}").OrderBy(s => s));
        if (items.Count == 0 || signature == _settings.LastNotified) return;
        _settings.LastNotified = signature;
        var detail = string.Join(", ", items.Take(3).Select(i => i.Title)) + (items.Count > 3 ? $" and {items.Count - 3} more" : "");
        Notifier.UpdatesReady(items.Count, detail, items.Count(i => i.NeedsAdmin));
    }
}
