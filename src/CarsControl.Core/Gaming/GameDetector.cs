using CarsControl.Core.Elevation;

namespace CarsControl.Core.Gaming;

/// <summary>A game's install folder, from Steam/Epic manifests or the app inventory.</summary>
public sealed record GameFolder(string Name, string Path);

/// <summary>
/// Decides whether a running program is a game: its exe sits inside a known game's install folder (the
/// deepest match wins, so a game inside another's folder is named correctly), or its file name is on your
/// own list. Launchers, crash reporters, anti-cheat services, installers and redistributables inside game
/// folders don't count, so Gaming mode doesn't flicker on while a game updates or a launcher idles.
/// </summary>
public static class GameDetector
{
    /// <summary>Parts of exe names that are never "the game".</summary>
    private static readonly string[] NotGameParts =
    [
        "crash", "report", "unins", "setup", "install", "redist", "dxsetup", "dotnet", "prereq", "vc_", "vcredist",
        "anticheat", "battleye", "beservice", "eac_", "easyanticheat", "launcher", "updater", "patcher", "webhelper",
        "cefprocess", "qtwebengine", "steamerrorreporter", "bugsplat", "sentry",
    ];

    /// <summary>Apps sold on Steam that run all day and aren't games (Wallpaper Engine kept Gaming mode on
    /// permanently in the first test), matched by their install folder's name.</summary>
    private static readonly HashSet<string> NotGameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper_engine", "Lossless Scaling", "SteamVR", "Soundpad", "Rainmeter", "fpsVR", "OVR Toolkit", "Desktop Mate",
        "BongoCat", "Steamworks Shared", "Steam Controller Configs", "DirectXRedist", "GameInputRedist", "Launcher",
    };

    public static bool IsNeverAGame(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return NotGameParts.Any(lower.Contains);
    }

    public static bool IsNotAGameFolder(string folder) => NotGameFolders.Contains(System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')));

    /// <summary>The game's name, or null when <paramref name="exePath"/> isn't a game.</summary>
    public static string? Match(string exePath, IReadOnlyList<GameFolder> folders, IReadOnlyCollection<string>? extraGames = null,
        IReadOnlyCollection<string>? ignored = null)
    {
        var file = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(file)) return null;
        if (ignored?.Contains(file, StringComparer.OrdinalIgnoreCase) == true) return null;
        if (extraGames?.Contains(file, StringComparer.OrdinalIgnoreCase) == true) return Path.GetFileNameWithoutExtension(file);
        if (IsNeverAGame(file)) return null;

        GameFolder? best = null;
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f.Path) || IsTooBroad(f.Path) || IsNotAGameFolder(f.Path)) continue;
            var root = f.Path.TrimEnd('\\') + "\\";
            if (!exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || f.Path.Length > best.Path.Length) best = f;
        }
        return best?.Name;
    }

    /// <summary>"D:\" or "C:\Program Files" as a game folder would make every program a game (a broken
    /// manifest or an "Other" game the inventory found without a real folder).</summary>
    internal static bool IsTooBroad(string path)
    {
        var p = path.TrimEnd('\\');
        if (p.Length <= 3 || Storage.DirectorySizer.IsTooBroadToMeasure(p)) return true;
        var name = System.IO.Path.GetFileName(p);
        return LibraryFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Library roots (a manifest with an empty install folder points at these).</summary>
    private static readonly string[] LibraryFolderNames = ["common", "steamapps", "SteamLibrary", "Steam", "Epic Games", "Games"];

    /// <summary>The full path of a running program (works for elevated games too), or null.</summary>
    public static string? ImagePath(int pid) => Native.ImagePath(pid);
}
