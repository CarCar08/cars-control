using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CarsControl.App.Services;
using CarsControl.Core.Alerts;
using CarsControl.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace CarsControl.App.ViewModels;

/// <summary>Settings › Phone alerts (ntfy).</summary>
public sealed partial class SettingsViewModel
{
    private AlertService _alerts = null!;

    [ObservableProperty] private bool _alertsEnabled;
    [ObservableProperty] private string _alertServer = "";
    [ObservableProperty] private string? _alertTopic;
    [ObservableProperty] private ImageSource? _alertQr;
    [ObservableProperty] private string? _alertDeviceLabel;
    [ObservableProperty] private string? _alertTokenInput;
    [ObservableProperty] private bool _alertHasToken;
    [ObservableProperty] private bool _alertQuiet;
    [ObservableProperty] private string? _alertQuietFrom;
    [ObservableProperty] private string? _alertQuietTo;
    [ObservableProperty] private bool _alertFailures;
    [ObservableProperty] private bool _alertApproval;
    [ObservableProperty] private bool _alertLowDisk;
    [ObservableProperty] private double _alertLowDiskGb;
    [ObservableProperty] private bool _alertMaintenance;
    [ObservableProperty] private bool _alertCrashes;
    [ObservableProperty] private bool _alertDriveHealth;
    [ObservableProperty] private bool _alertTemperature;
    [ObservableProperty] private bool _alertFillingFast;
    [ObservableProperty] private double _alertFillingFastGb;
    [ObservableProperty] private bool _alertDigest;
    [ObservableProperty] private string? _alertStatus;

    private void InitAlerts(AlertService alerts)
    {
        _alerts = alerts;
        _alerts.Changed += () => AlertStatus = _alerts.LastResult;
    }

    private void ReloadAlerts()
    {
        var a = _settings.Store.Current.Alerts;
        AlertsEnabled = a.Enabled;
        AlertServer = a.Server;
        AlertTopic = a.Topic;
        AlertDeviceLabel = a.DeviceLabel;
        AlertHasToken = !string.IsNullOrEmpty(a.ProtectedToken);
        AlertQuiet = a.QuietHours;
        AlertQuietFrom = Normalize(a.QuietFrom, "23:00");
        AlertQuietTo = Normalize(a.QuietTo, "08:00");
        AlertFailures = a.Failures;
        AlertApproval = a.ApprovalWaiting;
        AlertLowDisk = a.LowDisk;
        AlertLowDiskGb = a.LowDiskGb;
        AlertMaintenance = a.MaintenanceSummary;
        AlertCrashes = a.Crashes;
        AlertDriveHealth = a.DriveHealth;
        AlertTemperature = a.Temperature;
        AlertFillingFast = a.DriveFillingFast;
        AlertFillingFastGb = a.DriveFillingFastGb;
        AlertDigest = a.WeeklyDigest;
        AlertStatus = _alerts.LastResult;
        AlertQr = Qr(a.Server, a.Topic);
    }

    private string Normalize(string? time, string fallback)
    {
        var t = Core.Maintenance.MaintenanceSchedule.TryParseTime(time, out var ts) ? $"{ts.Hours:00}:{ts.Minutes:00}" : fallback;
        if (!MaintenanceTimes.Contains(t)) MaintenanceTimes.Add(t);
        return t;
    }

    partial void OnAlertsEnabledChanged(bool value)
    {
        SaveAlerts(a => a.Enabled = value);
        if (value && !_loading && !AlertPolicy.IsValidTopic(AlertTopic)) NewAlertTopic();
    }

    partial void OnAlertServerChanged(string value)
    {
        if (AlertPolicy.IsValidServer(value.Trim())) SaveAlerts(a => a.Server = value.Trim().TrimEnd('/'));
        AlertQr = Qr(value.Trim(), AlertTopic);
    }

