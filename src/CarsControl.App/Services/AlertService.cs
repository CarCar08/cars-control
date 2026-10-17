using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CarsControl.Core.Alerts;
using CarsControl.Core.Data;
using CarsControl.Core.Maintenance;
using CarsControl.Core.Settings;

namespace CarsControl.App.Services;

/// <summary>
/// Phone alerts through ntfy (Settings › Phone alerts). Watches for: scheduled maintenance results and
/// failures, admin items waiting, low disk space, a drive filling fast, blue screens and unexpected
/// shutdowns (System event log), drive health (filled in when health monitoring arrives), and a weekly
/// "updates waiting" digest. Each kind is sent at most once a day per drive/event; during quiet hours
/// alerts are held and sent when they end, except critical ones.
/// </summary>
public sealed class AlertService
{
    private const string StateKey = "alerts_state_v1";
    private const long GiB = 1024L * 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly UpdateCenter _updates;
    private readonly CarsDb _db;
    private readonly NtfyClient _ntfy = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly List<Alert> _held = [];
    private readonly State _state;
    private DateTime _lastCrashCheck = DateTime.MinValue;

    private sealed class State
    {
        public Dictionary<string, DateTime> LastSent { get; set; } = new();
        public DateTime? CrashCheckedUtc { get; set; }
        public Dictionary<string, Baseline> Drives { get; set; } = new();
    }

    private sealed record Baseline(DateTime At, long FreeBytes);

