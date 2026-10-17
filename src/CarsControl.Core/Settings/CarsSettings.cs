using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarsControl.Core.Settings;

/// <summary>
/// Everything you can choose, in one plain JSON file (%LOCALAPPDATA%\CarsControl\settings.json) so it can
/// be read, edited, backed up and later synced between devices. History, scan caches and update results
/// stay in cars.db: they describe this PC, not your preferences.
/// </summary>
public sealed class CarsSettings
{
    public int Version { get; set; } = 1;
    public AppearanceSettings Appearance { get; set; } = new();
    public bool RunInBackground { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 6;
    public int HelperIdleMinutes { get; set; } = 15;
    public bool FastScan { get; set; } = true;

    /// <summary>Apps that can't be ticked for uninstall ("name starts with"). Null = <see cref="Defaults.ProtectedApps"/>.</summary>
    public List<string>? ProtectedApps { get; set; }

    public ProfileSettings Profile { get; set; } = new();

    public MaintenanceSettings Maintenance { get; set; } = new();
    public GamingSettings Gaming { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public PaletteSettings Palette { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public RestartSettings Restart { get; set; } = new();
    public HealthSettings Health { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    /// <summary>The version that finished (or skipped) the first-time setup; null until then.</summary>
    public string? FirstRunCompleted { get; set; }
    public CarsControl.Core.Help.SetupUndo? FirstRunUndo { get; set; }

    /// <summary>Small remembered UI choices (sort orders, selected drives).</summary>
    public Dictionary<string, string> Ui { get; set; } = new();
}

public sealed class AppearanceSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;
    public BackdropChoice Backdrop { get; set; } = BackdropChoice.Translucent;
    public AccentChoice Accent { get; set; } = AccentChoice.Cars;

    /// <summary>#RRGGBB, used when <see cref="Accent"/> is Custom.</summary>
    public string CustomAccent { get; set; } = Defaults.CarsTeal;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThemeChoice { Dark, Light, System }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackdropChoice { Translucent, Solid }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccentChoice { Cars, Windows, Custom }

/// <summary>
/// Choices that depend on what's going on with a particular PC. On a fresh install everything is off;
/// an install upgraded from round 3 keeps behaving as before (both holds on, for the dxgkrnl BSOD
/// investigation on the development PC).
/// </summary>
public sealed class ProfileSettings
{
    /// <summary>Windows Update driver updates are listed but unticked.</summary>
    public bool HoldDriverUpdates { get; set; }

    public string? HoldDriverUpdatesReason { get; set; }

    /// <summary>Crash dumps and error reports are left out of the default cleanup (they're evidence).</summary>
    public bool KeepCrashEvidence { get; set; }

    public string? KeepCrashEvidenceReason { get; set; }

    /// <summary>What kind of PC this is, for the Recommended badges (Auto: a battery means Laptop).</summary>
    public CarsControl.Core.Help.PcType PcType { get; set; } = CarsControl.Core.Help.PcType.Auto;
}

public static class Defaults
{
    public const string CarsTeal = "#18A08C";

    /// <summary>What a fresh install protects: things Windows, drivers and C.A.R.S. itself depend on.
    /// Your own must-keep apps (VPN, password manager, …) are added in Settings or right-click › Protect.</summary>
    public static readonly IReadOnlyList<string> ProtectedApps =
    [
        "C.A.R.S. Control", "Microsoft .NET", "Microsoft Windows Desktop Runtime", "Microsoft ASP.NET", "Microsoft Visual C++",
        "Microsoft Edge", "Microsoft Edge WebView2", "App Installer", "Microsoft Store", "PowerShell", "Windows Subsystem for Linux",
        "Windows Terminal", "NVIDIA Graphics Driver", "AMD Chipset", "Salad",
    ];
}

/// <summary>Loads and saves settings.json (atomically), creating it from the old cars.db values once.</summary>
public sealed class SettingsStore
{
    // Enums as names ("Sunday", not 0): the file is meant to be readable and hand-editable.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly object _gate = new();

    public CarsSettings Current { get; private set; }

    /// <summary>True when there was no settings.json and nothing to migrate from cars.db: a brand-new install,
    /// which gets the first-time setup. Someone upgrading from an earlier version doesn't.</summary>
    public bool IsFreshInstall { get; }

    /// <summary>Raised after every save (the Appearance page and sync use it).</summary>
    public event Action? Saved;

    public static string DefaultPath =>
        Path.Combine(Util.UserData.Root, "settings.json");

    /// <param name="migrateFrom">Reads an old cars.db setting by key; used only when settings.json doesn't exist yet.</param>
    public SettingsStore(string? path = null, Func<string, string?>? migrateFrom = null)
    {
        _path = path ?? DefaultPath;
        var existed = File.Exists(_path);
        IsFreshInstall = !existed && (migrateFrom is null
            || (migrateFrom("run_in_background") is null && migrateFrom("check_interval_hours") is null && migrateFrom("protected_apps") is null));
        Current = Load() ?? Migrate(migrateFrom);
        if (!File.Exists(_path)) Save();
    }

    private CarsSettings? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<CarsSettings>(File.ReadAllText(_path), Json) : null;
        }
        catch (JsonException)
        {
            // A hand-edit gone wrong: keep the broken file for reference and start from defaults.
            File.Copy(_path, _path + ".broken", overwrite: true);
            return null;
        }
    }

    /// <summary>Round-3 installs kept everything in cars.db. Bring it over and keep this PC's behavior.</summary>
    internal static CarsSettings Migrate(Func<string, string?>? old)
    {
        var s = new CarsSettings();
        if (old is null || old("run_in_background") is null && old("check_interval_hours") is null && old("protected_apps") is null)
            return s; // fresh install: generic defaults
        s.RunInBackground = old("run_in_background") != "0";
        if (int.TryParse(old("check_interval_hours"), out var h)) s.CheckIntervalHours = Math.Clamp(h, 1, 48);
        if (int.TryParse(old("helper_idle_minutes"), out var m)) s.HelperIdleMinutes = Math.Clamp(m, 1, 240);
        s.FastScan = old("fast_scan") != "0";
        s.ProtectedApps = old("protected_apps") is { Length: > 0 } list
            ? list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [.. RoundThreeProtectedApps];
        s.Profile = new ProfileSettings
        {
            HoldDriverUpdates = true,
            HoldDriverUpdatesReason = "Open BSOD investigation (dxgkrnl, since NVIDIA 616.56)",
            KeepCrashEvidence = true,
            KeepCrashEvidenceReason = "Evidence for the open dxgkrnl BSOD investigation",
        };
        return s;
    }

    /// <summary>The list round 3 shipped with (this PC's security/network stack + runtimes).</summary>
    internal static readonly IReadOnlyList<string> RoundThreeProtectedApps =
    [
        "Windhawk", "Tailscale", "Proton VPN", "ProtonVPN", "Proton Drive", "Proton Mail", "Proton Pass",
        "Cryptomator", "Yubico", "YubiKey", "NVIDIA App", "NVIDIA Graphics Driver", "AMD Software", "AMD Chipset",
        "Razer Synapse", "Windows Terminal", "Microsoft .NET", "Microsoft Windows Desktop Runtime",
        "Microsoft ASP.NET", "Microsoft Visual C++", "Microsoft Edge", "Microsoft Edge WebView2", "App Installer",
        "Microsoft Store", "C.A.R.S. Control", "PowerShell", "Windows Subsystem for Linux",
    ];

    public void Update(Action<CarsSettings> change)
    {
        lock (_gate) change(Current);
        Save();
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        Saved?.Invoke();
    }
}
