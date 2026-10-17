using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › Health: the temperature limits that raise an alert, board sensors and the safety net.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _healthTempAlerts;
    [ObservableProperty] private double _healthCpuLimit = 90;
    [ObservableProperty] private double _healthGpuLimit = 85;
    [ObservableProperty] private bool _healthBoardSensors;
    [ObservableProperty] private bool _healthRestorePoints;

    private void ReloadHealth()
    {
        var h = _settings.Store.Current.Health;
        HealthTempAlerts = h.TemperatureAlerts;
        HealthCpuLimit = h.CpuAlertC;
        HealthGpuLimit = h.GpuAlertC;
        HealthBoardSensors = h.ReadBoardSensors;
        HealthRestorePoints = h.RestorePointBeforeRiskyChanges;
    }

    private void SaveHealth(Action<HealthSettings> change)
    {
        if (!_loading) _settings.Store.Update(s => change(s.Health));
    }

    partial void OnHealthTempAlertsChanged(bool value) => SaveHealth(h => h.TemperatureAlerts = value);
    partial void OnHealthCpuLimitChanged(double value) => SaveHealth(h => h.CpuAlertC = Math.Clamp((int)Math.Round(value), 60, 110));
    partial void OnHealthGpuLimitChanged(double value) => SaveHealth(h => h.GpuAlertC = Math.Clamp((int)Math.Round(value), 60, 110));
    partial void OnHealthBoardSensorsChanged(bool value) => SaveHealth(h => h.ReadBoardSensors = value);
    partial void OnHealthRestorePointsChanged(bool value) => SaveHealth(h => h.RestorePointBeforeRiskyChanges = value);
}
