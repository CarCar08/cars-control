using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarsControl.Contracts;

/// <summary>
/// Operations the elevated helper will perform. Each carries identifiers only, never a command line
/// or a filesystem path: the helper resolves what to run itself, from the registry or its own code.
/// That way, a session-long elevated process can't be talked into running arbitrary commands.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingOp), "ping")]
[JsonDerivedType(typeof(WingetUpgradeOp), "winget-upgrade")]
[JsonDerivedType(typeof(WingetUninstallOp), "winget-uninstall")]
[JsonDerivedType(typeof(ArpUninstallOp), "arp-uninstall")]
[JsonDerivedType(typeof(StoreUpdateOp), "store-update")]
[JsonDerivedType(typeof(WindowsUpdateInstallOp), "wu-install")]
[JsonDerivedType(typeof(WslUpdateOp), "wsl-update")]
[JsonDerivedType(typeof(PipUpgradeOp), "pip-upgrade")]
[JsonDerivedType(typeof(CreateRestorePointOp), "restore-point")]
[JsonDerivedType(typeof(CleanupRunOp), "cleanup-run")]
[JsonDerivedType(typeof(CleanupEstimateOp), "cleanup-estimate")]
[JsonDerivedType(typeof(LeftoverScanOp), "leftover-scan")]
[JsonDerivedType(typeof(QuarantineOp), "quarantine")]
[JsonDerivedType(typeof(RestoreQuarantineOp), "quarantine-restore")]
[JsonDerivedType(typeof(StorageScanOp), "storage-scan")]
[JsonDerivedType(typeof(WindowsUpdatePauseOp), "wu-pause")]
[JsonDerivedType(typeof(PurgeStandbyListOp), "purge-standby")]
[JsonDerivedType(typeof(ServiceControlOp), "service-control")]
[JsonDerivedType(typeof(TweakOp), "tweak")]
[JsonDerivedType(typeof(RemoveOldWindowsOp), "remove-old-windows")]
[JsonDerivedType(typeof(StartupEntryOp), "startup-entry")]
[JsonDerivedType(typeof(ServiceStartTypeOp), "service-start-type")]
[JsonDerivedType(typeof(ScheduledTaskToggleOp), "task-toggle")]
[JsonDerivedType(typeof(WingetInstallOp), "winget-install")]
[JsonDerivedType(typeof(DriveHealthOp), "drive-health")]
[JsonDerivedType(typeof(PowerRequestsOp), "power-requests")]
[JsonDerivedType(typeof(BootPerformanceOp), "boot-performance")]
[JsonDerivedType(typeof(DriverDeleteOp), "driver-delete")]
[JsonDerivedType(typeof(WindowsUpdatePolicyOp), "wu-policy")]
[JsonDerivedType(typeof(HideUpdateOp), "wu-hide")]
[JsonDerivedType(typeof(EnableSystemRestoreOp), "restore-enable")]
[JsonDerivedType(typeof(SensorsReadOp), "sensors-read")]
[JsonDerivedType(typeof(InstallPawnIoOp), "pawnio-install")]
[JsonDerivedType(typeof(CopyCrashDumpOp), "crashdump-copy")]
[JsonDerivedType(typeof(RemoveMovedGameOp), "moved-game-remove")]
public abstract record HelperOp;

/// <summary>Privacy: applies, undoes or resets one switch from the helper's own catalog, by id.
/// Action is "apply", "undo" or "default". Data: the switch's state afterwards ("On", "Off", "Partly").</summary>
public sealed record TweakOp(string Id, string Action) : HelperOp;

/// <summary>Gaming mode: pauses Windows Update the way Settings does (with a 12-hour safety expiry), or
/// resumes it. Resume only undoes a pause this app made; a pause you set yourself is left alone.
/// Data: "paused" / "already-paused" / "resumed" / "not-ours".</summary>
public sealed record WindowsUpdatePauseOp(bool Pause) : HelperOp;

/// <summary>Gaming mode: empties the standby list (what ISLC does). Data: bytes freed.</summary>
public sealed record PurgeStandbyListOp : HelperOp;

/// <summary>Gaming mode: stops or starts a service from a fixed allow-list (only Windows Search, "WSearch").
/// Data: "was-running" / "was-stopped".</summary>
public sealed record ServiceControlOp(string Service, bool Start) : HelperOp;