    /// <summary>"Last alert sent 14:02" / "Last alert failed: …" (Settings).</summary>
    public string? LastResult { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public AlertService(AppSettings settings, MaintenanceService maintenance, UpdateCenter updates, CarsDb db)
    {
        _settings = settings;
        _updates = updates;
        _db = db;
        _state = Load();
        var now = DateTime.Now;
        // First start: only what happens from now on (no alerts for last month's crashes).
        _state.CrashCheckedUtc ??= DateTime.UtcNow;
        if (!_state.LastSent.ContainsKey("digest")) _state.LastSent["digest"] = now;
        Save();
        maintenance.Finished += OnMaintenanceFinished;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private AlertSettings S => _settings.Store.Current.Alerts;

    /// <summary>Test copies share your settings, so they never send by themselves (CARS_DEV_ALERTS=1 allows it).</summary>
    public void Start()
    {
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_ALERTS") != "1") return;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Settings › Send test: sends even before alerts are switched on, so you can check first.</summary>
    public async Task<(bool Ok, string Message)> SendTestAsync()
    {
        var r = await SendAsync(AlertMessages.Test(S.DeviceLabel));
        return r;
    }

    /// <summary>For drive health monitoring (round 6): call when a drive reports a problem.</summary>
    public Task ReportDriveHealthAsync(char drive, string status) =>
        RaiseAsync(AlertMessages.DriveHealth(S.DeviceLabel, drive, status), $"health:{drive}", TimeSpan.FromHours(24));

    /// <summary>The processor or graphics card stayed above your limit for several readings in a row.
    /// <paramref name="what"/> is "processor" or "graphics card". At most once every 6 hours.</summary>
    public Task ReportTemperatureAsync(string what, int degrees, int limit) =>
        RaiseAsync(AlertMessages.Temperature(S.DeviceLabel, what, degrees, limit), $"temperature:{what}", TimeSpan.FromHours(6));

    /// <summary>A backup job ended with problems (not when you ran it yourself). At most twice a day.</summary>
    public Task ReportBackupProblemAsync() =>
        RaiseAsync(AlertMessages.BackupProblem(S.DeviceLabel), "backup-failed", TimeSpan.FromHours(12));

    private async void OnMaintenanceFinished(MaintenanceState m)
    {
        // "Run now" happens with you at the PC: nothing to tell your phone.
        if (m.LastOrigin?.Contains("run now", StringComparison.OrdinalIgnoreCase) == true) return;
        var alert = AlertMessages.MaintenanceSummary(S.DeviceLabel, m.Updated, m.Failed, m.FreedBytes, m.Waiting);
        if (AlertPolicy.Wanted(alert, S)) await RaiseAsync(alert, null, null);
        else if (m.Waiting > 0) await RaiseAsync(AlertMessages.ApprovalsWaiting(S.DeviceLabel, m.Waiting), "approval", TimeSpan.FromHours(12));
    }

    private async Task TickAsync()
    {
        _timer.Interval = TimeSpan.FromMinutes(5);
        if (!S.Enabled) return;
        try
        {
            await FlushHeldAsync();
            await CheckDrivesAsync();
            if (DateTime.Now - _lastCrashCheck > TimeSpan.FromMinutes(30)) await CheckCrashesAsync();
            await CheckDigestAsync();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("alerts", ex);
        }
    }

    private async Task CheckDrivesAsync()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var letter = char.ToUpperInvariant(d.Name[0]);
            var free = d.TotalFreeSpace;
            var pct = d.TotalSize == 0 ? 100 : free * 100.0 / d.TotalSize;
            // Low = under the GB threshold AND under the percentage: 27 GB free on a 1 TB drive is 3%, but
            // not "low"; 15 GB free on a 120 GB drive is.
            if ((free < S.LowDiskGb * GiB && pct < Math.Max(S.LowDiskPercent, 1)) || free < 2 * GiB)
                await RaiseAsync(AlertMessages.LowDisk(S.DeviceLabel, letter, free, pct), $"low-disk:{letter}", TimeSpan.FromHours(24));

            var key = letter.ToString();
            if (!_state.Drives.TryGetValue(key, out var b) || DateTime.Now - b.At > TimeSpan.FromHours(24) || free > b.FreeBytes)
            {
                _state.Drives[key] = new Baseline(DateTime.Now, free);
                Save();
            }
            else if (b.FreeBytes - free >= S.DriveFillingFastGb * GiB)
            {
                await RaiseAsync(AlertMessages.DriveFillingFast(S.DeviceLabel, letter, b.FreeBytes - free), $"filling:{letter}", TimeSpan.FromHours(24));
                _state.Drives[key] = new Baseline(DateTime.Now, free);
                Save();
            }
        }
    }

    private async Task CheckCrashesAsync()
    {
        _lastCrashCheck = DateTime.Now;
        var since = _state.CrashCheckedUtc ?? DateTime.UtcNow;
        var checkedAt = DateTime.UtcNow;
        var incidents = await CrashEvents.ReadAsync(since);
        _state.CrashCheckedUtc = checkedAt;
        Save();
        foreach (var i in incidents)
            await RaiseAsync(i.BlueScreen ? AlertMessages.BlueScreen(S.DeviceLabel, i.At) : AlertMessages.UnexpectedShutdown(S.DeviceLabel, i.At), null, null);
    }

    /// <summary>Once a week, if updates are waiting and nothing was updated in that week.</summary>
    private async Task CheckDigestAsync()
    {
        if (_state.LastSent.TryGetValue("digest", out var last) && DateTime.Now - last < TimeSpan.FromDays(7)) return;
        var found = _updates.Found.Count;
        var lastUpdate = _db.ListHistory(300).Where(h => h.Kind == "update" && h.Status == "Succeeded").Select(h => (DateTime?)h.Started).FirstOrDefault();
        var days = lastUpdate is { } t ? (int)(DateTime.Now - t).TotalDays : 7;
        if (found > 0 && days >= 7)
            await RaiseAsync(AlertMessages.WeeklyDigest(S.DeviceLabel, found, days), "digest", TimeSpan.FromDays(7));
        else
        {
            _state.LastSent["digest"] = DateTime.Now;
            Save();
        }
    }

    private async Task RaiseAsync(Alert alert, string? key, TimeSpan? every)
    {
        if (!S.Enabled || !AlertPolicy.Wanted(alert, S)) return;
        if (key is not null && every is { } gap && _state.LastSent.TryGetValue(key, out var last) && DateTime.Now - last < gap) return;
        if (key is not null)
        {
            _state.LastSent[key] = DateTime.Now;
            Save();
        }
        if (!AlertPolicy.SendNow(alert, S, DateTime.Now))
        {
            if (!_held.Any(h => h.Title == alert.Title)) _held.Add(alert);
            return;
        }
        await SendAsync(alert);
    }

    private async Task FlushHeldAsync()
    {
        if (_held.Count == 0 || AlertPolicy.InQuietHours(S, DateTime.Now)) return;
        var batch = _held.ToList();
        _held.Clear();
        foreach (var a in batch) await SendAsync(a);
    }

    private async Task<(bool Ok, string Message)> SendAsync(Alert alert)
    {
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_ALERTS") != "1")
            return (false, "Test copy: real phone notifications are disabled.");
        var (ok, message) = await _ntfy.SendAsync(S, alert, Dpapi.Unprotect(S.ProtectedToken));
        LastResult = ok ? $"Last alert sent {DateTime.Now:ddd HH:mm}: {alert.Title}" : $"Last alert couldn't be sent: {message}";
        if (!ok) ErrorReporter.Log("alert send", new InvalidOperationException(message));
        Changed?.Invoke();
        return (ok, message);
    }

    private State Load()
    {
        try
        {
            if (_db.GetSetting(StateKey) is { Length: > 0 } json) return JsonSerializer.Deserialize<State>(json) ?? new();
        }
        catch (JsonException)
        {
            // Start over.
        }
        return new State();
    }

    private void Save() => _db.SetSetting(StateKey, JsonSerializer.Serialize(_state));
}
