using System.Text.RegularExpressions;

namespace CarsControl.Core.Health;

public sealed record SlowItem(string Kind, string Name, TimeSpan Cost);

public sealed record BootRecord(DateTime At, TimeSpan Boot, TimeSpan MainPath, TimeSpan PostBoot, TimeSpan Kernel, TimeSpan Drivers, TimeSpan Explorer)
{
    public bool IsSlow => Boot >= TimeSpan.FromSeconds(60);
}

public sealed record ShutdownRecord(DateTime At, TimeSpan Time);

public sealed record BootReport(IReadOnlyList<BootRecord> Boots, IReadOnlyList<ShutdownRecord> Shutdowns, IReadOnlyList<SlowItem> SlowItems);

/// <summary>
/// Boot and shutdown times from Microsoft-Windows-Diagnostics-Performance (events 100 boot, 200 shutdown, and
/// 101/102/103 for the app, driver or service that slowed a boot). That log is administrators-only, so the
/// admin helper reads and parses it and hands back this summary.
/// </summary>
public static class BootPerformance
{
    public const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private const string Provider = "Microsoft-Windows-Diagnostics-Performance";

    public static string Query(DateTime sinceUtc) => WinEvents.Query(sinceUtc, (Provider, 100), (Provider, 200), (Provider, 101), (Provider, 102), (Provider, 103));

    public static BootReport Build(IReadOnlyList<WinEvent> events)
    {
        TimeSpan Ms(WinEvent e, string name) => TimeSpan.FromMilliseconds(Math.Max(0, e.GetLong(name) ?? 0));

        var boots = events.Where(e => e.Id == 100).Select(e => new BootRecord(e.Local, Ms(e, "BootTime"), Ms(e, "MainPathBootTime"), Ms(e, "BootPostBootTime"),
            Ms(e, "BootKernelInitTime"), Ms(e, "BootDriverInitTime"), Ms(e, "BootExplorerInitTime"))).OrderByDescending(b => b.At).ToList();
        var shutdowns = events.Where(e => e.Id == 200).Select(e => new ShutdownRecord(e.Local, Ms(e, "ShutdownTime"))).OrderByDescending(s => s.At).ToList();

        // What slowed boots: the worst per name, so a program that slows every boot shows once with its worst cost.
        var slow = events.Where(e => e.Id is 101 or 102 or 103)
            .Select(e => new SlowItem(e.Id switch { 101 => "App", 102 => "Driver", _ => "Service" },
                e.Get("FriendlyName") is { Length: > 0 } f ? f : e.Get("Name") ?? e.Get("FileName") ?? "unknown", Ms(e, "TotalTime")))
            .GroupBy(s => (s.Kind, s.Name)).Select(g => g.OrderByDescending(s => s.Cost).First())
            .OrderByDescending(s => s.Cost).Take(12).ToList();
        return new BootReport(boots, shutdowns, slow);
    }
}

/// <summary>What's keeping the PC from sleeping, from <c>powercfg /requests</c> (administrators only).</summary>
public sealed record PowerRequest(string Category, string Kind, string Who);

public static class PowerRequests
{
    /// <summary>Sections look like "DISPLAY:" then "[PROCESS] \Device\HarddiskVolume3\…\chrome.exe" lines, or "None.".</summary>
    public static IReadOnlyList<PowerRequest> Parse(IEnumerable<string> lines)
    {
        var list = new List<PowerRequest>();
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var header = Regex.Match(line, @"^([A-Z][A-Z ]+):$");
            if (header.Success) { section = header.Groups[1].Value; continue; }
            var item = Regex.Match(line, @"^\[(?<kind>[A-Z]+)\]\s*(?<who>.+)$");
            if (section is not null && item.Success)
                list.Add(new PowerRequest(Friendly(section), item.Groups["kind"].Value, Tidy(item.Groups["who"].Value)));
        }
        return list;
    }

    private static string Friendly(string section) => section switch
    {
        "DISPLAY" => "keeping the screen on",
        "SYSTEM" => "keeping the PC awake",
        "AWAYMODE" => "away mode (media playing)",
        "EXECUTION" => "keeping apps running",
        "PERFBOOST" => "keeping performance high",
        "ACTIVELOCKSCREEN" => "keeping the lock screen active",
        _ => section.ToLowerInvariant(),
    };

    /// <summary>"\Device\HarddiskVolume3\Program Files\Foo\foo.exe" → "foo.exe".</summary>
    private static string Tidy(string who) => who.Contains('\\') ? who[(who.LastIndexOf('\\') + 1)..] : who;
}
