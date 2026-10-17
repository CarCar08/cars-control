using System.Globalization;
using CarsControl.Core.Util;

namespace CarsControl.Core.Alerts;

/// <summary>One phone alert. <see cref="Critical"/> ones go through quiet hours.</summary>
public sealed record Alert(string Kind, string Title, string Message, int Priority = 3, IReadOnlyList<string>? Tags = null, bool Critical = false);

/// <summary>
/// Every phone alert C.A.R.S. can send. They're short and carry no personal details: counts, drive
/// letters and times only. Never paths, app or file names, account names or error text, because alerts
/// travel through a public server where anyone who knows the topic could read them.
/// </summary>
public static class AlertMessages
{
    public const string AppName = "C.A.R.S.";

    private static string Prefix(string? label) => string.IsNullOrWhiteSpace(label) ? AppName : $"{AppName} · {label.Trim()}";

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static Alert Test(string? label) =>
        new("test", $"{Prefix(label)}: test alert", "If you can read this, phone alerts work.", 3, ["white_check_mark"]);

    public static Alert MaintenanceSummary(string? label, int updated, int failed, long freedBytes, int waiting)
    {
        var text = $"{updated} updated, " + (freedBytes > 0 ? $"{ByteSize.Format(freedBytes)} cleaned" : "nothing to clean")
                   + (waiting > 0 ? $", {waiting} waiting for your approval" : "") + ".";
        return failed > 0
            ? new("maintenance-failed", $"{Prefix(label)}: maintenance had problems", $"{Plural(failed, "item", "items")} failed. {text} Details are in History on your PC.", 4, ["warning"])
            : new("maintenance", $"{Prefix(label)}: maintenance finished", text, waiting > 0 ? 3 : 2, ["broom"]);
    }

    public static Alert ApprovalsWaiting(string? label, int count) =>
        new("approval", $"{Prefix(label)}: {Plural(count, "admin item", "admin items")} waiting",
            "Approve them on your PC (one prompt runs them all).", 3, ["lock"]);

    public static Alert LowDisk(string? label, char drive, long freeBytes, double freePercent) =>
        new("low-disk", $"{Prefix(label)}: drive {drive}: is low on space",
            $"{ByteSize.Format(freeBytes)} free ({freePercent.ToString("0", CultureInfo.InvariantCulture)}%).",
            freeBytes < 2L * 1024 * 1024 * 1024 ? 5 : 4, ["floppy_disk"], Critical: freeBytes < 2L * 1024 * 1024 * 1024);

    public static Alert DriveFillingFast(string? label, char drive, long lostBytes) =>
        new("filling-fast", $"{Prefix(label)}: drive {drive}: is filling fast",
            $"{ByteSize.Format(lostBytes)} used up in the last 24 hours.", 3, ["chart_with_upwards_trend"]);

    public static Alert BlueScreen(string? label, DateTime when) =>
        new("bluescreen", $"{Prefix(label)}: your PC had a blue screen",
            $"It restarted at {when.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}. The crash details are kept on the PC.", 4, ["rotating_light"]);

    public static Alert UnexpectedShutdown(string? label, DateTime when) =>
        new("shutdown", $"{Prefix(label)}: your PC shut down unexpectedly",
            $"It started again at {when.ToString("ddd HH:mm", CultureInfo.InvariantCulture)} (after a freeze, a power cut or a held power button).", 4, ["zap"]);

    public static Alert DriveHealth(string? label, char drive, string status) =>
        new("drive-health", $"{Prefix(label)}: drive {drive}: reports a health problem",
            $"Status: {Clean(status)}. Back up what matters on it.", 5, ["rotating_light"], Critical: true);

    /// <summary><paramref name="what"/> is one of "processor" or "graphics card": fixed words, never device text.</summary>
    public static Alert Temperature(string? label, string what, int degrees, int limit) =>
        new("temperature", $"{Prefix(label)}: the {what} is running hot",
            $"It reached {degrees.ToString(CultureInfo.InvariantCulture)} °C (your limit is {limit.ToString(CultureInfo.InvariantCulture)} °C). Check the fans and the airflow in the case.", 4, ["fire"]);

    public static Alert BackupProblem(string? label) =>
        new("backup-failed", $"{Prefix(label)}: a backup didn't finish cleanly",
            "Open Backup & sync on your PC to see which one and why.", 4, ["floppy_disk"]);

    public static Alert WeeklyDigest(string? label, int updates, int days) =>
        new("digest", $"{Prefix(label)}: {Plural(updates, "update", "updates")} waiting",
            $"Nothing has been updated for {days} days.", 2, ["calendar"]);

    /// <summary>Health status words only ("Warning", "Unhealthy"): never pass through device or vendor text.</summary>
    private static string Clean(string status)
    {
        var s = new string(status.Where(c => char.IsLetter(c) || c == ' ').ToArray()).Trim();
        return s.Length is 0 or > 24 ? "unknown" : s;
    }
}
