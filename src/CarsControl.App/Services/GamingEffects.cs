using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CarsControl.App.Converters;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;

namespace CarsControl.App.Services;

/// <summary>
/// What Gaming mode does to Windows, and the session log. At game start: frees standby memory, pauses
/// Windows Update and Windows Search indexing; afterwards: resumes both. These need admin, so they run
/// over the admin session you approved once at sign-in (kept alive with a ping while automatic Gaming mode
/// is on). Without a session they're skipped and the log says so: never a prompt mid-game.
/// </summary>
public sealed class GamingEffects
{
    private const string PendingKey = "gaming_pending_v1";

    private readonly GameMode _mode;
    private readonly AppSettings _settings;
    private readonly HelperClient _helper;
    private readonly CarsDb _db;
    private readonly MaintenanceService _maintenance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _keepAlive = new();
    private DispatcherTimer? _signInDelay;
    private Pending _pending;
    private Session? _session;
    private bool _wasOn;

    /// <summary>What was paused and still needs resuming (survives a crash or restart).</summary>
    private sealed class Pending
    {
        public bool WindowsUpdatePaused { get; set; }
        public bool SearchStopped { get; set; }
    }

    private sealed class Session
    {
        public DateTime Start { get; } = DateTime.Now;
        public List<string> Games { get; } = [];
        public List<string> Done { get; } = [];
        public bool AdminSkipped { get; set; }
    }

    /// <summary>"Last session: Elden Ring, 1 h 42 min — …" (Settings).</summary>
    public string? LastSessionText { get; private set; }

