using System.Collections.ObjectModel;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    public ObservableCollection<SleepRow> SleepRows { get; } = [];
    public ObservableCollection<StallRow> StallRows { get; } = [];
    public ObservableCollection<string> BlockRows { get; } = [];
    public ObservableCollection<BootRow> BootRows { get; } = [];
    public ObservableCollection<string> ShutdownRows { get; } = [];
    public ObservableCollection<SlowItemRow> SlowItemRows { get; } = [];
    public ObservableCollection<InfoRow> PowerRequestRows { get; } = [];

    [ObservableProperty] private string _sleepHeadline = "";
    [ObservableProperty] private string _sleepSummary = "";
    [ObservableProperty] private string _bootStatus = "";
    [ObservableProperty] private string _powerStatus = "";
    [ObservableProperty] private bool _sleepBusy;

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalSeconds:0} s";

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    private async Task LoadSleepAsync()
    {
        try
        {
            _sleep = await SleepHealth.ReadAsync(TimeSpan.FromDays(60));
            SleepRows.Clear();
            foreach (var c in _sleep.Cycles.Take(25))
                SleepRows.Add(new SleepRow($"{c.SleptAt:ddd d MMM, h:mm tt}", c.State, c.Why, Span(c.Asleep), Seconds(c.ResumeTime),
                    c.IsSlow ? $"firmware {Seconds(c.FirmwareInit)}, drivers {Seconds(c.DriverInit)}, hibernate file {Seconds(c.HibernateRead)}" : null, c.IsSlow));
            StallRows.Clear();
            foreach (var s in _sleep.Stalls.Take(25)) StallRows.Add(new StallRow($"{s.At:ddd d MMM, h:mm:ss tt}", Seconds(s.Length)));
            BlockRows.Clear();
            foreach (var b in _sleep.Blocks.GroupBy(x => x.Driver).OrderByDescending(g => g.Count()))
                BlockRows.Add($"{b.Key} stopped {b.Count()} sleep{(b.Count() == 1 ? "" : "s")}, the last {Converters.Format.Ago(b.Max(x => x.At))}");
            SleepHeadline = _sleep.Headline ?? (_sleep.Cycles.Count == 0 ? "No sleeps were recorded in the last two months." : "Waking from sleep is quick.");
            SleepSummary = $"{_sleep.Cycles.Count} sleep{(_sleep.Cycles.Count == 1 ? "" : "s")} and hibernate{(_sleep.Cycles.Count == 1 ? "" : "s")}, {_sleep.SlowResumes} slow wake-up{(_sleep.SlowResumes == 1 ? "" : "s")}, {_sleep.Stalls.Count} freeze{(_sleep.Stalls.Count == 1 ? "" : "s")} (the system clock stopped for 3 s or more).";
            BuildOverview();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("sleep", ex);
            SetBanner("Couldn't read the sleep history: " + ex.Message, NoticeKind.Warning);
        }
    }

    [RelayCommand]
    private async Task ReadBootTimesAsync()
    {
        SleepBusy = true;
        try
        {
            BootStatus = "Reading the start-up log…";
            var r = await AdminAsync(new BootPerformanceOp(45));
            if (r is null) { BootStatus = ""; return; }
            if (!r.Ok || r.Data is null)
            {
                BootStatus = r.Message ?? (HelperClient.DevUnelevated ? "Test copy: admin reads are dry runs." : "No data came back.");
                return;
            }
            _boot = HelperJson.Deserialize<BootReport>(r.Data);
            BootRows.Clear();
            ShutdownRows.Clear();
            SlowItemRows.Clear();
            if (_boot is null) { BootStatus = "Windows has no start-up records."; return; }
            foreach (var b in _boot.Boots.Take(12))
                BootRows.Add(new BootRow($"{b.At:ddd d MMM, h:mm tt}", Seconds(b.Boot), $"Windows {Seconds(b.MainPath)} · after sign-in {Seconds(b.PostBoot)} · drivers {Seconds(b.Drivers)} · desktop {Seconds(b.Explorer)}", b.IsSlow));
            foreach (var s in _boot.Shutdowns.Take(8)) ShutdownRows.Add($"{s.At:ddd d MMM, h:mm tt}: {Seconds(s.Time)}");
            foreach (var i in _boot.SlowItems) SlowItemRows.Add(new SlowItemRow(i.Kind, i.Name, Seconds(i.Cost)));
            BootStatus = _boot.Boots.Count == 0 ? "No start-ups were recorded in the last 45 days." : $"{_boot.Boots.Count} start-ups read {DateTime.Now:h:mm tt}.";
            BuildOverview();
        }
        finally
        {
            SleepBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReadPowerRequestsAsync()
    {
        SleepBusy = true;
        try
        {
            PowerStatus = "Asking Windows what's keeping the PC awake…";
            var r = await AdminAsync(new PowerRequestsOp());
            if (r is null) { PowerStatus = ""; return; }
            if (!r.Ok || r.Data is null) { PowerStatus = r.Message ?? (HelperClient.DevUnelevated ? "Test copy: admin reads are dry runs." : "No data came back."); return; }
            PowerRequestRows.Clear();
            foreach (var p in PowerRequests.Parse(r.Data.Split('\n'))) PowerRequestRows.Add(new InfoRow(p.Who, p.Category));
            PowerStatus = PowerRequestRows.Count == 0 ? "Nothing is stopping the PC from sleeping right now." : $"{PowerRequestRows.Count} thing{(PowerRequestRows.Count == 1 ? " is" : "s are")} preventing sleep right now:";
        }
        finally
        {
            SleepBusy = false;
        }
    }
}