    partial void OnAlertDeviceLabelChanged(string? value) =>
        SaveAlerts(a => a.DeviceLabel = string.IsNullOrWhiteSpace(value) ? null : new string(value.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').Take(20).ToArray()));

    partial void OnAlertQuietChanged(bool value) => SaveAlerts(a => a.QuietHours = value);
    partial void OnAlertQuietFromChanged(string? value) { if (value is not null) SaveAlerts(a => a.QuietFrom = value); }
    partial void OnAlertQuietToChanged(string? value) { if (value is not null) SaveAlerts(a => a.QuietTo = value); }
    partial void OnAlertFailuresChanged(bool value) => SaveAlerts(a => a.Failures = value);
    partial void OnAlertApprovalChanged(bool value) => SaveAlerts(a => a.ApprovalWaiting = value);
    partial void OnAlertLowDiskChanged(bool value) => SaveAlerts(a => a.LowDisk = value);
    partial void OnAlertLowDiskGbChanged(double value) => SaveAlerts(a => a.LowDiskGb = Math.Clamp((int)Math.Round(value), 1, 1000));
    partial void OnAlertMaintenanceChanged(bool value) => SaveAlerts(a => a.MaintenanceSummary = value);
    partial void OnAlertCrashesChanged(bool value) => SaveAlerts(a => a.Crashes = value);
    partial void OnAlertDriveHealthChanged(bool value) => SaveAlerts(a => a.DriveHealth = value);
    partial void OnAlertTemperatureChanged(bool value) => SaveAlerts(a => a.Temperature = value);
    partial void OnAlertFillingFastChanged(bool value) => SaveAlerts(a => a.DriveFillingFast = value);
    partial void OnAlertFillingFastGbChanged(double value) => SaveAlerts(a => a.DriveFillingFastGb = Math.Clamp((int)Math.Round(value), 1, 1000));
    partial void OnAlertDigestChanged(bool value) => SaveAlerts(a => a.WeeklyDigest = value);

    private void SaveAlerts(Action<AlertSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Alerts));
    }

    /// <summary>A new random topic: anyone subscribed to the old one stops getting alerts.</summary>
    [RelayCommand]
    private void NewAlertTopic()
    {
        var topic = AlertPolicy.NewTopic();
        _settings.Store.Update(s => s.Alerts.Topic = topic);
        AlertTopic = topic;
        AlertQr = Qr(AlertServer, topic);
    }

    [RelayCommand]
    private void CopyAlertTopic() => Platform.CopyText(AlertTopic);

    [RelayCommand]
    private void SaveAlertToken()
    {
        if (string.IsNullOrWhiteSpace(AlertTokenInput)) return;
        var protectedToken = Dpapi.Protect(AlertTokenInput.Trim());
        AlertTokenInput = null;
        if (protectedToken is null)
        {
            Message = "Couldn't encrypt the token, so it wasn't saved.";
            return;
        }
        _settings.Store.Update(s => s.Alerts.ProtectedToken = protectedToken);
        AlertHasToken = true;
    }

    [RelayCommand]
    private void RemoveAlertToken()
    {
        _settings.Store.Update(s => s.Alerts.ProtectedToken = null);
        AlertHasToken = false;
    }

    [RelayCommand]
    private async Task SendTestAlertAsync()
    {
        if (!AlertPolicy.IsValidTopic(AlertTopic)) NewAlertTopic();
        AlertStatus = "Sending…";
        var (ok, message) = await _alerts.SendTestAsync();
        AlertStatus = ok ? "Test alert sent. It should be on your phone now." : $"Couldn't send: {message}";
    }

    /// <summary>The topic as a QR code: scan it with the iPhone camera, copy the text, paste it into the
    /// ntfy app's "Subscribe to topic". A self-hosted server is included as server/topic.</summary>
    private static ImageSource? Qr(string? server, string? topic)
    {
        if (!AlertPolicy.IsValidTopic(topic)) return null;
        var text = server is null || server.TrimEnd('/').Equals("https://ntfy.sh", StringComparison.OrdinalIgnoreCase) ? topic! : $"{server.TrimEnd('/')}/{topic}";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(5);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
