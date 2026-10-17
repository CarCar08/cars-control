using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CarsControl.Core.Alerts;
using CarsControl.Core.Data;
using CarsControl.Core.Settings;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Backup;

[Flags]
public enum SetupParts
{
    None = 0,
    /// <summary>Theme, maintenance schedule, Gaming mode, the palette, restart-when-away and Health limits.</summary>
    Preferences = 1,
    /// <summary>Updates you chose to skip or ignore.</summary>
    Ignored = 2,
    /// <summary>The protected-apps list.</summary>
    Protected = 4,
    /// <summary>Which apps you have (winget ids), so a new PC can install them.</summary>
    Apps = 8,
    /// <summary>Phone-alert choices and topic (never the access token). Off by default: the topic is the key to your alerts.</summary>
    Alerts = 16,
    Default = Preferences | Ignored | Protected | Apps,
}

public sealed record IgnoreEntry(string Key, string? Version);

/// <summary>What gets saved: only choices, never secrets, machine-specific holds or the update source.</summary>
public sealed record SetupContent(JsonObject? Preferences, List<IgnoreEntry>? Ignored, List<string>? ProtectedApps, List<AppListEntry>? Apps, JsonObject? Alerts);

/// <summary>A portable copy of how C.A.R.S. is set up, as a small JSON file (kept in the sync folder or saved by hand).</summary>
public sealed record SetupBundle(int Version, string Device, DateTime SavedUtc, string AppVersion, SetupContent Content)
{
    public const int CurrentVersion = 1;
}

public sealed record ApplyReport(IReadOnlyList<string> Changed, IReadOnlyList<string> Skipped)
{
    public bool AnythingChanged => Changed.Count > 0;
}

