using System.Globalization;
using CarsControl.Core.Settings;
using CarsControl.Core.Util;

namespace CarsControl.Core.Maintenance;

/// <summary>What the last maintenance run did, and which scheduled times are already dealt with.</summary>
public sealed class MaintenanceState
{
    /// <summary>Scheduled times up to here have run (or were skipped on purpose, e.g. when the schedule
    /// was changed). A scheduled time after it is due.</summary>
    public DateTime? CoveredUntil { get; set; }

    public DateTime? LastRun { get; set; }
    public string? LastOrigin { get; set; }
    public int Updated { get; set; }
    public int Failed { get; set; }
    public long FreedBytes { get; set; }
    public int Waiting { get; set; }

    /// <summary>A run came due during a game and waits until after it.</summary>
    public bool PostponedForGame { get; set; }
}

/// <summary>
/// When maintenance runs. Times are local wall-clock times ("Sunday 03:00" stays 03:00 across daylight
/// saving changes). A run the PC missed (off or asleep) is caught up once at the next chance, never
/// repeated for every missed week, and never started during a game or while you're using the PC.
/// </summary>
public static class MaintenanceSchedule
{
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        return text is not null
               && DateTime.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
               && (time = t.TimeOfDay) >= TimeSpan.Zero;
    }

    private static TimeSpan TimeOf(MaintenanceSettings s) => TryParseTime(s.Time, out var t) ? t : new TimeSpan(3, 0, 0);

    /// <summary>The most recent scheduled time at or before <paramref name="now"/> (within the last week).</summary>
    public static DateTime? Latest(MaintenanceSettings s, DateTime now)
    {
        if (s.Days.Count == 0) return null;
        var time = TimeOf(s);
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(-d);
            if (!s.Days.Contains(day.DayOfWeek)) continue;
            var at = day + time;
            if (at <= now) return at;
        }
        return null;
    }

    /// <summary>The next scheduled time after <paramref name="now"/>.</summary>
    public static DateTime? Next(MaintenanceSettings s, DateTime now)
    {
        if (!s.Enabled || s.Days.Count == 0) return null;
        var time = TimeOf(s);
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            if (!s.Days.Contains(day.DayOfWeek)) continue;
            var at = day + time;
            if (at > now) return at;
        }
        return null;
    }

    /// <summary>The scheduled time that should run now (on time or missed), or null.</summary>
    public static DateTime? Due(MaintenanceSettings s, MaintenanceState state, DateTime now)
    {
        if (!s.Enabled || Latest(s, now) is not { } latest) return null;
        return state.CoveredUntil is { } covered && covered >= latest ? null : latest;
    }

    /// <summary>A due run starts only when no game is running and you've been idle long enough.</summary>
    public static bool MayStart(MaintenanceSettings s, TimeSpan idle, bool gaming) =>
        !gaming && idle >= TimeSpan.FromMinutes(Math.Clamp(s.IdleMinutes, 0, 120));

    /// <summary>"5 updated, 2.1 GB cleaned, 3 waiting for approval". No app names or paths (it's also
    /// what a phone alert says).</summary>
    public static string Describe(int updated, int failed, long freedBytes, int waiting)
    {
        var parts = new List<string> { $"{updated} updated" };
        if (failed > 0) parts.Add($"{failed} failed");
        parts.Add(freedBytes > 0 ? $"{ByteSize.Format(freedBytes)} cleaned" : "nothing to clean");
        if (waiting > 0) parts.Add($"{waiting} waiting for approval");
        return string.Join(", ", parts);
    }

    public static string Describe(MaintenanceState s) => Describe(s.Updated, s.Failed, s.FreedBytes, s.Waiting);

    /// <summary>"Sun 03:00", or "Sun 19 Oct 03:00" when it's more than a week away.</summary>
    public static string When(DateTime at, DateTime now) =>
        Math.Abs((at - now).TotalDays) < 6.5 ? at.ToString("ddd HH:mm", CultureInfo.CurrentCulture) : at.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
}
