using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CarsControl.App.Services;
using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Inventory;
using CarsControl.Core.Settings;
using CarsControl.Core.Uninstall;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly CarsDb _db;
    private readonly HelperClient _helper;
    private readonly InventoryStore _inventory;
    private readonly UpdateCenter _updates;

    public ObservableCollection<IgnoreRule> IgnoreRules { get; } = [];

    [ObservableProperty] private bool _runInBackground;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private double _checkIntervalHours;
    [ObservableProperty] private double _helperIdleMinutes;
    [ObservableProperty] private string _protectedText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _helperStatus;
    [ObservableProperty] private bool _uniGetUiImportAvailable;

    // ---- Appearance ----
    public IReadOnlyList<Choice<ThemeChoice>> ThemeOptions { get; } = [new(ThemeChoice.Dark, "Dark"), new(ThemeChoice.Light, "Light"), new(ThemeChoice.System, "Same as Windows")];
    public IReadOnlyList<Choice<BackdropChoice>> BackdropOptions { get; } = [new(BackdropChoice.Translucent, "Translucent (wallpaper shows through)"), new(BackdropChoice.Solid, "Solid")];
    public IReadOnlyList<Choice<AccentChoice>> AccentOptions { get; } = [new(AccentChoice.Cars, "C.A.R.S. teal"), new(AccentChoice.Windows, "Windows accent color"), new(AccentChoice.Custom, "Custom")];

    [ObservableProperty] private Choice<ThemeChoice>? _theme;
    [ObservableProperty] private Choice<BackdropChoice>? _backdrop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomAccent))]
    private Choice<AccentChoice>? _accent;

    [ObservableProperty] private string _customAccent = Defaults.CarsTeal;
    public bool IsCustomAccent => Accent?.Value == AccentChoice.Custom;

    // ---- This PC (profile) ----
    [ObservableProperty] private bool _holdDriverUpdates;
    [ObservableProperty] private string? _holdDriverReason;
    [ObservableProperty] private bool _keepCrashEvidence;
    [ObservableProperty] private string? _keepCrashReason;

    private bool _loading;

    public string Version => $"{AppVersionText} · {RuntimeInformation.FrameworkDescription}";
    public string DatabasePath => _db.DatabasePath;
    public string SettingsPath => Core.Settings.SettingsStore.DefaultPath;
    public string UserQuarantine => Quarantine.UserRoot;
    public string HelperLogs => Path.Combine(SecureDataDir.Root, "logs");

    public SettingsViewModel(AppSettings settings, CarsDb db, HelperClient helper, InventoryStore inventory, UpdateCenter updates,
        MaintenanceService maintenance, GameMode game, GamingEffects gaming, GameWatcher watcher, AlertService alerts, GlobalHotkey hotkey, SelfUpdateService selfUpdate)
    {
        _settings = settings;
        _db = db;
        _helper = helper;
        _inventory = inventory;
        _updates = updates;
        InitMaintenance(maintenance);
        InitGaming(game, gaming, watcher);
        InitAlerts(alerts);
        InitPalette(hotkey);
        InitUpdates(selfUpdate);
        Reload();
    }

    [RelayCommand]
    public void Reload()
    {
        _loading = true;
        var a = _settings.Appearance;
        Theme = ThemeOptions.First(o => o.Value == a.Theme);
        Backdrop = BackdropOptions.First(o => o.Value == a.Backdrop);
        Accent = AccentOptions.First(o => o.Value == a.Accent);
        CustomAccent = a.CustomAccent;
        var p = _settings.Profile;
        HoldDriverUpdates = p.HoldDriverUpdates;
        HoldDriverReason = p.HoldDriverUpdatesReason;
        KeepCrashEvidence = p.KeepCrashEvidence;
        KeepCrashReason = p.KeepCrashEvidenceReason;
        ReloadMaintenance();
        ReloadGaming();
        ReloadAlerts();
        ReloadHealth();
        ReloadPcType();
        ReloadPalette();
        ReloadUpdates();
        _loading = false;

        RunInBackground = _settings.RunInBackground;
        StartWithWindows = _settings.StartWithWindows;
        CheckIntervalHours = _settings.CheckIntervalHours;
        HelperIdleMinutes = _settings.HelperIdleMinutes;
        ProtectedText = string.Join(Environment.NewLine, _settings.ProtectedApps);
        IgnoreRules.Clear();
        foreach (var r in _db.IgnoreRules().Values.OrderBy(r => r.Key)) IgnoreRules.Add(r);
        UniGetUiImportAvailable = !App.IsDemo && _db.GetSetting(UniGetUiImporter.ImportedSettingKey) is null or "declined" && UniGetUiImporter.Preview() is not null;
        HelperStatus = _helper.IsConnected ? $"Admin session active ({_helper.Hello?.User})" : "No admin session running";
    }

    partial void OnThemeChanged(Choice<ThemeChoice>? value) => SaveAppearance(a => a.Theme = value!.Value);
    partial void OnBackdropChanged(Choice<BackdropChoice>? value) => SaveAppearance(a => a.Backdrop = value!.Value);
    partial void OnAccentChanged(Choice<AccentChoice>? value) => SaveAppearance(a => a.Accent = value!.Value);

    partial void OnCustomAccentChanged(string value)
    {
        if (AppearanceService.TryParse(value, out _)) SaveAppearance(a => a.CustomAccent = value.ToUpperInvariant());
    }

    /// <summary>Appearance changes apply immediately, to every open window.</summary>
    private void SaveAppearance(Action<AppearanceSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Appearance));
        AppearanceService.Apply(_settings.Appearance);
    }

    partial void OnHoldDriverUpdatesChanged(bool value) => SaveProfile(p => p.HoldDriverUpdates = value);
    partial void OnHoldDriverReasonChanged(string? value) => SaveProfile(p => p.HoldDriverUpdatesReason = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    partial void OnKeepCrashEvidenceChanged(bool value) => SaveProfile(p => p.KeepCrashEvidence = value);
    partial void OnKeepCrashReasonChanged(string? value) => SaveProfile(p => p.KeepCrashEvidenceReason = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    private void SaveProfile(Action<ProfileSettings> change)
    {
        if (_loading) return;
        _settings.Store.Update(s => change(s.Profile));
    }

    partial void OnRunInBackgroundChanged(bool value)
    {
        if (_settings.RunInBackground == value) return;
        _settings.RunInBackground = value;
        if (value) _updates.StartBackground();
        else _updates.StopBackground();
        Message = value ? "Closing the window now keeps C.A.R.S. in the tray and checks for updates in the background." : "Closing the window now exits the app.";
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_settings.StartWithWindows == value) return;
        _settings.StartWithWindows = value;
        Message = value ? "C.A.R.S. will start minimised to the tray when you sign in." : "Removed from startup.";
    }

    partial void OnCheckIntervalHoursChanged(double value) => _settings.CheckIntervalHours = (int)Math.Round(value);

    partial void OnHelperIdleMinutesChanged(double value)
    {
        _settings.HelperIdleMinutes = (int)Math.Round(value);
        _helper.IdleMinutes = _settings.HelperIdleMinutes; // applies to the next admin session
    }

    [RelayCommand]
    private async Task SaveProtected()
    {
        var list = ProtectedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.ProtectedApps = list;
        _inventory.Protected = new ProtectedApps(list);
        Message = $"Saved {list.Count} protected name(s). Rescanning…";
        await _inventory.RefreshAsync();
        Message = $"Saved {list.Count} protected name(s).";
    }

    [RelayCommand]
    private void ResetProtected() => ProtectedText = string.Join(Environment.NewLine, ProtectedApps.Defaults);

    [RelayCommand]
    private void RemoveIgnore(IgnoreRule? rule)
    {
        if (rule is null) return;
        _db.RemoveIgnore(rule.Key);
        IgnoreRules.Remove(rule);
        Message = $"{rule.Key} will show up in Refresh again.";
    }

    [RelayCommand]
    private void ImportUniGetUi()
    {
        var (rules, history) = UniGetUiImporter.Import(_db);
        Message = $"Imported {rules} ignore rule(s) and {history} history entries.";
        Reload();
    }

    /// <summary>Starts (or reuses) the admin session and asks the helper who it is. Shows one UAC prompt if no session is active.</summary>
    [RelayCommand]
    private async Task TestHelperAsync()
    {
        HelperStatus = "Starting the admin helper…";
        try
        {
            var r = await _helper.SendAsync(new PingOp());
            HelperStatus = _helper.Hello is { } h ? (h.Elevated ? $"Works: the admin helper runs as {h.User} with administrator rights" : $"It answered, but without administrator rights (running as {h.User})") + $", and closes after {_helper.IdleMinutes} idle minutes." : r.Message;
        }
        catch (HelperCancelledException)
        {
            HelperStatus = "Admin approval was cancelled.";
        }
        catch (Exception ex)
        {
            HelperStatus = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private static void Open(string? path)
    {
        if (path is null) return;
        Platform.ShowInFileManager(path); // a file is shown selected in its folder
    }
}
