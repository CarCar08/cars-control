using System.Collections.ObjectModel;
using System.IO;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>
/// The Health page: what this PC is, how its parts are doing, and why it crashed, froze or woke slowly.
/// Everything that doesn't need admin is read as you; the few readings Windows keeps for administrators
/// (drive counters, boot times, power requests, CPU/board sensors) go through the admin helper, only when you
/// press the button for them. Polling (sensors, activity) runs only while that tab is open.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    private readonly HelperClient _helper;
    private readonly AppSettings _settings;
    private readonly CarsDb _db;
    private readonly AlertService _alerts;
    private readonly GameMode _game;
    private DateTime _overviewAt = DateTime.MinValue;
    private bool _pageVisible;

    // The latest of everything, for findings and the report.
    private SystemSpecs? _specs;
    private IReadOnlyList<DriveReading> _drives = [];
    private IReadOnlyList<GpuReading> _gpus = [];
    private IReadOnlyList<SensorReading> _board = [];
    private CrashTimeline _timeline = CrashTimeline.Empty;
    private SleepReport? _sleep;
    private BootReport? _boot;
    private TailscaleState? _tailscale;
    private VpnStatus? _vpn;
    private PendingRebootState? _reboot;
    private WindowsUpdateState? _wu;
    private readonly Dictionary<string, DumpAnalysis> _analyses = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<InfoRow> Specs { get; } = [];

    [ObservableProperty] private HealthTab _selectedTab;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private NoticeKind _bannerKind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _headline = "Checking…";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private string _batteryVerdict = "";

    public HealthViewModel(HelperClient helper, AppSettings settings, CarsDb db, AlertService alerts, GameMode game)
    {
        _helper = helper;
        _settings = settings;
        _db = db;
        _alerts = alerts;
        _game = game;
        _helper.ConnectionChanged += connected => Platform.OnUiThread(() =>
        {
            OnPropertyChanged(nameof(HelperConnected));
            if (connected) OnPropertyChanged(nameof(CanReadBoard));
        });
        LoadAnalyses();
    }

    public bool HelperConnected => _helper.IsConnected;

    private void SetBanner(string? text, NoticeKind kind = NoticeKind.Info)
    {
        Banner = text;
        BannerKind = kind;
    }

    // ---------- page lifecycle ----------

    /// <summary>The page became visible: refresh what's stale and start the tab's polling.</summary>
    public void PageShown()
    {
        if (_pageVisible) return;
        _pageVisible = true;
        if (DateTime.Now - _overviewAt > TimeSpan.FromMinutes(2)) BackgroundTasks.Run(RefreshAsync(), "health overview");
        EnterTab(SelectedTab);
    }

    public void PageHidden()
    {
        _pageVisible = false;
        StopPolling();
    }

    partial void OnSelectedTabChanged(HealthTab value)
    {
        if (_pageVisible) EnterTab(value);
    }

    private void EnterTab(HealthTab tab)
    {
        StopPolling();
        switch (tab)
        {
            case HealthTab.Sensors: StartSensors(); break;
            case HealthTab.Drives: _ = LoadDrivesAsync(); break;
            case HealthTab.Crashes: _ = LoadCrashesAsync(); break;
            case HealthTab.SleepBoot: _ = LoadSleepAsync(); break;
            case HealthTab.Drivers: _ = LoadDriversAsync(); break;
            case HealthTab.Activity: StartActivity(); break;
            case HealthTab.Network: _ = LoadNetworkAsync(); break;
        }
    }

    private void StopPolling()
    {
        StopSensors();
        StopActivity();
    }

    // ---------- overview ----------

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var specsTask = Task.Run(SystemSpecsReader.Read);
            var drivesTask = Task.Run(() => DriveHealthReader.Read(includeCounters: false));
            var gpuTask = Task.Run(NvidiaSensors.Read);
            var timelineTask = CrashLog.ReadAsync(TimeSpan.FromDays(120));
            var sleepTask = SleepHealth.ReadAsync(TimeSpan.FromDays(45));
            var tailTask = TailscaleStatus.ReadAsync();
            var adaptersTask = Task.Run(NetworkStatus.Adapters);
            var battery = await Task.Run(BatteryReader.Read);
            HasBattery = battery is not null;
            BatteryText = battery?.Summary ?? "";
            BatteryVerdict = BatteryRules.Verdict(battery?.WearPercent);
            _reboot = await Task.Run(PendingReboot.Read);
            _wu = await Task.Run(WindowsUpdatePolicy.Read);

            _specs = await specsTask;
            // Keep the counters (temperature, wear) already read with admin; the plain read doesn't have them.
            var fresh = await drivesTask;
            _drives = fresh.Select(f => _drives.FirstOrDefault(d => d.Name == f.Name && d.LettersText == f.LettersText && d.HasCounters) is { } old ? f.WithCountersOf(old) : f).ToList();
            _gpus = await gpuTask;
            _timeline = await timelineTask;
            _sleep = await sleepTask;
            _tailscale = await tailTask;
            var adapters = await adaptersTask;
            _vpn = Extras.ProtonVpn(adapters) ? NetworkStatus.Vpn(adapters) : null; // no VPN finding on PCs without Proton VPN

            BuildOverview();
            _overviewAt = DateTime.Now;
            UpdatedText = $"Checked {DateTime.Now:h:mm tt}";
            RefreshRestartState();
            RefreshSafetyNet();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("health refresh", ex);
            SetBanner("Couldn't read everything: " + ex.Message, NoticeKind.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private HealthSnapshot Snapshot() => new(DateTime.Now, _specs, _drives, _gpus, _board, _timeline.BlueScreens, _analyses.Values.ToList(), _sleep, _boot, _vpn, _tailscale, _reboot, _wu, AppVersion);

    private static string? AppVersion => typeof(HealthViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : null;

    private void BuildOverview()
    {
        var health = _settings.Store.Current.Health;
        var findings = HealthSummary.Build(Snapshot(), health.CpuAlertC, health.GpuAlertC);
        Findings.Clear();
        foreach (var f in findings) Findings.Add(new FindingRow(f.Level.ToString(), f.Title, f.Detail, f.Area, TabFor(f.Area)));
        var serious = findings.Count(f => f.Level != FindingLevel.Info);
        Headline = serious == 0
            ? (findings.Count == 0 ? "Everything looks fine." : "Nothing is wrong, a couple of things to know.")
            : $"{serious} thing{(serious == 1 ? "" : "s")} need{(serious == 1 ? "s" : "")} attention.";

        Specs.Clear();
        if (_specs is { } s)
        {
            Specs.Add(new InfoRow("Processor", $"{s.Cpu} ({s.Cores} cores, {s.Threads} threads)"));
            foreach (var g in s.Gpus) Specs.Add(new InfoRow("Graphics", $"{g.Name}" + (g.VramBytes > 0 ? $", {g.VramBytes / (1024.0 * 1024 * 1024):0.#} GB" : "") + $", driver {g.DriverVersion}"));
            Specs.Add(new InfoRow("Memory", s.RamSummary));
            Specs.Add(new InfoRow("Motherboard", $"{s.BoardMaker} {s.Board}"));
            Specs.Add(new InfoRow("BIOS", s.BiosVersion + (s.BiosDate is { } d ? $" ({d:d MMM yyyy})" : "")));
            Specs.Add(new InfoRow("Windows", $"{s.WindowsEdition} {s.WindowsVersion} (build {s.WindowsBuild})"));
            Specs.Add(new InfoRow("Running for", s.Uptime.TotalDays >= 1 ? $"{(int)s.Uptime.TotalDays} days {s.Uptime.Hours} h" : $"{s.Uptime.Hours} h {s.Uptime.Minutes} min"));
        }
        OnPropertyChanged(nameof(BoardSupportUrl));
        OnPropertyChanged(nameof(CanOpenBoardSupport));
    }

    private static HealthTab TabFor(string area) => area switch
    {
        "Sensors" => HealthTab.Sensors,
        "Drives" => HealthTab.Drives,
        "Crashes" => HealthTab.Crashes,
        "Sleep & boot" => HealthTab.SleepBoot,
        "Network" => HealthTab.Network,
        _ => HealthTab.Overview,
    };

    [RelayCommand]
    private void ShowFinding(FindingRow? row)
    {
        if (row is not null) SelectedTab = row.Tab;
    }

    /// <summary>A web search for this board's support and BIOS downloads (opened only when you click it).</summary>
    public string? BoardSupportUrl => _specs is { Board.Length: > 0 } s
        ? "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"{s.BoardMaker} {s.Board} BIOS drivers support")
        : null;

    public bool CanOpenBoardSupport => BoardSupportUrl is not null;

    [RelayCommand]
    private void OpenBoardSupport()
    {
        if (BoardSupportUrl is { } url) Platform.OpenUri(url);
    }

    // ---------- the report ----------

    [ObservableProperty] private bool _redactReport = true;

    [RelayCommand]
    private async Task SaveReportAsync()
    {
        if (_specs is null) await RefreshAsync();
        var name = $"PC health report {DateTime.Now:yyyy-MM-dd}.html";
        var path = Platform.PickSaveFile("Save the health report", name, "Web page (*.html)|*.html");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, HealthReport.Render(Snapshot(), RedactReport));
            SetBanner(RedactReport
                ? $"Saved {Path.GetFileName(path)} with your user name, this PC's name and your folders replaced. It's one file; nothing was sent anywhere."
                : $"Saved {Path.GetFileName(path)}. It includes this PC's name and your user name.", NoticeKind.Success);
            Platform.ShowInFileManager(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetBanner("Couldn't save the report: " + ex.Message, NoticeKind.Error);
        }
    }

    // ---------- restart when away ----------

    [ObservableProperty] private string _restartText = "";
    [ObservableProperty] private bool _restartPending;
    [ObservableProperty] private bool _restartAway;
    [ObservableProperty] private string _restartTime = "04:00";
    [ObservableProperty] private int _restartIdleMinutes = 30;
    private bool _loadingRestart;

    public IReadOnlyList<string> RestartTimes { get; } = ["01:00", "02:00", "03:00", "04:00", "05:00", "06:00"];
    public IReadOnlyList<int> RestartIdleChoices { get; } = [10, 20, 30, 60, 120];

    private void RefreshRestartState()
    {
        _loadingRestart = true;
        try
        {
            var r = _settings.Store.Current.Restart;
            RestartAway = r.Enabled;
            RestartTime = RestartTimes.Contains(r.Time) ? r.Time : "04:00";
            RestartIdleMinutes = RestartIdleChoices.Contains(r.IdleMinutes) ? r.IdleMinutes : 30;
            RestartPending = _reboot?.Pending == true;
            RestartText = _reboot?.Summary ?? "";
        }
        finally
        {
            _loadingRestart = false;
        }
    }

    partial void OnRestartAwayChanged(bool value) => SaveRestart();
    partial void OnRestartTimeChanged(string value) => SaveRestart();
    partial void OnRestartIdleMinutesChanged(int value) => SaveRestart();

    private void SaveRestart()
    {
        if (_loadingRestart) return;
        _settings.Store.Update(s =>
        {
            s.Restart.Enabled = RestartAway;
            s.Restart.Time = RestartTime;
            s.Restart.IdleMinutes = RestartIdleMinutes;
        });
    }

    // ---------- safety net ----------

    [ObservableProperty] private bool _protectionOn;
    [ObservableProperty] private bool _protectionBlocked;
    [ObservableProperty] private bool _restorePointsBefore;
    private bool _loadingSafety;

    private void RefreshSafetyNet()
    {
        _loadingSafety = true;
        try
        {
            var s = SafetyNet.Read();
            ProtectionOn = s.ProtectionOn;
            ProtectionBlocked = s.BlockedByPolicy;
            RestorePointsBefore = _settings.Store.Current.Health.RestorePointBeforeRiskyChanges;
        }
        finally
        {
            _loadingSafety = false;
        }
    }

    partial void OnRestorePointsBeforeChanged(bool value)
    {
        if (!_loadingSafety) _settings.Store.Update(s => s.Health.RestorePointBeforeRiskyChanges = value);
    }

    public string ProtectionText => ProtectionBlocked ? "System Protection is turned off by a policy on this PC, so restore points can't be made."
        : ProtectionOn ? "System Protection is on for C:. A restore point is made before risky changes."
        : "System Protection is off, so there's no restore point to go back to if a driver update goes wrong.";

    partial void OnProtectionOnChanged(bool value) => OnPropertyChanged(nameof(ProtectionText));
    partial void OnProtectionBlockedChanged(bool value) => OnPropertyChanged(nameof(ProtectionText));

    [RelayCommand]
    private async Task EnableProtectionAsync()
    {
        if (!await Dialogs.ConfirmAsync("Turn on System Protection for C:? Windows will keep restore points (a few GB of disk space) so a bad driver or update can be rolled back. It needs admin approval once.", "Turn on")) return;
        var r = await AdminAsync(new EnableSystemRestoreOp());
        if (r is null) return;
        RefreshSafetyNet();
        SetBanner(r.Ok ? "System Protection is on for C:." : r.Message ?? "Windows didn't turn it on.", r.Ok ? NoticeKind.Success : NoticeKind.Error);
        if (r.Ok) Record("safety-net", "System Protection on", "Turned on System Protection for C:", r);
    }

    // ---------- helpers ----------

    /// <summary>One admin call. <paramref name="onlyIfConnected"/> never prompts (background refreshes);
    /// otherwise approval is asked for once. Null when cancelled or unavailable (the banner says why).</summary>
    private async Task<OpResult?> AdminAsync(HelperOp op, bool onlyIfConnected = false, Action<string>? onLine = null)
    {
        try
        {
            var dry = HelperClient.DevUnelevated;
            return onlyIfConnected ? await _helper.SendIfConnectedAsync(op, onLine, dry) : await _helper.SendAsync(op, onLine, dry);
        }
        catch (HelperCancelledException)
        {
            SetBanner("Admin approval was cancelled, so nothing was read or changed.", NoticeKind.Warning);
            return null;
        }
        catch (HelperSessionClosedException)
        {
            return null;
        }
        catch (HelperUnavailableException ex)
        {
            SetBanner(ex.Message, NoticeKind.Error);
            return null;
        }
    }

    private void Record(string key, string title, string message, OpResult r) =>
        _db.AddHistory(new HistoryEntry(0, DateTime.Now, DateTime.Now, "health", key, title, "Health", null, null, true,
            r.Status.ToString(), r.ExitCode, message, r.Message));
}
