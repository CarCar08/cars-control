namespace CarsControl.Core.Settings;

/// <summary>Scheduled maintenance: updates and cleanup on a schedule, as you. Admin parts wait for
/// your approval unless an approved admin session is already open.</summary>
public sealed class MaintenanceSettings
{
    public bool Enabled { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Sunday];

    /// <summary>24-hour "HH:mm", local time.</summary>
    public string Time { get; set; } = "03:00";

    public bool RunUpdates { get; set; } = true;

    /// <summary>Update sources included (UpdateSource names). Null = all of them.</summary>
    public List<string>? UpdateSources { get; set; }

    public bool RunCleanup { get; set; } = true;

    /// <summary>Cleanup rule ids. Null = the categories ticked by default on the Cleanup page.</summary>
    public List<string>? CleanupRules { get; set; }

    /// <summary>A run is only started after this long without keyboard or mouse input.</summary>
    public int IdleMinutes { get; set; } = 5;
}

/// <summary>Gaming mode: what C.A.R.S. pauses while you play. Salad, notifications and your power plan
/// are deliberately not touched.</summary>
public sealed class GamingSettings
{
    /// <summary>Turn on when a game from your libraries starts, off 30 s after the last one closes.</summary>
    public bool Automatic { get; set; } = true;

    public bool PauseWindowsUpdate { get; set; } = true;
    public bool PauseSearchIndexing { get; set; } = true;
    public bool FreeStandbyMemory { get; set; } = true;

    /// <summary>Maintenance that came due during a game runs once you've been idle afterwards.</summary>
    public bool MaintenanceAfterPlay { get; set; } = true;

    /// <summary>Ask for the admin session once at sign-in so no prompt appears mid-game.</summary>
    public bool ApproveAtSignIn { get; set; } = true;

    /// <summary>Extra program names (e.g. "eldenring.exe") that count as games.</summary>
    public List<string> ExtraGames { get; set; } = [];

    /// <summary>Program names in game folders that never count (launchers, anti-cheat, crash reporters).</summary>
    public List<string> IgnoredPrograms { get; set; } = [];
}

/// <summary>Phone alerts through ntfy. The topic is the only key to your alerts, so it's random.</summary>
public sealed class AlertSettings
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "https://ntfy.sh";
    public string? Topic { get; set; }

    /// <summary>Optional short name shown in alerts ("Desktop") when you have several PCs. Empty = none.</summary>
    public string? DeviceLabel { get; set; }

    /// <summary>Optional access token, encrypted for this Windows account (DPAPI); never stored in plain text.</summary>
    public string? ProtectedToken { get; set; }

    public bool QuietHours { get; set; } = true;
    public string QuietFrom { get; set; } = "23:00";
    public string QuietTo { get; set; } = "08:00";

    public bool Failures { get; set; } = true;
    public bool ApprovalWaiting { get; set; } = true;
    public bool LowDisk { get; set; } = true;
    public int LowDiskGb { get; set; } = 20;
    public int LowDiskPercent { get; set; } = 10;
    public bool MaintenanceSummary { get; set; } = true;
    public bool Crashes { get; set; } = true;
    public bool DriveHealth { get; set; } = true;
    public bool Temperature { get; set; } = true;
    public bool DriveFillingFast { get; set; } = true;
    public int DriveFillingFastGb { get; set; } = 15;
    public bool WeeklyDigest { get; set; } = true;
}

/// <summary>The command palette (Ctrl+K in the app, and a global shortcut).</summary>
public sealed class PaletteSettings
{
    public bool GlobalHotkey { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>How often each result was chosen; frequent ones rank higher.</summary>
    public Dictionary<string, int> Usage { get; set; } = new();
}

/// <summary>C.A.R.S. Control updating itself from its GitHub Releases.</summary>
public sealed class UpdateSettings
{
    /// <summary>owner/name. A setting so a fork can point at its own releases.</summary>
    public string Repository { get; set; } = "CarCar08/cars-control";

