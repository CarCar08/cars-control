using System.Text;
using System.Text.Json;
using CarsControl.Core.Models;
using Microsoft.Win32;

namespace CarsControl.Core.Storage;

public sealed record GameEntry(
    string Name,
    string Source,          // Steam | Epic | Other
    string Library,         // e.g. D:\SteamLibrary
    string? InstallPath,
    long SizeBytes,
    DateTime? LastPlayed,
    DateTime? LastUpdated,
    string? SteamAppId,
    AppEntry? App = null,
    int? PlaytimeMinutes = null);

public sealed record GameLibrary(string Source, string Path, bool Online, int Games, long Bytes);

/// <summary>Steam (libraryfolders.vdf + appmanifest_*.acf), Epic (*.item manifests) and any other games
/// the inventory classified, with sizes and last-played dates where the launcher records them.</summary>
public static class GameLibraries
{
    public static (IReadOnlyList<GameEntry> Games, IReadOnlyList<GameLibrary> Libraries) Read(IEnumerable<AppEntry>? inventory = null)
    {
        var games = new List<GameEntry>();
        var libs = new List<GameLibrary>();
        ReadSteam(games, libs);
        ReadEpic(games, libs);

        // Games from the inventory that no launcher manifest covered (Riot, EA, Rockstar, …).
        if (inventory is not null)
        {
            var known = games.Where(g => g.InstallPath is not null).Select(g => g.InstallPath!.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in inventory.Where(a => a.Channel == UpdateChannel.Game))
            {
                if (a.InstallLocation is { } loc && known.Contains(loc.TrimEnd('\\'))) continue;
                if (a.Arp?.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) == true) continue;
                games.Add(new GameEntry(a.Name, "Other", a.InstallLocation is { } l ? Path.GetPathRoot(l) ?? "" : "", a.InstallLocation, a.SizeBytes ?? 0, null, a.InstallDate, null, a));
            }
        }
        return (games.OrderByDescending(g => g.SizeBytes).ToList(), libs);
    }

    private static string SteamRoot() =>
        (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string)?.Replace('/', '\\')
        ?? @"C:\Program Files (x86)\Steam";

    private static Vdf? SteamLibraryFolders(string steam)
    {
        var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        return File.Exists(vdfPath) ? Vdf.Parse(File.ReadAllText(vdfPath)).Get("libraryfolders") : null;
    }

    /// <summary>Every Steam library's root folder (connected or not), e.g. D:\SteamLibrary.</summary>
    public static IReadOnlyList<string> SteamLibraryRoots()
    {
        try
        {
            return SteamLibraryFolders(SteamRoot())?.Children.Values
                .Select(l => l.Get("path")?.Value?.Replace(@"\\", @"\"))
                .OfType<string>()
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void ReadSteam(List<GameEntry> games, List<GameLibrary> libs)
    {
        var steam = SteamRoot();
        var folders = SteamLibraryFolders(steam);
        if (folders is null) return;
        var played = ReadSteamPlaytime(steam);
        foreach (var lib in folders.Children.Values)
        {
            var path = lib.Get("path")?.Value?.Replace(@"\\", @"\");
            if (path is null) continue;
            var steamapps = Path.Combine(path, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                libs.Add(new GameLibrary("Steam", path, false, lib.Get("apps")?.Children.Count ?? 0, 0));
                continue;
            }
            var count = 0;
            long bytes = 0;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var app = Vdf.Parse(File.ReadAllText(acf)).Get("AppState");
                    if (app is null) continue;
                    var name = app.Get("name")?.Value ?? Path.GetFileNameWithoutExtension(acf);
                    if (name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                    var size = long.TryParse(app.Get("SizeOnDisk")?.Value, out var s) ? s : 0;
                    var installDir = app.Get("installdir")?.Value;
                    var appId = app.Get("appid")?.Value;
                    // The manifest's LastPlayed is often missing; Steam's per-user localconfig has the real one.
                    var lastPlayed = Unix(app.Get("LastPlayed")?.Value);
                    int? minutes = null;
                    if (appId is not null && played.TryGetValue(appId, out var pt))
                    {
                        if (pt.LastPlayed is { } lp && (lastPlayed is null || lp > lastPlayed)) lastPlayed = lp;
                        minutes = pt.Minutes;
                    }
                    games.Add(new GameEntry(
                        name, "Steam", path,
                        installDir is null ? null : Path.Combine(steamapps, "common", installDir),
                        size,
                        lastPlayed,
                        Unix(app.Get("LastUpdated")?.Value),
                        appId,
                        PlaytimeMinutes: minutes));
                    count++;
                    bytes += size;
                }
                catch
                {
                    // Partially written manifest during an update; skip it.
                }
            }
            libs.Add(new GameLibrary("Steam", path, true, count, bytes));
        }
    }

    /// <summary>Steam\userdata\{account}\config\localconfig.vdf: per-app LastPlayed (unix) and Playtime
    /// (minutes). Across several Steam accounts, the latest play and the largest playtime win.</summary>
    internal static Dictionary<string, (DateTime? LastPlayed, int Minutes)> ReadSteamPlaytime(string steamRoot)
    {
        var result = new Dictionary<string, (DateTime?, int)>();
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return result;
        foreach (var account in Directory.EnumerateDirectories(userdata))
        {
            var file = Path.Combine(account, "config", "localconfig.vdf");
            if (!File.Exists(file)) continue;
            try
            {
                MergePlaytime(result, Vdf.Parse(File.ReadAllText(file)));
            }
            catch
            {
                // Steam rewrites this file while running; skip a half-written copy.
            }
        }
        return result;
    }

    internal static void MergePlaytime(Dictionary<string, (DateTime? LastPlayed, int Minutes)> into, Vdf localConfig)
    {
        var apps = localConfig.Get("UserLocalConfigStore")?.Get("Software")?.Get("Valve")?.Get("Steam")?.Get("apps");
        if (apps is null) return;
        foreach (var (id, node) in apps.Children)
        {
            var last = Unix(node.Get("LastPlayed")?.Value);
            var minutes = int.TryParse(node.Get("Playtime")?.Value, out var m) ? m : 0;
            if (into.TryGetValue(id, out var cur))
                into[id] = (cur.LastPlayed is null || (last is not null && last > cur.LastPlayed) ? last : cur.LastPlayed, Math.Max(cur.Minutes, minutes));
            else
                into[id] = (last, minutes);
        }
    }

    private static void ReadEpic(List<GameEntry> games, List<GameLibrary> libs)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        var found = new List<GameEntry>();
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                var name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                if (name is null || loc is null) continue;
                var size = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                // The library is the folder games install into (e.g. C:\Program Files\Epic Games), like Steam's,
                // so Storage › Games can filter by it (it used to be the bare drive root).
                var library = Path.GetDirectoryName(loc.TrimEnd('\\')) ?? Path.GetPathRoot(loc) ?? "";
                found.Add(new GameEntry(name, "Epic", library, loc, size, null, null, null));
            }
            catch
            {
                // Malformed manifest.
            }
        }
        games.AddRange(found);
        foreach (var lib in found.GroupBy(g => g.Library, StringComparer.OrdinalIgnoreCase))
            libs.Add(new GameLibrary("Epic", lib.Key, Directory.Exists(lib.Key), lib.Count(), lib.Sum(g => g.SizeBytes)));
    }

    private static DateTime? Unix(string? s) =>
        long.TryParse(s, out var v) && v > 0 ? DateTimeOffset.FromUnixTimeSeconds(v).LocalDateTime : null;
}

/// <summary>Minimal Valve KeyValues (VDF/ACF) text parser.</summary>
public sealed class Vdf
{
    public Dictionary<string, Vdf> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Value { get; private set; }

    public Vdf? Get(string key) => Children.TryGetValue(key, out var v) ? v : null;

    public static Vdf Parse(string text)
    {
        var root = new Vdf();
        var stack = new Stack<Vdf>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '{')
            {
                var node = new Vdf();
                if (pendingKey is not null) stack.Peek().Children[pendingKey] = node;
                stack.Push(node);
                pendingKey = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                i++;
                continue;
            }
            var token = ReadToken(text, ref i);
            if (pendingKey is null) pendingKey = token;
            else
            {
                stack.Peek().Children[pendingKey] = new Vdf { Value = token };
                pendingKey = null;
            }
        }
        return root;
    }

    private static string ReadToken(string text, ref int i)
    {
        var sb = new StringBuilder();
        if (text[i] == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] == '\\' ? '\\' : text[i + 1]); i += 2; continue; }
                sb.Append(text[i++]);
            }
            i++; // closing quote
        }
        else
        {
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}')) sb.Append(text[i++]);
        }
        return sb.ToString();
    }
}