/// <summary>Fast scan of a fixed NTFS drive with WizTree's MFT reader (needs admin). Drive is a single
/// letter; the result Data is the path of the CSV the helper wrote into its own scans folder.</summary>
public sealed record StorageScanOp(string Drive) : HelperOp;

public sealed record PingOp : HelperOp;

/// <summary>Runs `winget upgrade --id {Id} --exact` as the admin account.</summary>
public sealed record WingetUpgradeOp(string Id, string? Version = null) : HelperOp;

/// <summary>`winget uninstall --id {Id}`. Hive/KeyName name the HKLM entry it removes, so the helper
/// can snapshot it first for a later leftover scan.</summary>
public sealed record WingetUninstallOp(string Id, string? Hive = null, string? KeyName = null) : HelperOp;

/// <summary>Uninstalls a machine-wide ARP entry. The helper reads the entry itself and plans the
/// method (MSI / quiet string / winget / detected silent switches). HKCU is refused: per-user apps
/// belong to you. Interactive must be true for uninstallers that can't run silently.</summary>
public sealed record ArpUninstallOp(string Hive, string KeyName, bool Interactive = false) : HelperOp;

public sealed record StoreUpdateOp : HelperOp;

public sealed record WindowsUpdateInstallOp(IReadOnlyList<string> UpdateIds) : HelperOp;

public sealed record WslUpdateOp : HelperOp;

public sealed record PipUpgradeOp(string Package) : HelperOp;

public sealed record CreateRestorePointOp(string Description) : HelperOp;

/// <summary>Rule ids are defined in the helper's own code; unknown ids are rejected.</summary>
public sealed record CleanupRunOp(IReadOnlyList<string> RuleIds) : HelperOp;

public sealed record CleanupEstimateOp(IReadOnlyList<string> RuleIds) : HelperOp;

/// <summary>Scans machine-wide locations for leftovers of an app *this helper session* uninstalled
/// (it uses its own pre-uninstall snapshot, not data from the app). Results come back as opaque
/// tokens; <see cref="QuarantineOp"/> only accepts tokens this helper session produced.</summary>
public sealed record LeftoverScanOp(string Hive, string KeyName) : HelperOp;

public sealed record QuarantineOp(IReadOnlyList<string> Tokens) : HelperOp;

public sealed record RestoreQuarantineOp(string QuarantineId) : HelperOp;

// ---- Health (round 6) ----

/// <summary>Storage › Old Windows: removes one top-level folder (Windows, Users, Program Files, Program Files (x86) or
/// ProgramData) of a leftover Windows installation on another drive. The helper re-checks that the drive holds an old Windows,
/// isn't the running one or a boot drive, and that no installed program still points into the folder.</summary>
public sealed record RemoveOldWindowsOp(string Folder) : HelperOp;

/// <summary>Apps › Startup: switches a machine-wide Run entry ("run", "run32") or a common Startup-folder shortcut ("folder")
/// on or off the way Task Manager does. Name must already exist there.</summary>
public sealed record StartupEntryOp(string Where, string Name, bool Enable) : HelperOp;

/// <summary>Apps › Startup: a service that isn't Microsoft's and isn't protected goes to "auto", "manual" or "disabled".</summary>
public sealed record ServiceStartTypeOp(string Service, string StartType) : HelperOp;

/// <summary>Apps › Startup: a scheduled task outside \Microsoft\ (and not one of C.A.R.S.'s or the protected ones) on or off.</summary>
public sealed record ScheduledTaskToggleOp(string TaskPath, bool Enable) : HelperOp;

/// <summary>`winget install --id {Id} --exact --scope machine` as the admin account: an app for every user of the PC.
/// Source is "winget" or "msstore". For apps that only come machine-wide, or that you want for all accounts.</summary>
public sealed record WingetInstallOp(string Id, string Source = "winget", string? Version = null) : HelperOp;

/// <summary>Reads each drive's temperature, wear, power-on hours and error counts (Windows only gives them to
/// administrators). Data: JSON list of drive readings.</summary>
public sealed record DriveHealthOp : HelperOp;

/// <summary>Runs `powercfg /requests`: what is stopping the PC from sleeping. Data: its text output.</summary>
public sealed record PowerRequestsOp : HelperOp;

/// <summary>Reads boot and shutdown times from the Diagnostics-Performance log (administrators only).
/// Data: JSON boot report for the last <paramref name="Days"/> days (1 to 90).</summary>
public sealed record BootPerformanceOp(int Days = 30) : HelperOp;