    /// <summary>The sign-in approval was declined this session.</summary>
    public bool ApprovalDeclined { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public GamingEffects(GameMode mode, AppSettings settings, HelperClient helper, CarsDb db, MaintenanceService maintenance)
    {
        _mode = mode;
        _settings = settings;
        _helper = helper;
        _db = db;
        _maintenance = maintenance;
        _pending = Load();
        _mode.Changed += async () => await OnModeChangedAsync();
        // A pause left over from a crash or restart is undone as soon as an admin session exists.
        _helper.ConnectionChanged += connected =>
        {
            if (connected && !_mode.IsOn && (_pending.WindowsUpdatePaused || _pending.SearchStopped))
                Platform.OnUiThread(() => _ = ResumeAsync(null));
        };
        _keepAlive.Tick += async (_, _) =>
        {
            // The idle timeout can be changed in Settings at any time: follow it.
            var wanted = KeepAliveInterval();
            if (_keepAlive.Interval != wanted) _keepAlive.Interval = wanted;
            if (KeepsSession) await _helper.PingIfConnectedAsync();
        };
    }

    private Core.Settings.GamingSettings Gaming => _settings.Store.Current.Gaming;

    private bool NeedsAdmin => Gaming.PauseWindowsUpdate || Gaming.PauseSearchIndexing || Gaming.FreeStandbyMemory;

    /// <summary>Automatic Gaming mode with sign-in approval keeps the admin session from idling out.</summary>
    private bool KeepsSession => Gaming.Automatic && Gaming.ApproveAtSignIn && NeedsAdmin;

    public bool SessionActive => _helper.IsConnected;

    /// <summary>Half the helper's idle timeout (at most 10 minutes), so the session never idles out.</summary>
    private TimeSpan KeepAliveInterval() => TimeSpan.FromMinutes(Math.Clamp(_helper.IdleMinutes / 2.0, 0.5, 10));

    public void Start(bool atSignIn)
    {
        _keepAlive.Interval = KeepAliveInterval();
        _keepAlive.Start();
        if (atSignIn && KeepsSession && !App.IsDevInstance)
        {
            // Once the desktop has settled after sign-in: the one admin prompt of the day.
            var delay = _signInDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            delay.Tick += async (_, _) =>
            {
                delay.Stop();
                await ApproveSessionAsync();
            };
            delay.Start();
        }
    }

    public void Stop() { _keepAlive.Stop(); _signInDelay?.Stop(); }

    /// <summary>Starts the admin session now (one UAC prompt). Settings › Gaming mode › Approve now, and sign-in.</summary>
    public async Task<bool> ApproveSessionAsync()
    {
        try
        {
            await _helper.SendAsync(new PingOp());
            ApprovalDeclined = false;
            return true;
        }
        catch (HelperCancelledException)
        {
            ApprovalDeclined = true;
            return false;
        }
        catch (HelperUnavailableException ex)
        {
            ErrorReporter.Log("gaming approval", ex);
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private async Task OnModeChangedAsync()
    {
        if (_mode.IsOn && !_wasOn)
        {
            _wasOn = true;
            _session = new Session();
            if (_mode.DetectedGame is { } g) _session.Games.Add(g);
            await BeginAsync(_session);
        }
        else if (!_mode.IsOn && _wasOn)
        {
            _wasOn = false;
            var s = _session;
            _session = null;
            await ResumeAsync(s);
            if (s is not null) Log(s);
        }
        else if (_mode.DetectedGame is { } g && _session is not null && !_session.Games.Contains(g))
        {
            _session.Games.Add(g);
        }
    }

    private async Task BeginAsync(Session s)
    {
        await _gate.WaitAsync();
        try
        {
            if (Gaming.FreeStandbyMemory && await SendAsync(new PurgeStandbyListOp(), s) is { Ok: true } purge
                && long.TryParse(purge.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var freed) && freed > 0)
                s.Done.Add($"freed {Format.Bytes(freed)} of standby memory");

            if (Gaming.PauseWindowsUpdate && await SendAsync(new WindowsUpdatePauseOp(true), s) is { Ok: true } wu)
            {
                if (wu.Data == "paused") _pending.WindowsUpdatePaused = true;
                s.Done.Add(wu.Data == "paused" ? "paused Windows Update" : "Windows Update was already paused");
            }

            if (Gaming.PauseSearchIndexing && await SendAsync(new ServiceControlOp("WSearch", Start: false), s) is { Ok: true } ws)
            {
                if (ws.Data == "was-running")
                {
                    _pending.SearchStopped = true;
                    s.Done.Add("paused search indexing");
                }
            }
            Save();
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>At exit: whatever Gaming mode paused is resumed before the admin session closes.</summary>
    public Task ResumeAtExitAsync() => _pending.WindowsUpdatePaused || _pending.SearchStopped ? ResumeAsync(null) : Task.CompletedTask;

    /// <summary>Undoes only what Gaming mode paused.</summary>
    private async Task ResumeAsync(Session? s)
    {
        await _gate.WaitAsync();
        try
        {
            if (_pending.WindowsUpdatePaused && await SendAsync(new WindowsUpdatePauseOp(false), s) is { Ok: true })
                _pending.WindowsUpdatePaused = false;
            if (_pending.SearchStopped && await SendAsync(new ServiceControlOp("WSearch", Start: true), s) is { Ok: true })
                _pending.SearchStopped = false;
            Save();
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Over the approved session only: Gaming mode never shows a prompt.</summary>
    private async Task<OpResult?> SendAsync(HelperOp op, Session? s)
    {
        try
        {
            return await _helper.SendIfConnectedAsync(op, dryRun: HelperClient.DevUnelevated);
        }
        catch (HelperSessionClosedException)
        {
            if (s is not null) s.AdminSkipped = true;
            return null;
        }
        catch (Exception ex) when (ex is HelperUnavailableException or IOException)
        {
            ErrorReporter.Log($"gaming {op.GetType().Name}", ex);
            return null;
        }
    }

    private void Log(Session s)
    {
        var length = DateTime.Now - s.Start;
        if (length < TimeSpan.FromMinutes(1) && s.Done.Count == 0) return; // a blip, not a session
        var title = s.Games.Count == 0 ? "Gaming mode (switched on by you)" : string.Join(", ", s.Games);
        var parts = new List<string> { Duration(length) };
        parts.AddRange(s.Done);
        if (s.AdminSkipped) parts.Add("admin parts skipped (no admin session — approve it at sign-in or in Settings)");
        if (_maintenance.State.PostponedForGame) parts.Add("postponed maintenance runs once you're idle");
        var message = string.Join(" · ", parts);
        LastSessionText = $"Last session: {title}, {message}";
        _db.AddHistory(new HistoryEntry(0, s.Start, DateTime.Now, "gaming", "gaming", title, "Gaming mode", null, null,
            !s.AdminSkipped && s.Done.Count > 0, "Succeeded", 0, message, null));
    }

    internal static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    private Pending Load()
    {
        try
        {
            if (_db.GetSetting(PendingKey) is { Length: > 0 } json) return JsonSerializer.Deserialize<Pending>(json) ?? new();
        }
        catch (JsonException)
        {
            // Start over.
        }
        return new Pending();
    }

    private void Save() => _db.SetSetting(PendingKey, JsonSerializer.Serialize(_pending));
}
