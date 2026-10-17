using CarsControl.Core.Maintenance;
using CarsControl.Core.Settings;

namespace CarsControl.Tests;

public class MaintenanceScheduleTests
{
    // 2026-10-11 is a Sunday.
    private static readonly DateTime Sunday = new(2026, 10, 11);

    private static MaintenanceSettings Weekly(string time = "03:00", params DayOfWeek[] days) =>
        new() { Enabled = true, Time = time, Days = days.Length == 0 ? [DayOfWeek.Sunday] : [.. days] };

    [Fact]
    public void Next_run_is_the_coming_scheduled_time()
    {
        var s = Weekly();
        Assert.Equal(Sunday.AddHours(3), MaintenanceSchedule.Next(s, Sunday.AddHours(1)));
        Assert.Equal(Sunday.AddDays(7).AddHours(3), MaintenanceSchedule.Next(s, Sunday.AddHours(3)));
        Assert.Equal(Sunday.AddDays(7).AddHours(3), MaintenanceSchedule.Next(s, Sunday.AddDays(2)));
    }

    [Fact]
    public void Several_days_pick_the_nearest()
    {
        var s = Weekly("22:30", DayOfWeek.Wednesday, DayOfWeek.Saturday);
        Assert.Equal(Sunday.AddDays(3).Add(new TimeSpan(22, 30, 0)), MaintenanceSchedule.Next(s, Sunday)); // Wednesday
        Assert.Equal(Sunday.AddDays(6).Add(new TimeSpan(22, 30, 0)), MaintenanceSchedule.Next(s, Sunday.AddDays(4))); // Saturday
    }

    [Fact]
    public void Nothing_is_due_before_the_time_or_when_off()
    {
        var state = new MaintenanceState { CoveredUntil = Sunday.AddDays(-1) };
        Assert.Null(MaintenanceSchedule.Due(Weekly(), state, Sunday.AddHours(2)));
        var off = Weekly();
        off.Enabled = false;
        Assert.Null(MaintenanceSchedule.Due(off, state, Sunday.AddHours(4)));
        Assert.Null(MaintenanceSchedule.Next(off, Sunday));
    }

    [Fact]
    public void A_run_is_due_at_its_time_and_not_again_after_it_ran()
    {
        var s = Weekly();
        var state = new MaintenanceState { CoveredUntil = Sunday.AddDays(-3) };
        Assert.Equal(Sunday.AddHours(3), MaintenanceSchedule.Due(s, state, Sunday.AddHours(3)));
        state.CoveredUntil = Sunday.AddHours(3).AddMinutes(20); // ran
        Assert.Null(MaintenanceSchedule.Due(s, state, Sunday.AddHours(9)));
    }

    [Fact]
    public void A_missed_run_catches_up_once_not_once_per_missed_week()
    {
        var s = Weekly();
        // PC off for three weeks; it's Tuesday now.
        var state = new MaintenanceState { CoveredUntil = Sunday.AddDays(-21) };
        var now = Sunday.AddDays(2).AddHours(18);
        Assert.Equal(Sunday.AddHours(3), MaintenanceSchedule.Due(s, state, now)); // only the latest one
        state.CoveredUntil = now;
        Assert.Null(MaintenanceSchedule.Due(s, state, now.AddHours(1)));
        Assert.Equal(Sunday.AddDays(7).AddHours(3), MaintenanceSchedule.Next(s, now));
    }

    [Fact]
    public void Changing_the_schedule_does_not_count_the_moved_time_as_missed()
    {
        // The service sets CoveredUntil = now on a schedule change; a time that moved behind you isn't due,
        // a time just ahead of you is.
        var now = Sunday.AddHours(15);
        var state = new MaintenanceState { CoveredUntil = now };
        Assert.Null(MaintenanceSchedule.Due(Weekly("14:00"), state, now.AddMinutes(1)));
        Assert.Equal(Sunday.AddHours(15).AddMinutes(5), MaintenanceSchedule.Due(Weekly("15:05"), state, now.AddMinutes(6)));
    }

    [Fact]
    public void Wall_clock_time_survives_daylight_saving()
    {
        // US DST ends 2026-11-01 (a Sunday) and starts 2027-03-14: 03:00 stays 03:00 local both times.
        var s = Weekly();
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0), MaintenanceSchedule.Next(s, new DateTime(2026, 10, 31, 12, 0, 0)));
        Assert.Equal(new DateTime(2027, 3, 14, 3, 0, 0), MaintenanceSchedule.Next(s, new DateTime(2027, 3, 13, 12, 0, 0)));
        // 02:30 doesn't exist on the spring-forward night; it's still due once that morning.
        var early = Weekly("02:30");
        var state = new MaintenanceState { CoveredUntil = new DateTime(2027, 3, 13) };
        Assert.Equal(new DateTime(2027, 3, 14, 2, 30, 0), MaintenanceSchedule.Due(early, state, new DateTime(2027, 3, 14, 4, 0, 0)));
    }

    [Fact]
    public void Never_starts_during_a_game_or_while_you_use_the_PC()
    {
        var s = Weekly();
        Assert.False(MaintenanceSchedule.MayStart(s, TimeSpan.FromHours(1), gaming: true));
        Assert.False(MaintenanceSchedule.MayStart(s, TimeSpan.FromMinutes(2), gaming: false));
        Assert.True(MaintenanceSchedule.MayStart(s, TimeSpan.FromMinutes(5), gaming: false));
    }

    [Fact]
    public void Times_parse_strictly()
    {
        Assert.True(MaintenanceSchedule.TryParseTime("03:00", out var t));
        Assert.Equal(new TimeSpan(3, 0, 0), t);
        Assert.True(MaintenanceSchedule.TryParseTime("7:45", out t));
        Assert.Equal(new TimeSpan(7, 45, 0), t);
        Assert.False(MaintenanceSchedule.TryParseTime("25:00", out _));
        Assert.False(MaintenanceSchedule.TryParseTime("3am", out _));
        Assert.False(MaintenanceSchedule.TryParseTime(null, out _));
        // A broken time in settings.json falls back to 03:00 rather than never running.
        Assert.Equal(Sunday.AddHours(3), MaintenanceSchedule.Next(Weekly("nonsense"), Sunday));
    }

    [Fact]
    public void Summary_has_counts_only()
    {
        Assert.Equal("5 updated, 2.1 GB cleaned, 3 waiting for approval", MaintenanceSchedule.Describe(5, 0, 2_254_857_830, 3));
        Assert.Equal("0 updated, 1 failed, nothing to clean", MaintenanceSchedule.Describe(0, 1, 0, 0));
    }

    [Fact]
    public void Settings_file_writes_days_as_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cars-maint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"), _ => null);
            store.Update(x => x.Maintenance.Days = [DayOfWeek.Sunday, DayOfWeek.Wednesday]);
            var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.Contains("\"Wednesday\"", json);
            var again = new SettingsStore(Path.Combine(dir, "settings.json"), _ => null).Current;
            Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Wednesday], again.Maintenance.Days);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
