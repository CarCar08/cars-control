using System.Text.Json;

namespace CarsControl.Core.Data;

public sealed record ImportPreview(int IgnoreRules, int HistoryEntries, string ConfigDir);

/// <summary>One-time import from UniGetUI (%LOCALAPPDATA%\UniGetUI\Configuration): its ignore list and
/// operation history, so replacing it loses nothing. Formats verified against UniGetUI 2026.3.1 on this PC.</summary>
public static class UniGetUiImporter
{
    public const string ImportedSettingKey = "unigetui_imported";

    public static string DefaultConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniGetUI", "Configuration");

    public static ImportPreview? Preview(string? configDir = null)
    {
        configDir ??= DefaultConfigDir;
        if (!Directory.Exists(configDir)) return null;
        var rules = ReadIgnores(configDir).Count;
        var history = ReadHistory(configDir).Count;
        return rules + history == 0 ? null : new ImportPreview(rules, history, configDir);
    }

    public static (int Rules, int History) Import(CarsDb db, string? configDir = null)
    {
        configDir ??= DefaultConfigDir;
        var rules = ReadIgnores(configDir);
        foreach (var (key, version) in rules) db.SetIgnore(key, version);
        var history = ReadHistory(configDir);
        foreach (var h in history) db.AddHistory(h);
        db.SetSetting(ImportedSettingKey, DateTime.Now.ToString("O"));
        return (rules.Count, history.Count);
    }

    /// <summary>"winget\\Ubisoft.Connect": "174.0.0.13368" → winget:Ubisoft.Connect skip 174.0.0.13368.
    /// "*" means ignore all versions.</summary>
    internal static List<(string Key, string? Version)> ReadIgnores(string configDir)
    {
        var file = Path.Combine(configDir, "IgnoredPackageUpdates.json");
        var list = new List<(string, string?)>();
        if (!File.Exists(file)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var parts = p.Name.Split('\\', 2);
            if (parts.Length != 2) continue;
            var prefix = parts[0].ToLowerInvariant() switch
            {
                "winget" => "winget",
                "powershell7" or "powershell" or "pwsh" => "psmodule",
                "npm" => "npm",
                "pip" => "pip",
                _ => null,
            };
            if (prefix is null) continue;
            var version = p.Value.GetString();
            list.Add(($"{prefix}:{parts[1]}", version is null or "*" ? null : version));
        }
        return list;
    }

    internal static List<HistoryEntry> ReadHistory(string configDir)
    {
        var file = Path.Combine(configDir, "OperationHistory.json");
        var list = new List<HistoryEntry>();
        if (!File.Exists(file)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = Str(e, "PackageId");
            if (string.IsNullOrEmpty(id)) continue;
            var when = DateTime.TryParse(Str(e, "TimestampUtc"), out var t) ? t.ToLocalTime() : DateTime.MinValue;
            var status = Str(e, "Status") switch
            {
                "succeeded" => "Succeeded",
                "failed" => "Failed",
                "canceled" => "Cancelled",
                var s => s ?? "Unknown",
            };
            var output = e.TryGetProperty("Output", out var o) && o.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, o.EnumerateArray().Select(x => Str(x, "Text")).Where(x => !string.IsNullOrWhiteSpace(x)))
                : null;
            list.Add(new HistoryEntry(
                0, when, when,
                Str(e, "Kind") == "update-package" ? "update" : Str(e, "Kind") ?? "update",
                $"{(Str(e, "ManagerName") == "winget" ? "winget" : Str(e, "ManagerName"))}:{id}",
                Str(e, "PackageName") ?? id,
                $"UniGetUI/{Str(e, "ManagerName")}",
                Str(e, "VersionBefore"),
                Str(e, "VersionAfter"),
                e.TryGetProperty("RanElevated", out var el) && el.ValueKind == JsonValueKind.True,
                status,
                e.TryGetProperty("ExitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0,
                Str(e, "FailureSummary"),
                output,
                Imported: true));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
