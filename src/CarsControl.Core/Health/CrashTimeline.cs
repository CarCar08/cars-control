using System.Globalization;

namespace CarsControl.Core.Health;

/// <summary>The PC stopped without shutting down (Kernel-Power 41 with no bug check): a power cut, a hard reset, a hang you had to power off.</summary>
public sealed record UnexpectedShutdown(DateTime At);

public sealed record CrashTimeline(IReadOnlyList<BlueScreen> BlueScreens, IReadOnlyList<UnexpectedShutdown> Shutdowns)
{
    public static CrashTimeline Empty { get; } = new([], []);
}

/// <summary>An app's crashes and hangs from the Application log, grouped by program.</summary>
public sealed record AppCrashGroup(string App, int Crashes, int Hangs, DateTime Last, string? Module, string? Reason)
{
    public int Total => Crashes + Hangs;
}

/// <summary>
/// Blue screens and unexpected shutdowns from the System log (any account can read it):
/// WER-SystemErrorReporting 1001 has the stop code, its parameters and the dump file; Kernel-Power 41 says the
/// PC restarted without a clean shutdown (with the bug check code when it was a blue screen).
/// </summary>
public static class CrashLog
{
    private const string Wer = "Microsoft-Windows-WER-SystemErrorReporting";
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";

    public static async Task<CrashTimeline> ReadAsync(TimeSpan lookBack, CancellationToken ct = default)
    {
        var query = WinEvents.Query(DateTime.UtcNow - lookBack, (Wer, 1001), (KernelPower, 41));
        return Build(await WinEvents.ReadAsync("System", query, 400, ct));
    }

    public static CrashTimeline Build(IReadOnlyList<WinEvent> events)
    {
        var screens = new List<BlueScreen>();
        foreach (var e in events.Where(x => x.Provider == Wer && x.Id == 1001))
        {
            if (Parse(e.Get("param1")) is not { } stop) continue;
            var dump = e.Get("param2");
            screens.Add(new BlueScreen(e.Local, stop.Code, BugChecks.NameOf(stop.Code), stop.Parameters, dump is { Length: > 0 } && dump.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ? dump : null, BugChecks.MeaningOf(stop.Code)));
        }

        var shutdowns = new List<UnexpectedShutdown>();
        foreach (var e in events.Where(x => x.Provider == KernelPower && x.Id == 41).OrderBy(x => x.TimeUtc))
        {
            var code = e.GetLong("BugcheckCode") ?? 0;
            // The restart logs both; a 41 with a bug check code that the 1001 already covers is the same incident.
            if (code != 0)
            {
                if (!screens.Any(s => Math.Abs((s.At - e.Local).TotalMinutes) < 10))
                    screens.Add(new BlueScreen(e.Local, (uint)code, BugChecks.NameOf((uint)code), ParametersOf(e), null, BugChecks.MeaningOf((uint)code)));
            }
            else if (!screens.Any(s => Math.Abs((s.At - e.Local).TotalMinutes) < 10))
            {
                shutdowns.Add(new UnexpectedShutdown(e.Local));
            }
        }
        return new CrashTimeline(screens.OrderByDescending(s => s.At).ToList(), shutdowns.OrderByDescending(s => s.At).ToList());
    }

    /// <summary>
    /// The bug check report names the full memory dump (C:\Windows\MEMORY.DMP, administrators only) when that's
    /// what Windows is set to save; the small minidump written at the same moment can be read by anyone and is
    /// what the debugger needs. Give each blue screen the minidump written within 15 minutes of it.
    /// </summary>
    public static IReadOnlyList<BlueScreen> AttachMinidumps(IReadOnlyList<BlueScreen> screens, IReadOnlyList<DumpFile> dumps) =>
        screens.Select(b =>
        {
            if (b.DumpPath is { } p && p.Contains("Minidump", StringComparison.OrdinalIgnoreCase)) return b;
            var near = dumps.Where(d => Math.Abs((d.Written - b.At).TotalMinutes) <= 15).OrderBy(d => Math.Abs((d.Written - b.At).TotalSeconds)).FirstOrDefault();
            return near is null ? b : b with { DumpPath = near.Path };
        }).ToList();

    private static IReadOnlyList<string> ParametersOf(WinEvent e) =>
        Enumerable.Range(1, 4).Select(i => e.Get("BugcheckParameter" + i)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();

    /// <summary>"0x0000003b (0x00000000c0000005, 0xfffff8056e16ca26, …)".</summary>
    private static (uint Code, IReadOnlyList<string> Parameters)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var open = text.IndexOf('(');
        var head = (open < 0 ? text : text[..open]).Trim();
        if (head.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) head = head[2..];
        if (!uint.TryParse(head, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return null;
        var close = text.IndexOf(')');
        var ps = open >= 0 && close > open ? text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
        return (code, ps);
    }
}

public static class AppCrashes
{
    private const string AppError = "Application Error";
    private const string AppHang = "Application Hang";

    public static async Task<IReadOnlyList<AppCrashGroup>> ReadAsync(TimeSpan lookBack, CancellationToken ct = default)
    {
        var query = WinEvents.Query(DateTime.UtcNow - lookBack, (AppError, 1000), (AppHang, 1002));
        return Group(await WinEvents.ReadAsync("Application", query, 600, ct));
    }

    public static IReadOnlyList<AppCrashGroup> Group(IReadOnlyList<WinEvent> events)
    {
        var rows = new List<(string App, bool Hang, DateTime At, string? Module, string? Code)>();
        foreach (var e in events)
        {
            if (e.Provider == AppError && e.Id == 1000)
                rows.Add((Clean(e.Get("Data0")), false, e.Local, Module(e.Get("Data3")), e.Get("Data6")));
            else if (e.Provider == AppHang && e.Id == 1002)
                rows.Add((Clean(e.Get("Data0")), true, e.Local, null, null));
        }
        return rows.Where(r => r.App.Length > 0).GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var crashes = g.Where(r => !r.Hang).ToList();
            var module = crashes.GroupBy(r => r.Module).Where(m => m.Key is not null).OrderByDescending(m => m.Count()).FirstOrDefault()?.Key;
            var code = crashes.GroupBy(r => r.Code).Where(c => c.Key is not null).OrderByDescending(c => c.Count()).FirstOrDefault()?.Key;
            return new AppCrashGroup(g.Key, crashes.Count, g.Count() - crashes.Count, g.Max(r => r.At), module, code is null ? null : ExceptionName(code));
        }).OrderByDescending(a => a.Total).ThenByDescending(a => a.Last).ToList();
    }

    private static string Clean(string? s) => (s ?? "").Trim();

    private static string? Module(string? m) => string.IsNullOrWhiteSpace(m) || m.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : m.Trim();

    public static string ExceptionName(string code) => code.Trim().ToLowerInvariant() switch
    {
        "0xc0000005" => "memory access violation",
        "0xc0000409" => "stack buffer overrun (a safety check failed)",
        "0xc000001d" => "illegal instruction",
        "0xc0000094" => "divide by zero",
        "0xc00000fd" => "stack overflow",
        "0xc0000374" => "heap corruption",
        "0xe06d7363" => "a C++ exception nobody caught",
        "0xe0434352" => "a .NET exception nobody caught",
        "0x80000003" => "a breakpoint was hit",
        "0xc0000142" => "a DLL failed to start",
        var other => $"error {other}",
    };
}