/// <summary>Deletes one old driver package from the driver store with `pnputil /delete-driver oemN.inf`.
/// The helper re-derives the list itself and only accepts an older copy of a driver that no device uses;
/// there's no /force and no /uninstall.</summary>
public sealed record DriverDeleteOp(string PublishedName) : HelperOp;

/// <summary>Windows Update settings from a fixed list: "pause-until" (a date up to 35 days ahead, or "resume"),
/// "exclude-drivers" ("on"/"default"), "defer-feature-days" ("0".."365" or "default") and "active-hours"
/// ("8-23" style, or "default"). Anything else is rejected.</summary>
public sealed record WindowsUpdatePolicyOp(string Setting, string Value) : HelperOp;

/// <summary>Hides or shows one Windows Update (by its GUID) so it isn't offered again.</summary>
public sealed record HideUpdateOp(string UpdateId, bool Hide) : HelperOp;

/// <summary>Turns System Protection (restore points) on for C: only.</summary>
public sealed record EnableSystemRestoreOp : HelperOp;

/// <summary>Reads CPU, board, fan and other sensors with LibreHardwareMonitor and the PawnIO driver.
/// Data: JSON sensor list. Fails with "pawnio-missing" in Data when the driver isn't installed.</summary>
public sealed record SensorsReadOp : HelperOp;

/// <summary>Installs the PawnIO driver from its official release. Only the release whose SHA-256 is pinned
/// in the helper is accepted; the file is downloaded by the helper to its own folder and checked first.</summary>
public sealed record InstallPawnIoOp : HelperOp;

/// <summary>Health › Crashes: copies one minidump out of C:\Windows\Minidump (which only administrators can read) so the debugger,
/// running as you, can analyze it. FileName is a bare Windows minidump name ("091126-13359-01.dmp"); the copy lands in
/// ProgramData\CarsControl\dumps, readable by you and administrators only. Data: the copy's path.</summary>
public sealed record CopyCrashDumpOp(string FileName) : HelperOp;

/// <summary>Storage › Games: after an Epic game was copied to another drive and the launcher switched to the copy, removes the old
/// folder (Epic installs into Program Files, which only administrators can delete from). The helper re-checks everything: the old
/// folders come from this manifest and its pre-move backup in Epic's fixed manifest directory; the launcher points at the new
/// folder, nothing points at the old one, and every file matches. ManifestId is a bare identifier, never a path.</summary>
public sealed record RemoveMovedGameOp(string ManifestId) : HelperOp;

/// <summary>Request frame: app → helper.</summary>
public sealed record HelperRequest(int Id, HelperOp Op, bool DryRun = false);

/// <summary>Response frames: helper → app. Zero or more "line"/"progress" frames, then one "done".</summary>
public sealed record HelperEvent(int Id, string Kind, string? Text = null, double? Percent = null, OpResult? Result = null)
{
    public const string Line = "line";
    public const string Progress = "progress";
    public const string Done = "done";
}

public enum OpStatus { Succeeded, Failed, Rejected, NotFound, Cancelled, DryRun }

public sealed record OpResult(
    OpStatus Status,
    int ExitCode = 0,
    string? Message = null,
    bool RebootRequired = false,
    string? LogPath = null,
    string? Data = null)
{
    public bool Ok => Status is OpStatus.Succeeded or OpStatus.DryRun;

    public static OpResult Success(string? message = null, string? data = null) => new(OpStatus.Succeeded, 0, message, Data: data);
    public static OpResult Reject(string why) => new(OpStatus.Rejected, -1, why);
}

/// <summary>Leftover candidate returned by a scan. Path is informational only; deletion is by Token.</summary>
public sealed record LeftoverItem(string Token, string Kind, string Path, long SizeBytes, string Confidence, string Reason);

public sealed record HelperHello(string Version, string User, bool Elevated, int ProcessId);

public static class HelperJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Pipe names are generated by the app as CarsControl.{32 hex}; the helper rejects anything else.</summary>
    public static bool IsValidPipeName(string name) =>
        name.Length == "CarsControl.".Length + 32 &&
        name.StartsWith("CarsControl.", StringComparison.Ordinal) &&
        name["CarsControl.".Length..].All(Uri.IsHexDigit);
}