    public bool AutoCheck { get; set; } = true;
    public bool IncludePreReleases { get; set; }

    /// <summary>"Skip this version": not offered again (a manual check still shows it).</summary>
    public string? SkippedVersion { get; set; }
}

/// <summary>Restart when you're away: finishes updates at a quiet time instead of surprising you.</summary>
public sealed class RestartSettings
{
    public bool Enabled { get; set; }

    /// <summary>24-hour "HH:mm": the earliest time a restart may start.</summary>
    public string Time { get; set; } = "04:00";

    /// <summary>How long after <see cref="Time"/> a restart may still start (one try per night).</summary>
    public int WindowHours { get; set; } = 3;

    /// <summary>Only after this long without keyboard or mouse input.</summary>
    public int IdleMinutes { get; set; } = 30;
}

/// <summary>The Health page: temperature alerts and the safety net before risky changes.</summary>
public sealed class HealthSettings
{
    public bool TemperatureAlerts { get; set; } = true;
    public int CpuAlertC { get; set; } = 90;
    public int GpuAlertC { get; set; } = 85;

    /// <summary>A restore point before driver updates, feature updates and privacy batches (when System Protection is on).</summary>
    public bool RestorePointBeforeRiskyChanges { get; set; } = true;

    /// <summary>Include the CPU and board sensors (needs the PawnIO driver and admin) when the Health page is open.</summary>
    public bool ReadBoardSensors { get; set; } = true;
}

/// <summary>Keeping your setup (and a status line per PC) in a cloud folder such as Proton Drive.</summary>
public sealed class SyncSettings
{
    public bool Enabled { get; set; }

    /// <summary>Null = the "C.A.R.S." folder inside Proton Drive, found automatically.</summary>
    public string? Folder { get; set; }

    // What to sync. Alerts are off: the topic is the key to your phone alerts.
    public bool Preferences { get; set; } = true;
    public bool Ignored { get; set; } = true;
    public bool Protected { get; set; } = true;
    public bool Apps { get; set; } = true;
    public bool Alerts { get; set; }

    /// <summary>Write a short status file for this PC so "Your PCs" can list it elsewhere.</summary>
    public bool PublishStatus { get; set; } = true;

    /// <summary>Fingerprint of the setup at the last sync, to tell which side changed since.</summary>
    public string? LastHash { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}

/// <summary>One backup: which folders, where to, how long old versions are kept, how often.</summary>
public sealed class BackupJobSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Backup";
    public bool Enabled { get; set; } = true;
    public List<string> Sources { get; set; } = [];

    /// <summary>"folder" (a folder on any drive, e.g. in Proton Drive), or "usb" (a drive recognised by its volume ID).</summary>
    public string Kind { get; set; } = "folder";
    public string? Folder { get; set; }
    public string? UsbSerial { get; set; }
    public string? UsbLabel { get; set; }
    public string UsbSubfolder { get; set; } = "C.A.R.S. Backup";

    public int KeepDays { get; set; } = 30;
    public int IntervalHours { get; set; } = 24;
    public List<string> Excludes { get; set; } = ["*.tmp", "Thumbs.db", "desktop.ini"];

    public DateTime? LastRunUtc { get; set; }
    public bool? LastOk { get; set; }
    public string? LastResult { get; set; }
}

public sealed class BackupSettings
{
    public List<BackupJobSettings> Jobs { get; set; } = [];
}

/// <summary>A folder moved to another drive, with a link left in its place, so it can be moved back.</summary>
public sealed class LinkedMoveRecord
{
    public string Original { get; set; } = "";
    public string NewLocation { get; set; } = "";
    public DateTime When { get; set; }
    public long Bytes { get; set; }
}

public sealed class StorageSettings
{
    public List<LinkedMoveRecord> LinkedMoves { get; set; } = [];
}
