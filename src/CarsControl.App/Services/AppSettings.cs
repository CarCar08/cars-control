using System.IO;
using CarsControl.Core.Data;
using CarsControl.Core.Settings;
using Microsoft.Win32;

namespace CarsControl.App.Services;

/// <summary>Your choices, persisted in settings.json (see <see cref="SettingsStore"/>; values from
/// cars.db move over once). "Start with Windows" is the HKCU Run value itself, written only when you
/// flip the toggle; "last notified" is state, so it stays in cars.db.</summary>
public sealed class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CarsControl";
    private readonly CarsDb _db;

    public SettingsStore Store { get; }

    public AppSettings(CarsDb db)
    {
        _db = db;
        Store = new SettingsStore(migrateFrom: db.GetSetting);
        ActiveProfile.Current = Store.Current.Profile;
        Store.Saved += () => ActiveProfile.Current = Store.Current.Profile;
    }

    private CarsSettings S => Store.Current;

    public AppearanceSettings Appearance => S.Appearance;
    public ProfileSettings Profile => S.Profile;

    public bool RunInBackground
    {
        get => S.RunInBackground;
        set => Store.Update(s => s.RunInBackground = value);
    }

    public int CheckIntervalHours
    {
        get => Math.Clamp(S.CheckIntervalHours, 1, 48);
        set => Store.Update(s => s.CheckIntervalHours = Math.Clamp(value, 1, 48));
    }

    public int HelperIdleMinutes
    {
        get => Math.Clamp(S.HelperIdleMinutes, 1, 240);
        set => Store.Update(s => s.HelperIdleMinutes = Math.Clamp(value, 1, 240));
    }

    /// <summary>Use WizTree's master-file-table reader through the admin helper for drive scans.</summary>
    public bool FastScan
    {
        get => S.FastScan;
        set => Store.Update(s => s.FastScan = value);
    }

    public IReadOnlyList<string> ProtectedApps
    {
        get => S.ProtectedApps ?? Defaults.ProtectedApps;
        set => Store.Update(s => s.ProtectedApps = value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Small UI choices worth keeping between runs (Storage sort orders and selected drives).</summary>
    public string? GetUi(string key) => S.Ui.TryGetValue(key, out var v) ? v : null;

    public void SetUi(string key, string? value)
    {
        if (GetUi(key) == value) return;
        Store.Update(s =>
        {
            if (value is null) s.Ui.Remove(key);
            else s.Ui[key] = value;
        });
    }

    public string? LastNotified
    {
        get => _db.GetSetting("last_notified");
        set => _db.SetSetting("last_notified", value);
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CarsControl.exe");

    public bool StartWithWindows
    {
        get => StartupCommand is not null;
        set => StartupCommand = value ? $"\"{ExePath}\" --background" : null;
    }

    public string? StartupCommand
    {
        get
        {
            if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_DATA_ROOT") is { } root && Path.IsPathFullyQualified(root))
                return GetUi("dev.startup-command");
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) as string;
        }
        set
        {
            if (App.IsDevInstance && Environment.GetEnvironmentVariable("CARS_DEV_DATA_ROOT") is { } root && Path.IsPathFullyQualified(root))
            { SetUi("dev.startup-command", value); return; }
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value is not null) k.SetValue(RunValue, value);
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
