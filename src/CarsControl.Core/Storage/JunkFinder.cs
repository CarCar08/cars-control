using System.Text.RegularExpressions;

namespace CarsControl.Core.Storage;

public enum JunkKind { Installer, EmptyFolder, HugeLog, GameRedist }

public sealed record JunkItem(JunkKind Kind, string Path, long Bytes, DateTime Modified, string Reason, bool IsFolder)
{
    public string Category => Kind switch
    {
        JunkKind.Installer => "Old installers",
        JunkKind.EmptyFolder => "Empty folders",
        JunkKind.HugeLog => "Huge log files",
        _ => "Game setup leftovers",
    };

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public override string ToString() => $"{Name} ({Category})";
}

public sealed record JunkOptions(
    string? DownloadsFolder,
    IReadOnlyList<string> EmptyFolderRoots,
    IReadOnlyList<string> LogRoots,
    IReadOnlyList<string> SteamLibraries,
    DateTime? Now = null,
    int InstallerAgeDays = 60,
    long HugeLogBytes = 100L * 1024 * 1024);

/// <summary>
/// Things that are almost never wanted and that Cleanup's rules don't cover: installers you downloaded months ago, folders
/// with nothing in them, logs that grew to hundreds of MB, and the redistributable installers Steam leaves inside games.
/// Everything found is shown first and goes to the Recycle Bin, so it can be put back.
/// </summary>
public static partial class JunkFinder
{
    [GeneratedRegex(@"(?i)(setup|install|installer|update|updater)")]
    private static partial Regex InstallerName();

    private static readonly string[] InstallerExtensions = [".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"];

    public static IReadOnlyList<JunkItem> Find(JunkOptions o, CancellationToken ct = default)
    {
        var now = o.Now ?? DateTime.Now;
        var list = new List<JunkItem>();
        list.AddRange(Installers(o.DownloadsFolder, now, o.InstallerAgeDays));
        foreach (var root in o.EmptyFolderRoots) list.AddRange(EmptyFolders(root, now, ct));
        foreach (var root in o.LogRoots) list.AddRange(HugeLogs(root, now, o.HugeLogBytes, ct));
        foreach (var lib in o.SteamLibraries) list.AddRange(GameRedist(lib, now));
        return list.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(i => i.Bytes).ToList();
    }

    public static bool LooksLikeInstaller(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (InstallerExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && InstallerName().IsMatch(Path.GetFileNameWithoutExtension(fileName));
    }

    private static IEnumerable<JunkItem> Installers(string? downloads, DateTime now, int ageDays)
    {
        if (downloads is null || !Directory.Exists(downloads)) yield break;
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(downloads).EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in files)
        {
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint) || !LooksLikeInstaller(f.Name)) continue;
            var age = (now - f.LastWriteTime).TotalDays;
            if (age < ageDays) continue;
            yield return new JunkItem(JunkKind.Installer, f.FullName, f.Length, f.LastWriteTime, $"An installer from {(int)age} days ago. If you still need it, it can be downloaded again.", false);
        }
    }

    /// <summary>The top-most folders with no file anywhere inside, made at least a week ago.</summary>
    public static IEnumerable<JunkItem> EmptyFolders(string root, DateTime now, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) yield break;
        var found = new List<JunkItem>();
        Visit(new DirectoryInfo(root), isRoot: true);
        foreach (var f in found) yield return f;

        // Returns true when the folder holds no files at any depth.
        bool Visit(DirectoryInfo dir, bool isRoot)
        {
            ct.ThrowIfCancellationRequested();
            List<DirectoryInfo> subs;
            bool hasFile;
            try
            {
                subs = dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList();
                hasFile = dir.EnumerateFiles().Any();
                // A reparse point inside is something we don't look into, so the folder isn't known to be empty.
                if (dir.EnumerateDirectories().Any(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint))) hasFile = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            var emptySubs = new List<DirectoryInfo>();
            var allSubsEmpty = true;
            foreach (var s in subs)
            {
                if (Visit(s, isRoot: false)) emptySubs.Add(s);
                else allSubsEmpty = false;
            }
            var empty = !hasFile && allSubsEmpty;
            if (empty && !isRoot) return true;
            // This folder isn't empty, but some of its children are: those are the top-most empty ones.
            foreach (var s in emptySubs)
                if ((now - s.LastWriteTime).TotalDays >= 7 && !s.Attributes.HasFlag(FileAttributes.System) && !s.Attributes.HasFlag(FileAttributes.Hidden))
                    found.Add(new JunkItem(JunkKind.EmptyFolder, s.FullName, 0, s.LastWriteTime, "Nothing is inside it.", true));
            return false;
        }
    }

    private static IEnumerable<JunkItem> HugeLogs(string root, DateTime now, long minBytes, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root).EnumerateFiles("*.log", options).Where(f => f.Length >= minBytes).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if ((now - f.LastWriteTime).TotalDays < 2) continue; // still being written
            yield return new JunkItem(JunkKind.HugeLog, f.FullName, f.Length, f.LastWriteTime, "A log file that grew very large; the program starts a new one if it needs it.", false);
        }
    }

    private static IEnumerable<JunkItem> GameRedist(string steamLibrary, DateTime now)
    {
        var common = Path.Combine(steamLibrary, "steamapps", "common");
        if (!Directory.Exists(common)) yield break;
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(common).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var game in dirs)
        {
            var redist = Path.Combine(game, "_CommonRedist");
            if (!Directory.Exists(redist)) continue;
            long size = 0;
            try { size = DirectorySizer.Measure(redist); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unmeasured */ }
            yield return new JunkItem(JunkKind.GameRedist, redist, size, Directory.GetLastWriteTime(redist),
                $"Setup files for Visual C++ and DirectX that {Path.GetFileName(game)} ran once. Steam puts them back if a game needs them again.", true);
        }
    }
}
