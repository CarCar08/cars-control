using System.Globalization;
using System.Xml.Linq;
using CarsControl.Core.Util;

namespace CarsControl.Core.Health;

/// <summary>One Windows event, reduced to what the health pages need.</summary>
public sealed record WinEvent(string Provider, int Id, DateTime TimeUtc, IReadOnlyDictionary<string, string> Data, string? Message = null)
{
    public DateTime Local => TimeUtc.ToLocalTime();

    public string? Get(string name) => Data.TryGetValue(name, out var v) ? v : null;

    public long? GetLong(string name) => long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"2026-09-07T02:18:42.3192472Z" style times used inside event data.</summary>
    public DateTime? GetTime(string name) =>
        DateTime.TryParse(Get(name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>
/// Reads the Windows event log with wevtutil (no admin needed for the System and Application logs) and parses
/// the XML it prints. wevtutil prints &lt;Event&gt; elements back to back with no root, and every element is in
/// the event namespace; both are handled here once instead of in every analyzer.
/// </summary>
public static class WinEvents
{
    public static string Provider(string name) => $"Provider[@Name='{name}']";

    /// <summary>XPath for events of the given providers and ids since a time, e.g.
    /// <c>Query(since, ("Microsoft-Windows-Kernel-Power", 41), ("Microsoft-Windows-Kernel-Power", 42))</c>.</summary>
    public static string Query(DateTime sinceUtc, params (string Provider, int Id)[] events)
    {
        var any = string.Join(" or ", events.Select(e => $"({Provider(e.Provider)} and EventID={e.Id})"));
        return $"*[System[({any}) and TimeCreated[@SystemTime>='{sinceUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
    }

    public static async Task<IReadOnlyList<WinEvent>> ReadAsync(string log, string xpath, int max = 200, CancellationToken ct = default)
    {
        var wevtutil = Path.Combine(Tools.System32, "wevtutil.exe");
        var r = await ProcessRunner.RunAsync(wevtutil, ["qe", log, $"/q:{xpath}", "/f:xml", $"/c:{max}", "/rd:true"], null, TimeSpan.FromSeconds(45), ct);
        return r.ExitCode == 0 && !r.TimedOut ? Parse(r.Stdout) : [];
    }

    /// <summary>Events, oldest first.</summary>
    public static IReadOnlyList<WinEvent> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        var root = XElement.Parse("<Events>" + xml + "</Events>");
        var list = new List<WinEvent>();
        foreach (var e in root.Elements().Where(x => x.Name.LocalName == "Event"))
        {
            var system = Child(e, "System");
            var provider = Child(system, "Provider")?.Attribute("Name")?.Value;
            var time = Child(system, "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (provider is null || !int.TryParse(Child(system, "EventID")?.Value, out var id)
                || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unnamed = 0;
            foreach (var d in Child(e, "EventData")?.Elements().Where(x => x.Name.LocalName == "Data") ?? [])
                data[d.Attribute("Name")?.Value ?? $"Data{unnamed++}"] = d.Value;
            // Providers that log free text (WER 1001, driver messages) put it in UserData or unnamed Data.
            list.Add(new WinEvent(provider, id, utc, data, data.Count == unnamed ? string.Join(" ", data.Values) : null));
        }
        return list.OrderBy(w => w.TimeUtc).ToList();
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
}
