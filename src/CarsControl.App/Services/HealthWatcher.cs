using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CarsControl.Core.Data;
using CarsControl.Core.Health;
using Wpf.Ui.Controls;

namespace CarsControl.App.Services;

/// <summary>
/// The quiet half of Health: every half hour it looks for the few things worth interrupting you about
/// (a restart that's waiting, a drive Windows reports as unhealthy, a recent blue screen) and puts them on
/// Home; and, if you turned it on, restarts the PC at a quiet time when a restart is waiting and you're away.
/// Nothing here shows a UAC prompt or runs while a game is on.
/// </summary>
public sealed class HealthWatcher
{
    private const string LastAttemptKey = "restart_last_attempt";
    private readonly AppSettings _settings;
    private readonly AttentionService _attention;
    private readonly GameMode _game;
    private readonly CarsDb _db;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private DateTime? _countdownEnds;
    private bool _checking;
    private readonly CancellationTokenSource _lifetime = new();

    public HealthWatcher(AppSettings settings, AttentionService attention, GameMode game, CarsDb db)
    {
        _settings = settings;
        _attention = attention;
        _game = game;
        _db = db;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>True from the moment a restart countdown starts until it's cancelled or the PC goes down.</summary>
    public bool CountdownRunning => _countdownEnds is { } e && e > DateTime.Now;

    public void Start()
    {
        // Test copies share your settings, so they never restart your PC (CARS_DEV_RESTART=1 allows it).
        _timer.Start();
        BackgroundTasks.Run(FirstCheckAsync(), "initial health check");
    }

    private async Task FirstCheckAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(2), _lifetime.Token);
        if (!_lifetime.IsCancellationRequested) await TickAsync(force: true);
    }

    public void Stop() { _timer.Stop(); _lifetime.Cancel(); }

    private async Task TickAsync(bool force = false)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (CountdownRunning) WatchCountdown();
            else if (_countdownEnds is not null) _countdownEnds = null;

            if (!_game.IsOn && (force || DateTime.Now - _lastHealthCheck > TimeSpan.FromMinutes(30)))
            {
                _lastHealthCheck = DateTime.Now;
                await CheckHealthAsync();
            }
            TryRestart();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("health watcher", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task CheckHealthAsync()
    {
        var reboot = await Task.Run(PendingReboot.Read);
        _attention.Set("health-restart", !reboot.Pending ? null : new AttentionItem
        {
            Key = "health-restart",
            Title = "A restart is waiting",
            Detail = reboot.Summary + (_settings.Store.Current.Restart.Enabled ? $" It will restart on its own after {_settings.Store.Current.Restart.Time} when you're away." : ""),
            Level = AttentionLevel.Info,
            Icon = SymbolRegular.ArrowClockwise24,
            ActionText = "Open Health",
            Action = () => { Navigator.Go("health"); return Task.CompletedTask; },
            SnoozeFor = TimeSpan.FromDays(1),
        });

        var drives = await Task.Run(() => DriveHealthReader.Read(includeCounters: false));
        var bad = drives.Where(d => DriveHealthRules.Evaluate(d).Level != DriveLevel.Good).ToList();
        _attention.Set("health-drive", bad.Count == 0 ? null : new AttentionItem
        {
            Key = "health-drive:" + string.Join(",", bad.Select(d => d.Name)),
            Title = bad.Count == 1 ? $"{bad[0].Name} reports a health problem" : $"{bad.Count} drives report a health problem",
            Detail = "Windows says it isn't healthy. Back up what matters on it.",
            Level = AttentionLevel.Critical,
            Icon = SymbolRegular.HardDrive24,
            ActionText = "Open Health",
            Action = () => { Navigator.Go("health", "drives"); return Task.CompletedTask; },
        });

        var timeline = await CrashLog.ReadAsync(TimeSpan.FromDays(7));
        var recent = timeline.BlueScreens.FirstOrDefault();
        _attention.Set("health-crash", recent is null ? null : new AttentionItem
        {
            Key = "health-crash:" + recent.At.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
            Title = $"Blue screen {Converters.Format.Ago(recent.At)}: {recent.Name}",
            Detail = "Open Health › Crashes to see what Windows blamed.",
            Level = AttentionLevel.Warning,
            Icon = SymbolRegular.ErrorCircle24,
            ActionText = "Open Health",
            Action = () => { Navigator.Go("health", "crashes"); return Task.CompletedTask; },
        });
    }

    // ---------- restart when away ----------

    private void TryRestart()
    {
        var s = _settings.Store.Current.Restart;
        if (!s.Enabled || CountdownRunning) return;
        if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_RESTART") != "1") return;

        DateTime? last = DateTime.TryParse(_db.GetSetting(LastAttemptKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var l) ? l : null;
        var check = RestartPlanner.Decide(s, DateTime.Now, PendingReboot.Read().Pending, UserIdle.Current, _game.IsOn, UserIdle.FullScreenBusy, last);
        if (check.Decision != RestartDecision.Start) return;

        _db.SetSetting(LastAttemptKey, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        if (!RunShutdown(RestartPlanner.StartArguments("C.A.R.S. Control: restarting to finish Windows updates. Open the C.A.R.S. notification to cancel."))) return;
        _countdownEnds = DateTime.Now + RestartPlanner.Countdown;
        Notifier.RestartCountdown((int)RestartPlanner.Countdown.TotalMinutes);
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "health", "restart-away", "Restarted while you were away", "Health", null, null,
            false, "Succeeded", 0, "A restart was waiting and you'd been away; started a 5-minute countdown.", null));
    }

    /// <summary>You came back during the countdown: stop it.</summary>
    private void WatchCountdown()
    {
        if (UserIdle.Current < TimeSpan.FromSeconds(45)) CancelRestart(because: "you're back");
    }

    public void CancelRestart(string because = "you cancelled it")
    {
        if (!CountdownRunning) return;
        RunShutdown(RestartPlanner.CancelArguments);
        _countdownEnds = null;
        Notifier.Info("The restart was cancelled", $"C.A.R.S. Control didn't restart the PC because {because}. It tries again tomorrow night if a restart is still waiting.");
    }

    private static bool RunShutdown(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorReporter.Log("shutdown", ex);
            return false;
        }
    }
}
