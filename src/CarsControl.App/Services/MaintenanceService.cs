using System.Text.Json;
using System.Windows.Threading;
using CarsControl.Core.Cleanup;
using CarsControl.Core.Data;
using CarsControl.Core.Maintenance;
using CarsControl.Core.Settings;
using CarsControl.Core.Updates;

namespace CarsControl.App.Services;

/// <summary>
/// Scheduled maintenance and the approval queue. Every 30 seconds it checks whether a run is due
/// (on time, or missed while the PC was off or asleep) and starts it once no game is running and you've
/// been idle for a few minutes. Runs are unattended: your-account work happens as you, admin work uses an
/// approved session if one is open and otherwise waits for one approval when you're back.
/// </summary>
public sealed class MaintenanceService
{
    private const string StateKey = "maintenance_state_v1";

    private readonly AppSettings _settings;
    private readonly UpdateCenter _center;
    private readonly UpdateRunner _updates;
    private readonly CleanupRunner _cleanup;
    private readonly ApprovalQueue _queue;
    private readonly CarsDb _db;
    private readonly GameMode _game;
    private readonly AttentionService _attention;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _scheduleSignature;
    private string? _toastedSignature;

    public MaintenanceState State { get; private set; }

    public bool IsRunning { get; private set; }

    public ApprovalQueue Approvals => _queue;

    /// <summary>Raised on the UI thread when the state, the schedule or the approval queue changes.</summary>
    public event Action? Changed;

    /// <summary>A run finished (phone alerts listen).</summary>
    public event Action<MaintenanceState>? Finished;

    /// <summary>Admin items are waiting and you're back (phone alerts listen).</summary>
    public event Action<int>? ApprovalsWaiting;

