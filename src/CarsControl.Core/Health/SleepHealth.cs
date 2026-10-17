namespace CarsControl.Core.Health;

/// <summary>One sleep or hibernate and the resume that ended it.</summary>
public sealed record SleepCycle(
    DateTime SleptAt,
    DateTime WokeAt,
    string State,
    string Why,
    TimeSpan Asleep,
    TimeSpan FirmwareInit,
    TimeSpan DriverInit,
    TimeSpan HibernateRead,
    string? WokenBy)
{
    /// <summary>From pressing the key or mouse to a usable desktop: firmware + drivers + reading the hibernate file.</summary>
    public TimeSpan ResumeTime => FirmwareInit + DriverInit + HibernateRead;

    public bool IsSlow => ResumeTime >= TimeSpan.FromSeconds(15);
}

/// <summary>A moment the whole system stopped: its clock stopped ticking, then caught up with the hardware clock.</summary>
public sealed record Stall(DateTime At, TimeSpan Length);

/// <summary>A driver that refused to let Windows sleep ("The driver \Driver\vpcivsp … stopped the power transition").</summary>
public sealed record PowerBlock(DateTime At, string Driver, string Device);

public sealed record SleepReport(IReadOnlyList<SleepCycle> Cycles, IReadOnlyList<Stall> Stalls, IReadOnlyList<PowerBlock> Blocks)
{
    public int SlowResumes => Cycles.Count(c => c.IsSlow);

    /// <summary>The slowest piece of the slowest resume, in words ("firmware took 26 s").</summary>
    public string? Headline
    {
        get
        {
            var worst = Cycles.OrderByDescending(c => c.ResumeTime).FirstOrDefault();
            if (worst is null || !worst.IsSlow) return null;
            var parts = new[] { ("firmware start-up", worst.FirmwareInit), ("drivers", worst.DriverInit), ("reading the hibernate file", worst.HibernateRead) }.OrderByDescending(p => p.Item2).First();
            return $"The slowest resume took {worst.ResumeTime.TotalSeconds:0} s; most of it was {parts.Item1} ({parts.Item2.TotalSeconds:0} s).";
        }
    }
}

/// <summary>
/// Sleep, hibernate and "it froze" history from the System log, no admin needed:
/// <list type="bullet">
/// <item>Power-Troubleshooter 1: every resume, with how long firmware, drivers and the hibernate file took.</item>
/// <item>Kernel-Power 42: why the PC went to sleep (power button, an app, the timer).</item>
/// <item>Kernel-Power 40: a driver that blocked a sleep.</item>
/// <item>Kernel-General 1: the system clock jumped. When it was re-synchronised with the hardware clock after
///   several seconds, nothing was running during that time: that's a freeze, measured exactly and after the fact.</item>
/// </list>
/// </summary>
public static class SleepHealth
{
    private const string Troubleshooter = "Microsoft-Windows-Power-Troubleshooter";
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";

    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);

    public static async Task<SleepReport> ReadAsync(TimeSpan lookBack, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - lookBack;
        var query = WinEvents.Query(since, (Troubleshooter, 1), (KernelPower, 42), (KernelPower, 40), (KernelGeneral, 1));
        return Build(await WinEvents.ReadAsync("System", query, 2000, ct));
    }

    public static SleepReport Build(IReadOnlyList<WinEvent> events)
    {
        var sleeps = events.Where(e => e.Provider == KernelPower && e.Id == 42).ToList();
        var cycles = new List<SleepCycle>();
        foreach (var r in events.Where(e => e.Provider == Troubleshooter && e.Id == 1))
        {
            var slept = r.GetTime("SleepTime");
            var woke = r.GetTime("WakeTime");
            if (slept is null || woke is null) continue;
            // The matching "entering sleep" event is logged within a second or two of SleepTime.
            var why = sleeps.FirstOrDefault(s => Math.Abs((s.TimeUtc - slept.Value).TotalSeconds) < 10);
            cycles.Add(new SleepCycle(
                slept.Value.ToLocalTime(), woke.Value.ToLocalTime(),
                StateName(r.GetLong("EffectiveState") ?? r.GetLong("TargetState")),
                why is null ? "unknown" : ReasonName(why.GetLong("Reason")),
                woke.Value - slept.Value,
                Ms(r.GetLong("BiosInitDuration")), Ms(r.GetLong("DriverInitDuration")), Ms(r.GetLong("HiberReadDuration")),
                WakeSource(r)));
        }

        var stalls = new List<Stall>();
        foreach (var t in events.Where(e => e.Provider == KernelGeneral && e.Id == 1))
        {
            var delta = DeltaOf(t);
            // Reason 2 = "synchronised with the hardware clock": the system clock had stopped. Negative deltas are corrections.
            if (t.GetLong("Reason") == 2 && delta >= StallThreshold) stalls.Add(new Stall(t.Local, delta));
        }

        var blocks = new List<PowerBlock>();
        foreach (var b in events.Where(e => e.Provider == KernelPower && e.Id == 40))
        {
            var driver = (b.Get("DriverName") ?? "").Replace(@"\Driver\", "", StringComparison.OrdinalIgnoreCase);
            blocks.Add(new PowerBlock(b.Local, driver.Length > 0 ? driver : "a driver", b.Get("InstanceName") ?? ""));
        }

        return new SleepReport(cycles.OrderByDescending(c => c.SleptAt).ToList(), stalls.OrderByDescending(s => s.At).ToList(), blocks.OrderByDescending(b => b.At).ToList());
    }

    private static TimeSpan DeltaOf(WinEvent e)
    {
        if (e.GetLong("TimeDeltaInMs") is { } ms && ms != 0) return TimeSpan.FromMilliseconds(ms);
        return e.GetTime("NewTime") is { } n && e.GetTime("OldTime") is { } o ? n - o : TimeSpan.Zero;
    }

    private static TimeSpan Ms(long? ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms ?? 0));

    /// <summary>The system power states Windows reports (PowerSystemSleeping1…Hibernate).</summary>
    public static string StateName(long? state) => state switch
    {
        2 => "Sleep (S1)",
        3 => "Sleep (S2)",
        4 => "Sleep (S3)",
        5 => "Hibernate",
        _ => "Sleep",
    };

    /// <summary>Kernel-Power 42's Reason: why the PC was told to sleep. Only the codes seen and checked against
    /// Windows' own message text are named (0 "Button or Lid", 4 "Application API"); any other shows its number
    /// rather than a guess.</summary>
    public static string ReasonName(long? reason) => reason switch
    {
        0 => "the power button or lid",
        4 => "an app or Windows (Sleep in the Start menu, a script)",
        { } n => $"another reason (code {n})",
        _ => "an unknown reason",
    };

    private static string? WakeSource(WinEvent r) =>
        r.Get("WakeSourceText") is { Length: > 0 } text ? text
        : r.GetLong("WakeSourceType") is { } t and > 0 ? t switch { 1 => "the power button", 2 => "a device or network packet", 3 => "an RTC timer", _ => "another source" }
        : null;
}
