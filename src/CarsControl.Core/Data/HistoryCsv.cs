using System.Text;

namespace CarsControl.Core.Data;

/// <summary>History as a spreadsheet: one row per operation. Quotes, commas and line breaks inside a value
/// are escaped the standard way, and values that start with = + - @ get a leading apostrophe so Excel never
/// runs one as a formula (an app or message text is not something to trust).</summary>
public static class HistoryCsv
{
    public static readonly string[] Header = ["When", "Action", "Name", "Version", "Result", "Admin", "Via", "Details"];

    public static string Build(IEnumerable<HistoryEntry> entries, Func<HistoryEntry, string>? friendlyKind = null, Func<string, string>? friendlySource = null)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var e in entries)
        {
            var version = (e.FromVersion, e.ToVersion) switch
            {
                ({ } a, { } b) when a != b => $"{a} -> {b}",
                ({ } a, _) => a,
                (_, { } b) => b,
                _ => "",
            };
            string[] cells =
            [
                e.Started.ToString("yyyy-MM-dd HH:mm"),
                friendlyKind?.Invoke(e) ?? e.Kind,
                e.Title,
                version,
                e.Status,
                e.Elevated ? "yes" : "no",
                friendlySource?.Invoke(e.Source) ?? e.Source,
                e.Message ?? "",
            ];
            sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