/// <summary>
/// Builds and applies <see cref="SetupBundle"/>s. A bundle may come from a synced folder, so it's treated as untrusted
/// input: only whitelisted settings are read from it, numbers are clamped, and the things that could redirect trust
/// (the self-update repository, the alert server without validation, the access token) are never taken.
/// </summary>
public static class SetupBundles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Top-level preference sections and values that are safe to move between PCs.
    private static readonly string[] PreferenceValues = [nameof(CarsSettings.RunInBackground), nameof(CarsSettings.CheckIntervalHours), nameof(CarsSettings.HelperIdleMinutes), nameof(CarsSettings.FastScan)];
    private static readonly string[] PreferenceSections = [nameof(CarsSettings.Appearance), nameof(CarsSettings.Maintenance), nameof(CarsSettings.Gaming), nameof(CarsSettings.Palette), nameof(CarsSettings.Restart), nameof(CarsSettings.Health)];

    public static SetupBundle Build(CarsSettings s, IReadOnlyDictionary<string, IgnoreRule> ignores, IEnumerable<AppListEntry> apps, SetupParts parts, string device, string appVersion)
    {
        JsonObject? prefs = null;
        if (parts.HasFlag(SetupParts.Preferences))
        {
            var all = JsonSerializer.SerializeToNode(s, Json)!.AsObject();
            prefs = new JsonObject();
            foreach (var k in PreferenceValues.Concat(PreferenceSections))
                if (all[k] is { } v) prefs[k] = v.DeepClone();
            // How often each palette result was chosen is per PC.
            if (prefs["Palette"] is JsonObject palette) palette.Remove("Usage");
        }

        JsonObject? alerts = null;
        if (parts.HasFlag(SetupParts.Alerts))
        {
            alerts = JsonSerializer.SerializeToNode(s.Alerts, Json)!.AsObject();
            alerts.Remove(nameof(AlertSettings.ProtectedToken)); // encrypted for this Windows account: useless elsewhere and not for sharing
            alerts.Remove(nameof(AlertSettings.DeviceLabel));    // names this PC
        }

        return new SetupBundle(SetupBundle.CurrentVersion, device, DateTime.UtcNow, appVersion, new SetupContent(
            prefs,
            parts.HasFlag(SetupParts.Ignored) ? ignores.Values.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => new IgnoreEntry(r.Key, r.Version)).ToList() : null,
            parts.HasFlag(SetupParts.Protected) ? (s.ProtectedApps ?? [.. Defaults.ProtectedApps]).Order(StringComparer.OrdinalIgnoreCase).ToList() : null,
            parts.HasFlag(SetupParts.Apps) ? AppList.Clean(apps).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList() : null,
            alerts));
    }

    /// <summary>A fingerprint of what's in the bundle (not who saved it or when): two PCs with the same choices match.</summary>
    public static string ContentHash(SetupContent c)
    {
        var text = JsonSerializer.Serialize(c, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    // ---------- reading and writing the file ----------

    public const long MaxBytes = 2_000_000;

    public static string Serialize(SetupBundle b) => JsonSerializer.Serialize(b, Json);

    /// <summary>Null when it isn't a C.A.R.S. setup file (or is from a newer version this build can't read).</summary>
    public static SetupBundle? Parse(string json)
    {
        try
        {
            if (json.Length > MaxBytes) return null;
            var b = JsonSerializer.Deserialize<SetupBundle>(json, Json);
            return b is { Content: not null } && b.Version is >= 1 and <= SetupBundle.CurrentVersion ? b : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<SetupBundle?> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? Parse(await File.ReadAllTextAsync(path, ct)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Atomic: a sync client never sees half a file.</summary>
    public static async Task WriteAsync(string path, SetupBundle bundle, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, Serialize(bundle), ct);
        File.Move(tmp, path, overwrite: true);
    }

    // ---------- applying ----------

    /// <summary>Applies the chosen parts onto <paramref name="target"/> and returns what changed. Ignore rules are returned
    /// through <paramref name="applyIgnores"/> because they live in the database, not in settings.json.</summary>
    public static ApplyReport Apply(CarsSettings target, SetupBundle bundle, SetupParts parts, Action<IgnoreEntry>? applyIgnores = null)
    {
        var changed = new List<string>();
        var skipped = new List<string>();
        var c = bundle.Content;

        if (parts.HasFlag(SetupParts.Preferences) && c.Preferences is { } p) ApplyPreferences(target, p, changed, skipped);

        if (parts.HasFlag(SetupParts.Protected) && c.ProtectedApps is { } protectedApps)
        {
            var clean = protectedApps.Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 200).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
            if (clean.Count > 0 && !(target.ProtectedApps ?? [.. Defaults.ProtectedApps]).Order().SequenceEqual(clean.Order()))
            {
                target.ProtectedApps = clean;
                changed.Add($"Protected apps ({clean.Count})");
            }
        }

        if (parts.HasFlag(SetupParts.Ignored) && c.Ignored is { } ignored && applyIgnores is not null)
        {
            var n = 0;
            foreach (var i in ignored.Take(2000))
            {
                if (string.IsNullOrWhiteSpace(i.Key) || i.Key.Length > 300) continue;
                applyIgnores(i);
                n++;
            }
            if (n > 0) changed.Add($"Ignored updates ({n})");
        }

        if (parts.HasFlag(SetupParts.Alerts) && c.Alerts is { } a) ApplyAlerts(target, a, changed, skipped);
        return new ApplyReport(changed, skipped);
    }

    private static void ApplyPreferences(CarsSettings target, JsonObject p, List<string> changed, List<string> skipped)
    {
        void Section<T>(string name, Action<T> set) where T : class
        {
            if (p[name] is not { } node) return;
            try
            {
                if (node.Deserialize<T>(Json) is { } value)
                {
                    set(value);
                    changed.Add(name);
                }
            }
            catch (JsonException)
            {
                skipped.Add(name + " (couldn't be read)");
            }
        }

        void Value<T>(string key, string title, Action<T> set)
        {
            if (p[key] is not { } node) return;
            try { var value = node.Deserialize<T>(Json); if (value is not null) { set(value); changed.Add(title); } }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { skipped.Add(title + " (couldn't be read)"); }
        }
        Value<bool>(nameof(CarsSettings.RunInBackground), "Run in background", v => target.RunInBackground = v);
        Value<int>(nameof(CarsSettings.CheckIntervalHours), "Check interval", v => target.CheckIntervalHours = Math.Clamp(v, 1, 48));
        Value<int>(nameof(CarsSettings.HelperIdleMinutes), "Admin session idle time", v => target.HelperIdleMinutes = Math.Clamp(v, 1, 240));
        Value<bool>(nameof(CarsSettings.FastScan), "Fast scan", v => target.FastScan = v);

        Section<AppearanceSettings>("Appearance", v => target.Appearance = v);
        Section<MaintenanceSettings>("Maintenance", v =>
        {
            v.IdleMinutes = Math.Clamp(v.IdleMinutes, 1, 240);
            if (!TimeSpan.TryParse(v.Time, out _)) v.Time = "03:00";
            target.Maintenance = v;
        });
        Section<GamingSettings>("Gaming", v => target.Gaming = v);
        Section<PaletteSettings>("Palette", v => { v.Usage = target.Palette.Usage; target.Palette = v; });
        Section<RestartSettings>("Restart", v =>
        {
            v.WindowHours = Math.Clamp(v.WindowHours, 1, 12);
            v.IdleMinutes = Math.Clamp(v.IdleMinutes, 1, 600);
            if (!TimeSpan.TryParse(v.Time, out _)) v.Time = "04:00";
            target.Restart = v;
        });
        Section<HealthSettings>("Health", v =>
        {
            v.CpuAlertC = Math.Clamp(v.CpuAlertC, 60, 110);
            v.GpuAlertC = Math.Clamp(v.GpuAlertC, 60, 110);
            target.Health = v;
        });
    }

    private static void ApplyAlerts(CarsSettings target, JsonObject a, List<string> changed, List<string> skipped)
    {
        try
        {
            if (a.Deserialize<AlertSettings>(Json) is not { } v) return;
            // The server and topic decide where your alerts go: a synced file can't point them somewhere odd.
            if (v.Topic is not null && !AlertPolicy.IsValidTopic(v.Topic)) { skipped.Add("Phone alerts (the topic isn't valid)"); return; }
            if (!AlertPolicy.IsValidServer(v.Server)) { skipped.Add("Phone alerts (the server isn't a plain https address)"); return; }
            v.ProtectedToken = target.Alerts.ProtectedToken; // this PC's own token stays
            v.DeviceLabel = target.Alerts.DeviceLabel;
            target.Alerts = v;
            changed.Add("Phone alerts");
        }
        catch (JsonException)
        {
            skipped.Add("Phone alerts (couldn't be read)");
        }
    }
}

public enum SyncAction { InSync, Push, Pull, Conflict, Nothing }

/// <summary>When to send and when to fetch. <c>last</c> is the fingerprint both sides had at the previous sync.</summary>
public static class SetupSync
{
    public static SyncAction Decide(string localHash, string? lastHash, string? remoteHash)
    {
        if (remoteHash is null) return SyncAction.Push;           // nothing in the folder yet
        if (localHash == remoteHash) return SyncAction.InSync;
        if (lastHash is null) return SyncAction.Conflict;         // two PCs that never synced: ask
        var localChanged = localHash != lastHash;
        var remoteChanged = remoteHash != lastHash;
        return localChanged && remoteChanged ? SyncAction.Conflict : localChanged ? SyncAction.Push : remoteChanged ? SyncAction.Pull : SyncAction.InSync;
    }

    public static SetupParts PartsOf(Settings.SyncSettings s) =>
        (s.Preferences ? SetupParts.Preferences : 0) | (s.Ignored ? SetupParts.Ignored : 0) | (s.Protected ? SetupParts.Protected : 0) | (s.Apps ? SetupParts.Apps : 0) | (s.Alerts ? SetupParts.Alerts : 0);

    public const string FolderName = "C.A.R.S.";
    public const string SetupFileName = "setup.json";

    /// <summary>The sync folder inside Proton Drive (or another cloud folder): never created until you turn syncing on.</summary>
    public static string? DefaultFolder(IEnumerable<Storage.SyncRoot> roots)
    {
        var proton = roots.FirstOrDefault(r => r.Provider.Contains("Proton", StringComparison.OrdinalIgnoreCase));
        return proton is null ? null : Path.Combine(proton.Path, FolderName);
    }
}
