using System.Globalization;
using System.Xml.Linq;
using CarsControl.Core.Util;

namespace CarsControl.Core.Alerts;

/// <summary>A restart after a crash: a blue screen (BugCheck 1001) or an unexpected shutdown (Kernel-Power 41).</summary>
public sealed record CrashIncident(DateTime At, bool BlueScreen);

/// <summary>
/// Reads crashes from the System event log, which any account can read (no admin). After a blue screen
/// Windows logs both a Kernel-Power 41 and a BugCheck/WER 1001 when it starts again; events within a few
/// minutes of each other are one incident.
/// </summary>
public static class CrashEvents
{
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private static readonly string[] BugCheckProviders = ["Microsoft-Windows-WER-SystemErrorReporting", "BugCheck"];

    public static string Query(DateTime sinceUtc) =>
        "*[System[((Provider[@Name='" + KernelPower + "'] and EventID=41) or " +
        "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
        "(Provider[@Name='BugCheck'] and EventID=1001)) and " +
        $"TimeCreated[@SystemTime>='{sinceUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]";

    /// <summary>Incidents logged after <paramref name="sinceUtc"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<CrashIncident>> ReadAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var wevtutil = Path.Combine(Tools.System32, "wevtutil.exe");
        var r = await ProcessRunner.RunAsync(wevtutil, ["qe", "System", $"/q:{Query(sinceUtc)}", "/f:xml", "/c:50"], null, TimeSpan.FromSeconds(30), ct);
        return r.ExitCode == 0 ? Incidents(Parse(r.Stdout)) : [];
    }

    /// <summary>wevtutil prints &lt;Event&gt; elements back to back, without a root.</summary>
    public static IReadOnlyList<(DateTime At, bool BlueScreen)> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        var root = XElement.Parse("<Events>" + xml + "</Events>");
        var result = new List<(DateTime, bool)>();
        foreach (var e in root.Elements().Where(x => x.Name.LocalName == "Event"))
        {
            var system = e.Elements().FirstOrDefault(x => x.Name.LocalName == "System");
            var provider = system?.Elements().FirstOrDefault(x => x.Name.LocalName == "Provider")?.Attribute("Name")?.Value;
            var time = system?.Elements().FirstOrDefault(x => x.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (provider is null || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
            // Kernel-Power 41 carries the bug check code when the restart followed a blue screen.
            var code = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Data" && x.Attribute("Name")?.Value == "BugcheckCode")?.Value;
            var blue = BugCheckProviders.Contains(provider) || (code is not null && code != "0");
            result.Add((utc.ToLocalTime(), blue));
        }
        return result.OrderBy(r => r.Item1).ToList();
    }

    public static IReadOnlyList<CrashIncident> Incidents(IReadOnlyList<(DateTime At, bool BlueScreen)> events)
    {
        var incidents = new List<CrashIncident>();
        foreach (var (at, blue) in events.OrderBy(e => e.At))
        {
            if (incidents.Count > 0 && at - incidents[^1].At < TimeSpan.FromMinutes(10))
            {
                if (blue && !incidents[^1].BlueScreen) incidents[^1] = incidents[^1] with { BlueScreen = true };
                continue;
            }
            incidents.Add(new CrashIncident(at, blue));
        }
        return incidents;
    }
}
