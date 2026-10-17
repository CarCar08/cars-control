using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    private DispatcherTimer? _gpuTimer;
    private DispatcherTimer? _boardTimer;
    private int _hotReads;
    private int _sensorEpoch;
    private bool _readingGpu;

    public ObservableCollection<GpuRow> Gpus { get; } = [];
    public ObservableCollection<SensorGroup> BoardGroups { get; } = [];

    [ObservableProperty] private bool _pawnIoInstalled;
    [ObservableProperty] private string _boardStatus = "";
    [ObservableProperty] private bool _boardBusy;
    [ObservableProperty] private bool _showAllSensors;
    [ObservableProperty] private bool _noGpu;

    public bool CanReadBoard => PawnIoInstalled && !BoardBusy;
    public bool NeedsPawnIo => !PawnIoInstalled;
    public bool HasBoardData => BoardGroups.Count > 0;

    /// <summary>The spinner shows while the first reading is on its way, not on every refresh.</summary>
    public bool BoardFirstRead => BoardBusy && !HasBoardData;

    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private string _cpuPowerText = "—";
    [ObservableProperty] private string _cpuLoadText = "—";
    [ObservableProperty] private string _boardTempText = "—";
    [ObservableProperty] private string _fansText = "—";
    [ObservableProperty] private string _cpuTempTip = "";

    public string PawnIoText => $"CPU, motherboard and fan readings need PawnIO, a small signed driver (version {PawnIo.Version}, 3.3 MB) that lets C.A.R.S. read the processor's own temperature registers and the board's sensor chip. " +
        "It comes from the PawnIO project's official release (github.com/namazso/PawnIO.Setup); C.A.R.S. checks the file against a SHA-256 it was built with before running it. Installing a driver needs admin approval once.";

    partial void OnPawnIoInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReadBoard));
        OnPropertyChanged(nameof(NeedsPawnIo));
    }

    partial void OnBoardBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReadBoard));
        OnPropertyChanged(nameof(BoardFirstRead));
    }

    partial void OnShowAllSensorsChanged(bool value) => ShowBoard(_boardAll);

    private IReadOnlyList<SensorReading> _boardAll = [];

    private void StartSensors()
    {
        PawnIoInstalled = PawnIo.IsInstalled();
        _hotReads = 0;
        BackgroundTasks.Run(ReadGpuAsync(), "GPU sensors");
        _gpuTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _gpuTimer.Tick += (_, _) => BackgroundTasks.Run(ReadGpuAsync(), "GPU sensors");
        _gpuTimer.Start();

        if (PawnIoInstalled && _settings.Store.Current.Health.ReadBoardSensors)
        {
            // Only over a session that's already open: no prompt appears just because this tab was opened.
            _ = ReadBoardAsync(prompt: false);
            _boardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _boardTimer.Tick += async (_, _) => await ReadBoardAsync(prompt: false);
            _boardTimer.Start();
        }
    }

    private void StopSensors()
    {
        _sensorEpoch++;
        _gpuTimer?.Stop();
        _gpuTimer = null;
        _boardTimer?.Stop();
        _boardTimer = null;
    }

    private async Task ReadGpuAsync()
    {
        if (_readingGpu || !_pageVisible || SelectedTab != HealthTab.Sensors) return;
        _readingGpu = true;
        var epoch = _sensorEpoch;
        try
        {
        var readings = await Task.Run(NvidiaSensors.Read);
        if (epoch != _sensorEpoch || !_pageVisible || SelectedTab != HealthTab.Sensors) return;
        _gpus = readings;
        NoGpu = readings.Count == 0;
        Gpus.Clear();
        foreach (var g in readings)
        {
            var used = g.VramUsedBytes / (1024.0 * 1024 * 1024);
            var total = g.VramTotalBytes / (1024.0 * 1024 * 1024);
            Gpus.Add(new GpuRow(g.Name, g.DriverVersion, g.TempC, g.GpuLoadPct,
                $"core {g.CoreMHz} MHz · memory {g.MemoryMHz} MHz" + (g.FanPct is { } f ? $" · fan {f}%" : ""),
                g.PowerWatts is { } w ? $"{w:0} W" + (g.PowerLimitWatts is { } l ? $" of {l:0} W" : "") : "—",
                total > 0 ? $"{used:0.0} of {total:0.0} GB" : "—",
                total > 0 ? (int)Math.Round(used / total * 100) : null));
        }
        CheckHot(readings);
        }
        finally { _readingGpu = false; }
    }

    /// <summary>Three hot readings in a row (about six seconds) before a phone alert: a one-off spike isn't news.</summary>
    private void CheckHot(IReadOnlyList<GpuReading> readings)
    {
        var limit = _settings.Store.Current.Health.GpuAlertC;
        var hot = readings.FirstOrDefault(g => g.TempC is { } t && t >= limit);
        if (hot is null) { _hotReads = 0; return; }
        if (++_hotReads == 3 && _settings.Store.Current.Health.TemperatureAlerts)
            _ = _alerts.ReportTemperatureAsync("graphics card", hot.TempC!.Value, limit);
    }

    [RelayCommand]
    private Task ReadBoardNowAsync() => ReadBoardAsync(prompt: true);

    private async Task ReadBoardAsync(bool prompt)
    {
        if (BoardBusy) return;
        var epoch = _sensorEpoch;
        BoardBusy = true;
        try
        {
            var r = await AdminAsync(new SensorsReadOp(), onlyIfConnected: !prompt);
            if (!prompt && (epoch != _sensorEpoch || !_pageVisible || SelectedTab != HealthTab.Sensors)) return;
            if (r is null)
            {
                if (!prompt && BoardGroups.Count == 0) BoardStatus = "Press “Read CPU & board sensors” to approve admin access once; the readings then keep updating while this tab is open.";
                return;
            }
            if (!r.Ok)
            {
                BoardStatus = r.Data == "pawnio-missing" ? "The PawnIO driver isn't installed." : r.Message ?? "The sensors couldn't be read.";
                PawnIoInstalled = PawnIo.IsInstalled();
                return;
            }
            if (r.Data is null)
            {
                BoardStatus = HelperClient.DevUnelevated ? "Test copy: admin reads are dry runs." : "No readings came back.";
                return;
            }
            var sensors = HelperJson.Deserialize<List<SensorReading>>(r.Data) ?? [];
            _board = sensors;
            _boardAll = sensors;
            ShowBoard(sensors);
            BoardStatus = $"{sensors.Count} sensors, updated {DateTime.Now:h:mm:ss tt}";
            if (_boardTimer is null && _pageVisible && SelectedTab == HealthTab.Sensors && _settings.Store.Current.Health.ReadBoardSensors)
            {
                _boardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
                _boardTimer.Tick += async (_, _) => await ReadBoardAsync(prompt: false);
                _boardTimer.Start();
            }
            CheckCpuHot(sensors);
        }
        finally
        {
            BoardBusy = false;
        }
    }

    private int _cpuHotReads;

    private void CheckCpuHot(IReadOnlyList<SensorReading> sensors)
    {
        var limit = _settings.Store.Current.Health.CpuAlertC;
        var hot = SensorRules.Summarize(sensors).CpuTempC ?? 0;
        if (hot < limit) { _cpuHotReads = 0; return; }
        if (++_cpuHotReads == 3 && _settings.Store.Current.Health.TemperatureAlerts)
            _ = _alerts.ReportTemperatureAsync("processor", (int)hot, limit);
    }

    private static readonly string[] TypeOrder = ["Temperature", "Fan", "Power", "Load", "Clock", "Voltage"];
    private static readonly string[] EssentialTypes = ["Temperature", "Fan", "Power"];

    private void ShowBoard(IReadOnlyList<SensorReading> sensors)
    {
        var sum = SensorRules.Summarize(sensors);
        var limit = _settings.Store.Current.Health.CpuAlertC;
        CpuTempText = sum.CpuTempC is { } t ? $"{t:0} °C" : "—";
        CpuTempTip = sum.CpuTempC is { } t2 ? (t2 < 70 ? "Comfortable." : t2 < 85 ? "Warm, normal under a heavy load." : "Hot. Fine for short bursts; check the cooler if it stays here.") + $" Your alert limit is {limit} °C (Settings › Health)." : "";
        CpuPowerText = sum.CpuPowerW is { } p ? $"{p:0} W" : "—";
        CpuLoadText = sum.CpuLoadPct is { } l ? $"{l:0}%" : "—";
        BoardTempText = sum.BoardTempC is { } b ? $"{b:0} °C" : "—";
        FansText = sum.Fans.Count == 0 ? "none reporting" : string.Join(" · ", sum.Fans.Select(f => $"{f.Rpm:0} RPM"));

        BoardGroups.Clear();
        // Everything with Show every sensor; otherwise the useful types, without the impossible readings of unconnected probes and empty fan headers.
        var wanted = sensors.Where(s => s.Value is not null && (ShowAllSensors
            || ((EssentialTypes.Contains(s.Type) || (s.Type == "Load" && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))) && SensorRules.IsPlausible(s))));
        foreach (var g in wanted.GroupBy(s => (s.Hardware, s.HardwareKind)))
        {
            var rows = g.OrderBy(s => Array.IndexOf(TypeOrder, s.Type) is var i && i < 0 ? 99 : i).ThenBy(s => SensorRules.DisplayName(s), CarsControl.Core.Util.NaturalComparer.Instance)
                .Select(s => new SensorRow(SensorRules.DisplayName(s), FormatSensor(s.Type, s.Value!.Value), s.Min is { } mn && s.Max is { } mx ? $"{FormatNumber(s.Type, mn)} – {FormatNumber(s.Type, mx)}" : "", s.Type)).ToList();
            if (rows.Count > 0) BoardGroups.Add(new SensorGroup(g.Key.Hardware, g.Key.HardwareKind, rows));
        }
        OnPropertyChanged(nameof(HasBoardData));
        OnPropertyChanged(nameof(BoardFirstRead));
    }

    private static string FormatNumber(string type, double v) => type switch
    {
        "Temperature" => $"{v:0} °C",
        "Fan" => $"{v:0} RPM",
        "Voltage" => $"{v:0.000} V",
        "Power" => $"{v:0.0} W",
        "Load" => $"{v:0}%",
        "Clock" => $"{v:0} MHz",
        // The rest of LibreHardwareMonitor's sensor types, so no number shows without its unit.
        "Control" or "Level" => $"{v:0.#}%",
        "Factor" => $"{v:0.##}×",
        "Data" => $"{v:0.##} GB",
        "SmallData" => $"{v:0} MB",
        "Throughput" => v >= 1_000_000 ? $"{v / 1_000_000:0.#} MB/s" : $"{v / 1_000:0} KB/s",
        "Timing" => $"{v:0.##} ns",
        "Current" => $"{v:0.00} A",
        "Frequency" => $"{v:0} Hz",
        "Energy" => $"{v:0} mWh",
        "Flow" => $"{v:0.#} L/h",
        "TimeSpan" => $"{v:0} s",
        _ => v.ToString("0.##", CultureInfo.InvariantCulture),
    };

    private static string FormatSensor(string type, double v) => FormatNumber(type, v);

    [RelayCommand]
    private async Task InstallPawnIoAsync()
    {
        if (!await Dialogs.ConfirmAsync(PawnIoText + "\n\nInstall it now?", "Install PawnIO")) return;
        BoardBusy = true;
        BoardStatus = "Installing PawnIO…";
        try
        {
            var r = await AdminAsync(new InstallPawnIoOp(), onLine: line => Platform.OnUiThread(() => BoardStatus = line));
            PawnIoInstalled = PawnIo.IsInstalled();
            if (r is null) { BoardStatus = ""; return; }
            BoardStatus = r.Message ?? "";
            SetBanner(r.Ok ? (r.RebootRequired ? "PawnIO installed. Restart Windows to finish, then open this tab again." : "PawnIO installed. Press “Read CPU & board sensors”.") : r.Message ?? "PawnIO wasn't installed.", r.Ok ? NoticeKind.Success : NoticeKind.Error);
            Record("pawnio", "PawnIO driver", r.Ok ? $"Installed PawnIO {PawnIo.Version}" : "PawnIO install failed", r);
        }
        finally
        {
            BoardBusy = false;
        }
    }
}