    public MaintenanceService(AppSettings settings, UpdateCenter center, UpdateRunner updates, CleanupRunner cleanup,
        ApprovalQueue queue, CarsDb db, GameMode game, AttentionService attention)
    {
        _attention = attention;
        _settings = settings;
        _center = center;
        _updates = updates;
        _cleanup = cleanup;
        _queue = queue;
        _db = db;
        _game = game;
        State = Load();
        // First start with maintenance: don't run last Sunday's "missed" run right away.
        if (State.CoveredUntil is null)
        {
            State.CoveredUntil = DateTime.Now;
            Save();
        }
        _scheduleSignature = Signature(Settings);
        _settings.Store.Saved += OnSettingsSaved;
        _queue.Changed += () =>
        {
            UpdateAttention();
            Changed?.Invoke();
        };
        UpdateAttention();
        _game.Changed += () => Changed?.Invoke();
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private MaintenanceSettings Settings => _settings.Store.Current.Maintenance;

    /// <summary>Test copies share your settings and history, so they never start scheduled runs by
    /// themselves (CARS_DEV_MAINTENANCE=1 allows it for testing the scheduler).</summary>
    public void Start()
    {
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_MAINTENANCE") != "1") return;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public DateTime? NextRun => MaintenanceSchedule.Next(Settings, DateTime.Now);

    /// <summary>"Next: Sun 03:00" / "Off" / "Due — waiting until you're idle" / "Waits until your game ends".</summary>
    public string NextText
    {
        get
        {
            if (!Settings.Enabled) return "Scheduled maintenance is off";
            if (IsRunning) return "Running now…";
            if (MaintenanceSchedule.Due(Settings, State, DateTime.Now) is not null)
                return _game.IsOn ? "Due — waits until your game ends" : $"Due — starts after {Settings.IdleMinutes} min without input";
            return NextRun is { } next ? $"Next: {MaintenanceSchedule.When(next, DateTime.Now)}" : "No days chosen";
        }
    }

    /// <summary>"Last: Sun 03:00 — 5 updated, 2.1 GB cleaned, 3 waiting for approval".</summary>
    public string LastText =>
        State.LastRun is { } last
            ? $"Last maintenance {MaintenanceSchedule.When(last, DateTime.Now)} — {MaintenanceSchedule.Describe(State.Updated, State.Failed, State.FreedBytes, _queue.Count)}"
            : "Maintenance hasn't run yet";

    private static string Signature(MaintenanceSettings m) => $"{m.Enabled}|{string.Join(",", m.Days)}|{m.Time}";

    /// <summary>Changing the schedule never triggers a "missed" run for a time that just moved behind you.</summary>
    private void OnSettingsSaved()
    {
        var sig = Signature(Settings);
        if (sig == _scheduleSignature) return;
        _scheduleSignature = sig;
        State.CoveredUntil = DateTime.Now;
        State.PostponedForGame = false;
        Save();
        Platform.OnUiThread(() => Changed?.Invoke());
    }

    private async Task TickAsync()
    {
        try
        {
            // You're back: one toast per set of waiting admin items.
            if (_queue.Count > 0 && !IsRunning && !_game.IsOn && UserIdle.Current < TimeSpan.FromMinutes(1)
                && _queue.Signature != _toastedSignature)
            {
                _toastedSignature = _queue.Signature;
                Notifier.ApprovalsWaiting(_queue.Count, _queue.Describe());
                ApprovalsWaiting?.Invoke(_queue.Count);
            }

            if (IsRunning || MaintenanceSchedule.Due(Settings, State, DateTime.Now) is not { } due) return;
            if (_game.IsOn)
            {
                if (!State.PostponedForGame)
                {
                    State.PostponedForGame = true;
                    Save();
                    Changed?.Invoke();
                }
                return;
            }
            if (State.PostponedForGame && !_settings.Store.Current.Gaming.MaintenanceAfterPlay)
            {
                // You chose not to run it after playing: skip to the next scheduled time.
                State.PostponedForGame = false;
                State.CoveredUntil = due;
                Save();
                Changed?.Invoke();
                return;
            }
            if (!MaintenanceSchedule.MayStart(Settings, UserIdle.Current, _game.IsOn) || UserIdle.FullScreenBusy) return;

            var origin = State.PostponedForGame ? "Maintenance after your game"
                : DateTime.Now - due > TimeSpan.FromMinutes(15) ? "Missed maintenance"
                : "Scheduled maintenance";
            await RunAsync(origin, unattended: true);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("maintenance tick", ex);
        }
    }

    /// <summary>Runs maintenance now. Unattended runs never prompt; "Run now" (attended) asks once for admin.</summary>
    public async Task<MaintenanceState?> RunAsync(string origin, bool unattended)
    {
        if (IsRunning || _updates.IsRunning || _cleanup.IsRunning) return null;
        IsRunning = true;
        Changed?.Invoke();
        var m = Settings;
        int updated = 0, failed = 0;
        long freed = 0;
        try
        {
            if (m.RunUpdates)
            {
                await _center.CheckAsync(force: true);
                var sources = m.UpdateSources;
                var items = _center.Found.Concat(_center.CheckActions)
                    .Where(c => c.SelectedByDefault && (sources is null || sources.Contains(c.Source.ToString())))
                    .ToList();
                if (items.Count > 0 && await _updates.RunAsync(items, new UpdateRunOptions { Origin = origin, Unattended = unattended }) is { } u)
                {
                    updated = u.Updated;
                    failed = u.Failed;
                    _queue.Add(u.Waiting, origin);
                }
            }

            if (m.RunCleanup)
            {
                var rules = (m.CleanupRules is { } ids ? CleanupRules.All.Where(r => ids.Contains(r.Id)) : CleanupRules.All.Where(r => r.DefaultOn)).ToList();
                if (await _cleanup.RunAsync(rules, origin, unattended: unattended) is { } c)
                {
                    freed = c.FreedBytes;
                    failed += c.Results.Count(r => r.Text.StartsWith("Failed", StringComparison.Ordinal));
                    _queue.Add(c.Waiting, origin);
                }
            }
        }
        finally
        {
            IsRunning = false;
        }

        State.LastRun = DateTime.Now;
        State.LastOrigin = origin;
        State.Updated = updated;
        State.Failed = failed;
        State.FreedBytes = freed;
        State.Waiting = _queue.Count;
        State.PostponedForGame = false;
        // Covers the scheduled time this run was for (and a "Run now" covers a pending missed one).
        State.CoveredUntil = DateTime.Now;
        Save();

        var summary = MaintenanceSchedule.Describe(State);
        _db.AddHistory(new HistoryEntry(0, State.LastRun.Value, DateTime.Now, "maintenance", "maintenance", origin, "Maintenance", null, null,
            false, failed > 0 ? "Failed" : "Succeeded", 0, summary, null));
        if (unattended) Notifier.MaintenanceDone(origin, summary);
        UpdateAttention();
        Changed?.Invoke();
        Finished?.Invoke(State);
        return State;
    }

    /// <summary>Runs everything in the approval queue with one admin prompt. Updates are matched to the
    /// latest check; ones that are no longer needed are dropped.</summary>
    public async Task ApproveAsync()
    {
        if (_queue.Count == 0 || IsRunning || _updates.IsRunning || _cleanup.IsRunning) return;
        IsRunning = true;
        Changed?.Invoke();
        try
        {
            var items = _queue.Items.ToList();
            var updates = items.Where(i => i.Kind == ApprovalItem.Update).ToList();
            if (updates.Count > 0)
            {
                await _center.CheckAsync(force: false);
                var current = _center.Found.Concat(_center.CheckActions).ToList();
                var matched = updates.Select(u => (Item: u, Candidate: current.FirstOrDefault(c => c.Key == u.Key))).ToList();
                _queue.Remove(matched.Where(x => x.Candidate is null).Select(x => x.Item)); // updated some other way, or gone
                var run = matched.Where(x => x.Candidate is not null).ToList();
                if (run.Count > 0
                    && await _updates.RunAsync(run.Select(x => x.Candidate!).ToList(), new UpdateRunOptions { Origin = "Approved", AskAboutOpenApps = AskAsync }) is { } s)
                {
                    var notApproved = s.Outcomes.Where(o => o.StatusText.Contains("approval was cancelled")).Select(o => o.Candidate.Key).ToHashSet();
                    _queue.Remove(run.Where(x => !notApproved.Contains(x.Candidate!.Key)).Select(x => x.Item));
                }
            }

            var cleanup = items.Where(i => i.Kind == ApprovalItem.Cleanup).ToList();
            var rules = cleanup.Select(i => CleanupRules.Find(i.Key)).OfType<CleanupRule>().ToList();
            _queue.Remove(cleanup.Where(i => CleanupRules.Find(i.Key) is null));
            if (rules.Count > 0 && await _cleanup.RunAsync(rules, "Approved") is { } c)
            {
                var notApproved = c.Results.Where(r => r.Text.Contains("approval was cancelled")).Select(r => r.Rule.Id).ToHashSet();
                _queue.Remove(cleanup.Where(i => !notApproved.Contains(i.Key)));
            }
        }
        finally
        {
            IsRunning = false;
            State.Waiting = _queue.Count;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Home's "Needs attention": admin items waiting, and a last run that had failures.</summary>
    private void UpdateAttention()
    {
        var waiting = _queue.Count;
        _attention.Set("approvals", waiting == 0 ? null : new AttentionItem
        {
            Key = $"approvals:{waiting}", // string hashes change every run; the count is stable
            Title = waiting == 1 ? "1 admin item is waiting for your approval" : $"{waiting} admin items are waiting for your approval",
            Detail = $"{_queue.Describe()} from scheduled maintenance. One admin prompt runs them all.",
            Icon = Wpf.Ui.Controls.SymbolRegular.ShieldKeyhole24,
            ActionText = "Approve",
            Action = ApproveAsync,
            SecondaryText = "Dismiss",
            Secondary = DismissApprovals,
        });
        _attention.Set("maintenance-failed", State.Failed == 0 || State.LastRun is null ? null : new AttentionItem
        {
            Key = $"maintenance-failed:{State.LastRun:yyyyMMddHHmm}",
            Title = State.Failed == 1 ? "1 item failed in the last maintenance" : $"{State.Failed} items failed in the last maintenance",
            Detail = $"{State.LastOrigin} on {MaintenanceSchedule.When(State.LastRun.Value, DateTime.Now)}. History has the reason for each.",
            Icon = Wpf.Ui.Controls.SymbolRegular.CalendarSync24,
            ActionText = "Show History",
            Action = () =>
            {
                Navigator.Go("history");
                return Task.CompletedTask;
            },
            SnoozeFor = TimeSpan.FromDays(7),
        });
    }

    private static Task<OpenAppsChoice> AskAsync(IReadOnlyList<UpdateCandidate> open) => Dialogs.AskAboutOpenAppsAsync(open.Select(c => c.Title));

    /// <summary>Forget the waiting admin items (they still show in Refresh and Cleanup).</summary>
    public void DismissApprovals()
    {
        _queue.Clear();
        State.Waiting = 0;
        Save();
    }

    private MaintenanceState Load()
    {
        try
        {
            if (_db.GetSetting(StateKey) is { Length: > 0 } json) return JsonSerializer.Deserialize<MaintenanceState>(json) ?? new();
        }
        catch (JsonException)
        {
            // Start over.
        }
        return new MaintenanceState();
    }

    private void Save() => _db.SetSetting(StateKey, JsonSerializer.Serialize(State));
}
