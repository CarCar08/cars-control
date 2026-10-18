namespace CarsControl.Core.Help;

/// <summary>What kind of PC this is. Auto = Laptop when there's a battery, otherwise Desktop.</summary>
public enum PcType { Auto, Desktop, Laptop, GamingFirst }

/// <summary>A recommended value for one switch, and why, in a sentence.</summary>
public sealed record Recommendation(string Id, string Title, bool On, string Why);

/// <summary>
/// The recommended setting for each switch, per kind of PC. Ids are the setting's name (for Settings switches, the view
/// model property it binds to), "privacy:{tweak id}", "cleanup:{rule id}" and "optimize:{id}" (Optimization page). A switch
/// without an entry has no recommendation: it's a matter of taste or, for "Test it" optimizations, it depends on the PC.
/// </summary>
public static class Recommendations
{
    public static PcType Resolve(PcType chosen, bool hasBattery) => chosen == PcType.Auto ? (hasBattery ? PcType.Laptop : PcType.Desktop) : chosen;

    public static string Describe(PcType type) => type switch
    {
        PcType.Laptop => "a laptop",
        PcType.GamingFirst => "a gaming PC",
        _ => "a desktop PC",
    };

    private sealed record Rule(string Title, bool? Desktop, bool? Laptop, bool? Gaming, string Why, string? LaptopWhy = null);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaintenanceEnabled"] = new("Scheduled maintenance", true, true, true,
            "Keeps apps and Windows current without you thinking about it. It only runs while you're away and never during a game."),
        ["MaintenanceUpdates"] = new("Install updates during maintenance", true, true, true, "Most security problems come from apps that weren't updated."),
        ["MaintenanceCleanupOn"] = new("Clean up during maintenance", true, true, true, "Temporary files grow back; clearing them weekly keeps drives from filling up."),
        ["GamingAutomatic"] = new("Automatic Gaming mode", true, true, true, "Nothing in the background competes with your game, and you don't have to remember to switch it on."),
        ["GamingPauseWindowsUpdate"] = new("Pause Windows Update while playing", true, true, true, "Stops downloads and installs from eating bandwidth and disk mid-game; it resumes right after."),
        ["GamingPauseSearch"] = new("Pause search indexing while playing", true, true, true, "Indexing reads the disk in bursts that can cause stutter; it catches up afterwards."),
        ["GamingFreeStandby"] = new("Free standby memory when a game starts", null, null, true,
            "Helps games that stutter when memory is full. On PCs with plenty of RAM it makes little difference either way."),
        ["GamingMaintenanceAfter"] = new("Run postponed maintenance after playing", true, true, true, "Maintenance skipped during a game still happens, once you've left the PC."),
        ["GamingApproveAtSignIn"] = new("Ask for admin once at sign-in", true, null, true,
            "One approval at sign-in means Gaming mode can pause Windows Update without ever interrupting a game.",
            "On a laptop you rarely game on, a prompt at every sign-in isn't worth it."),
        ["RunInBackground"] = new("Keep running in the tray", true, true, true, "Maintenance, alerts, Gaming mode and the health watch only work while C.A.R.S. runs; it costs almost nothing."),
        ["StartWithWindows"] = new("Start with Windows", true, true, true, "So maintenance, Gaming mode and alerts work without you opening the app."),
        ["RestartAway"] = new("Restart when I'm away", true, false, true,
            "Finishes update restarts at night instead of interrupting you during the day.",
            "Laptops usually sleep at night instead of staying on, so there's no quiet moment to restart in."),
        ["HealthTempAlerts"] = new("Temperature alerts", true, true, true, "A processor or graphics card that stays too hot usually means dust or a failing fan; you'll want to know."),
        ["HealthBoardSensors"] = new("Read motherboard sensors", true, null, true, "Shows CPU temperature and fan speeds on the Sensors tab once PawnIO is installed."),
        ["HealthRestorePoints"] = new("Restore points before risky changes", true, true, true, "A way back if a driver or update goes wrong. It costs a little disk space."),
        ["UpdatesAuto"] = new("Look for C.A.R.S. updates", true, true, true, "Fixes reach you without checking yourself; nothing installs until you press Update now."),
        ["UpdatesPre"] = new("Offer pre-releases", false, false, false, "Release candidates are for testing; stick to finished releases unless you want to help test."),
        ["AlertDriveHealth"] = new("Alert on drive health problems", true, true, true, "A failing drive gives only a little warning; this is the one alert worth having even at night."),
        ["AlertCrashes"] = new("Alert on blue screens", true, true, true, "Crashes while you're away are otherwise easy to miss."),
        ["AlertFailures"] = new("Alert on failures", true, true, true, "So a failed update or cleanup doesn't go unnoticed."),

        // ---------- Optimization page (1.1). "Test it" switches never get one: whether they help depends on the PC. ----------
        ["optimize:game-mode"] = new("Game Mode", true, true, true, "Windows' default: it keeps driver installs and restart prompts out of your games."),
        ["optimize:background-recording-off"] = new("No background game recording", null, true, true,
            "Recording every game all the time is wasted work if you don't save clips; Win+Alt+R still records on demand.",
            "Constant recording costs battery for clips you may never save."),
        ["optimize:mouse-acceleration-off"] = new("No mouse acceleration", null, null, true, "The same hand movement always moves the same distance, which makes aiming consistent."),
        ["optimize:fast-startup-off"] = new("Full shutdown", true, null, true,
            "Updates and drivers start from a clean state after every shutdown; on an SSD a full start takes only a few seconds longer."),
        ["optimize:delivery-optimization-local"] = new("Share updates only on my network", true, true, true,
            "Your upload bandwidth isn't used to send updates to strangers; downloads are as fast as before."),
        ["optimize:hibernation-off"] = new("Turn off hibernation", true, false, true,
            "Desktops rarely hibernate; turning it off frees the hibernation file (about 40% of your RAM) on the system drive.",
            "Laptops hibernate to save your work when the battery runs low; keep it."),
    };

    /// <summary>The recommendation for one switch on this kind of PC, or null when there isn't one.</summary>
    public static Recommendation? For(string id, PcType type)
    {
        if (id.StartsWith("privacy:", StringComparison.OrdinalIgnoreCase)) return null; // see ForPrivacy
        if (!Rules.TryGetValue(id, out var r)) return null;
        var value = type switch { PcType.Laptop => r.Laptop, PcType.GamingFirst => r.Gaming, _ => r.Desktop };
        if (value is null) return null;
        var why = type == PcType.Laptop && r.LaptopWhy is not null ? r.LaptopWhy : r.Why;
        return new Recommendation(id, r.Title, value.Value, why);
    }

    /// <summary>Privacy switches: on is recommended, except the Taskbar group (taste) and switches with a caution on this PC.</summary>
    public static Recommendation? ForPrivacy(string tweakId, string title, bool isTaskbar, bool hasCaution) =>
        isTaskbar || hasCaution ? null : new Recommendation("privacy:" + tweakId, title, true, "Stops data collection or ads without affecting how Windows works.");

    /// <summary>Cleanup categories: the ones ticked by default are the recommended ones.</summary>
    public static Recommendation ForCleanup(string ruleId, string title, bool defaultOn) =>
        new("cleanup:" + ruleId, title, defaultOn, defaultOn ? "Safe to clear: it's rebuilt automatically when needed." : "Left off: clearing it has a cost (slower first launches, or losing something you might want).");

    /// <summary>Every switch with a recommendation for this kind of PC (for "Apply recommended").</summary>
    public static IReadOnlyList<Recommendation> AllFor(PcType type) =>
        Rules.Keys.Where(k => !k.StartsWith("optimize:", StringComparison.OrdinalIgnoreCase)).Select(k => For(k, type)).OfType<Recommendation>().ToList();

    /// <summary>The Optimization page's recommendations for this kind of PC ("optimize:{id}").</summary>
    public static IReadOnlyList<Recommendation> OptimizationsFor(PcType type) =>
        Rules.Keys.Where(k => k.StartsWith("optimize:", StringComparison.OrdinalIgnoreCase)).Select(k => For(k, type)).OfType<Recommendation>().ToList();
}
