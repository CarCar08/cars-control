using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarsControl.Core.Storage;

/// <summary>
/// Saves each drive's last scan to %LOCALAPPDATA%\CarsControl\scans\{letter}.json so Storage opens on
/// the previous results instantly. The tree is kept to <see cref="MaxDepth"/> levels (and the biggest
/// <see cref="MaxChildren"/> subfolders per folder); deeper folders are marked Truncated and rescanned
/// on demand when you drill into them.
/// </summary>
public static class ScanCache
{
    public const int MaxDepth = 4;
    public const int MaxChildren = 300;

    public static string Folder => Path.Combine(Util.UserData.Root, "scans");

    public sealed record Node(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("b")] long Bytes,
        [property: JsonPropertyName("f")] long Files,
        [property: JsonPropertyName("i")] bool Inaccessible,
        [property: JsonPropertyName("t")] bool Truncated,
        [property: JsonPropertyName("c")] List<Node>? Children);

    /// <summary>Version 2 (round 3): sizes are space on disk. Version-1 caches counted online-only
    /// iCloud files (Fast scan) or skipped downloaded ones (folder scan), so they are ignored.</summary>
    public const int FormatVersion = 2;

    public sealed record Saved(int Version, string Root, DateTime When, double ElapsedMs, long DriveUsed, string Method,
        Node Tree, List<BigFile> Largest, List<BigFile> OldLarge, long OnlineOnly = 0);

    public static string PathFor(string driveOrRoot) => Path.Combine(Folder, $"{char.ToUpperInvariant(driveOrRoot.TrimEnd(':', '\\')[0])}.json");

    public static void Save(ScanResult r)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var saved = new Saved(FormatVersion, r.Root.FullPath, r.When, r.Elapsed.TotalMilliseconds, r.DriveUsedBytes, r.Method,
                ToNode(r.Root, 0), r.Largest.ToList(), r.OldLarge.Take(2000).ToList(), r.OnlineOnlyBytes);
            File.WriteAllText(PathFor(r.Root.FullPath), JsonSerializer.Serialize(saved));
        }
        catch
        {
            // Cache only.
        }
    }

    public static ScanResult? Load(string drive)
    {
        try
        {
            var file = PathFor(drive);
            if (!File.Exists(file)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
            if (saved is null || saved.Version != FormatVersion) return null;
            var root = FromNode(saved.Tree, null, saved.Root);
            root.FinishFromTotals();
            return new ScanResult
            {
                Root = root,
                Largest = saved.Largest,
                OldLarge = saved.OldLarge,
                Elapsed = TimeSpan.FromMilliseconds(saved.ElapsedMs),
                When = saved.When,
                DriveUsedBytes = saved.DriveUsed,
                Method = saved.Method,
                FromCache = true,
                OnlineOnlyBytes = saved.OnlineOnly,
            };
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<ScanResult> LoadAll()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var f in Directory.EnumerateFiles(Folder, "?.json"))
            if (Load(Path.GetFileNameWithoutExtension(f)) is { } r) yield return r;
    }

    internal static Node ToNode(DirNode d, int depth)
    {
        if (depth >= MaxDepth)
            return new Node(d.Name, d.TotalBytes, d.TotalFiles, d.Inaccessible, d.Children.Count > 0 || d.Truncated, null);
        var kids = d.Children.Take(MaxChildren).Select(c => ToNode(c, depth + 1)).ToList();
        return new Node(d.Name, d.TotalBytes, d.TotalFiles, d.Inaccessible, d.Truncated || d.Children.Count > MaxChildren, kids);
    }

    internal static DirNode FromNode(Node n, DirNode? parent, string? rootName = null)
    {
        var d = new DirNode(rootName ?? n.Name, parent) { TotalBytes = n.Bytes, TotalFiles = n.Files, Inaccessible = n.Inaccessible, Truncated = n.Truncated };
        if (n.Children is not null)
            foreach (var c in n.Children) d.Children.Add(FromNode(c, d));
        return d;
    }
}
