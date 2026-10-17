using System.Collections.ObjectModel;
using System.Globalization;
using CarsControl.App.Services;
using CarsControl.Core.Cleanup;
using CarsControl.Core.Maintenance;
using CarsControl.Core.Settings;
using CarsControl.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › Maintenance: when it runs and what it does.</summary>
public sealed partial class SettingsViewModel
{
    private MaintenanceService _maintenance = null!;

    /// <summary>Monday…Sunday in your calendar's order.</summary>
    public ObservableCollection<FilterChip> MaintenanceDays { get; } = [];
    public ObservableCollection<FilterChip> MaintenanceSources { get; } = [];
    public ObservableCollection<FilterChip> MaintenanceCleanup { get; } = [];

    /// <summary>Every half hour; the time in settings.json can be anything ("03:15").</summary>
    public ObservableCollection<string> MaintenanceTimes { get; } = [];

    [ObservableProperty] private bool _maintenanceEnabled;
    [ObservableProperty] private string? _maintenanceTime;
    [ObservableProperty] private bool _maintenanceUpdates;
    [ObservableProperty] private bool _maintenanceCleanupOn;
    [ObservableProperty] private double _maintenanceIdleMinutes;
    [ObservableProperty] private string _maintenanceStatus = "";

    private void InitMaintenance(MaintenanceService maintenance)
    {
        _maintenance = maintenance;
        _maintenance.Changed += () => MaintenanceStatus = $"{_maintenance.NextText} · {_maintenance.LastText}";

        for (var h = 0; h < 24; h++)
        {
            MaintenanceTimes.Add($"{h:00}:00");
            MaintenanceTimes.Add($"{h:00}:30");
        }

        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)first + i) % 7);
            var chip = new FilterChip(day.ToString(), CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day));
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FilterChip.IsSelected)) SaveMaintenance(m => m.Days = SelectedDays()); };
            MaintenanceDays.Add(chip);
        }

        foreach (var source in UpdateCenter.Order)
        {
            var chip = new FilterChip(source.ToString(), UpdateCenter.SourceTitle(source))
            {
                ToolTip = source is UpdateSource.WindowsUpdate or UpdateSource.Store ? "Needs admin: runs if an admin session is open, otherwise waits for your approval" : null,
            };
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FilterChip.IsSelected)) SaveMaintenance(m => m.UpdateSources = SelectedSources()); };
            MaintenanceSources.Add(chip);
        }

        foreach (var rule in CleanupRules.All)
        {
            var chip = new FilterChip(rule.Id, rule.Title)
            {
                Detail = rule.Elevated ? "admin" : null,
                ToolTip = rule.Elevated ? "Needs admin: runs if an admin session is open, otherwise waits for your approval" : rule.Description,
            };
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FilterChip.IsSelected)) SaveMaintenance(m => m.CleanupRules = SelectedRules()); };
            MaintenanceCleanup.Add(chip);
        }
    }

    private void ReloadMaintenance()
    {
        var m = _settings.Store.Current.Maintenance;
        MaintenanceEnabled = m.Enabled;
        MaintenanceTime = MaintenanceSchedule.TryParseTime(m.Time, out var t) ? $"{t.Hours:00}:{t.Minutes:00}" : "03:00";
        if (!MaintenanceTimes.Contains(MaintenanceTime)) MaintenanceTimes.Add(MaintenanceTime); // a hand-edited "03:15"
        MaintenanceUpdates = m.RunUpdates;
        MaintenanceCleanupOn = m.RunCleanup;
        MaintenanceIdleMinutes = m.IdleMinutes;
        foreach (var d in MaintenanceDays) d.IsSelected = m.Days.Contains(Enum.Parse<DayOfWeek>(d.Key));
        foreach (var s in MaintenanceSources) s.IsSelected = m.UpdateSources is null || m.UpdateSources.Contains(s.Key);
        var rules = m.CleanupRules;
        foreach (var r in MaintenanceCleanup) r.IsSelected = rules?.Contains(r.Key) ?? CleanupRules.Find(r.Key)?.DefaultOn == true;
        MaintenanceStatus = $"{_maintenance.NextText} · {_maintenance.LastText}";
    }

    private List<DayOfWeek> SelectedDays() => MaintenanceDays.Where(d => d.IsSelected).Select(d => Enum.Parse<DayOfWeek>(d.Key)).ToList();

    /// <summary>Null (= everything, including sources added later) when all are ticked.</summary>
    private List<string>? SelectedSources() =>
        MaintenanceSources.All(s => s.IsSelected) ? null : MaintenanceSources.Where(s => s.IsSelected).Select(s => s.Key).ToList();

    private List<string> SelectedRules() => MaintenanceCleanup.Where(r => r.IsSelected).Select(r => r.Key).ToList();

    partial void OnMaintenanceEnabledChanged(bool value) => SaveMaintenance(m => m.Enabled = value);
    partial void OnMaintenanceUpdatesChanged(bool value) => SaveMaintenance(m => m.RunUpdates = value);
    partial void OnMaintenanceCleanupOnChanged(bool value) => SaveMaintenance(m => m.RunCleanup = value);
    partial void OnMaintenanceIdleMinutesChanged(double value) => SaveMaintenance(m => m.IdleMinutes = Math.Clamp((int)Math.Round(value), 0, 120));

    partial void OnMaintenanceTimeChanged(string? value)
    {
        if (value is not null && MaintenanceSchedule.TryParseTime(value, out _)) SaveMaintenance(m => m.Time = value);
    }

    private void SaveMaintenance(Action<MaintenanceSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Maintenance));
        MaintenanceStatus = $"{_maintenance.NextText} · {_maintenance.LastText}";
    }

    [RelayCommand]
    private async Task RunMaintenanceNowAsync()
    {
        Message = "Maintenance is running — Refresh and Cleanup show the progress.";
        var state = await _maintenance.RunAsync("Maintenance (run now)", unattended: false);
        Message = state is null ? "Something else is running; try again when it's finished." : $"Maintenance finished: {MaintenanceSchedule.Describe(state)}.";
    }
}
