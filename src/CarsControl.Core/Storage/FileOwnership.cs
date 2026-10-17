using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;

namespace CarsControl.Core.Storage;

public enum OwnerKind { Free, AppData, Synced, InstalledApp, System, InUse }

public sealed record Ownership(OwnerKind Kind, string Text, string? Provider = null)
{
    /// <summary>Recycling is offered for loose files and (with a warning) app data and cloud-synced files;
    /// never for files an installed app or Windows depends on, or files currently open.</summary>
    public bool CanRecycle => Kind is OwnerKind.Free or OwnerKind.AppData or OwnerKind.Synced;

    /// <summary>Synced files can be made online-only instead: the cloud keeps them, the disk doesn't.</summary>
    public bool CanFreeUp => Kind == OwnerKind.Synced;

    public bool IsWarning => Kind is OwnerKind.AppData or OwnerKind.Synced;
}

/// <summary>
/// Tags big files with who owns them before offering "Recycle". Recycling Apex's .starpak or Valorant's
/// .ucas breaks the game, and Salad's 113 GB ext4.vhdx is a live VM disk; those must be removed by
/// uninstalling the app instead. Model files in an app's data folder (LTX Desktop) are allowed with a warning.
/// Files in a cloud-sync folder (iCloud Drive, Proton Drive) are flagged because recycling one deletes it
/// from the cloud as well; "Free up space" is the safe way to get the disk space back.
/// </summary>
public sealed class FileOwnership
{
    private readonly List<(string Root, string Owner)> _installed;
    private readonly Dictionary<string, string> _appNames;
    private readonly string[] _systemRoots;
    private readonly string[] _dataRoots;
    private readonly List<(string Root, string Provider)> _syncRoots;

    private static readonly HashSet<string> WindowsRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp",
    };

    public FileOwnership(IEnumerable<AppEntry> apps, IEnumerable<GameEntry> games, IEnumerable<SyncRoot>? syncRoots = null)
    {
        _syncRoots = (syncRoots ?? []).Select(r => (Norm(r.Path), r.Provider)).OrderByDescending(r => r.Item1.Length).ToList();
        _installed = apps
            .Where(a => a.InstallLocation is { } l && !DirectorySizer.IsTooBroadToMeasure(l))
            .Select(a => (Norm(a.InstallLocation!), a.Name))
            .Concat(games.Where(g => g.InstallPath is not null).Select(g => (Norm(g.InstallPath!), $"{g.Name} ({g.Source})")))
            .OrderByDescending(x => x.Item1.Length) // most specific folder wins
            .ToList();

        _appNames = new Dictionary<string, string>();
        foreach (var a in apps)
        {
            var key = LeftoverScanner.NormalizeName(a.Name);
            if (key.Length >= 3) _appNames.TryAdd(key, a.Name);
        }

        string F(Environment.SpecialFolder f) => Norm(Environment.GetFolderPath(f));
        _systemRoots =
        [
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86), F(Environment.SpecialFolder.CommonApplicationData),
        ];
        _dataRoots = [F(Environment.SpecialFolder.ApplicationData), F(Environment.SpecialFolder.LocalApplicationData)];
    }

    public Ownership Classify(string path)
    {
        var p = Norm(path);
        foreach (var (root, owner) in _installed)
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new(OwnerKind.InstalledApp, $"Part of {owner} — uninstall it instead");

        // pagefile.sys and friends at a drive root: D:\pagefile.sys was offered for Recycle in round 2.
        var name = Path.GetFileName(path);
        if (WindowsRootFiles.Contains(name) && Path.GetDirectoryName(Path.GetFullPath(path))?.Length <= 3)
            return new(OwnerKind.System, name.StartsWith("hiberfil", StringComparison.OrdinalIgnoreCase)
                ? "Windows hibernation file — turn hibernation off to remove it"
                : "Windows virtual memory — change it in System › Advanced settings");
        if (p.Contains(@"\WINDOWSAPPS\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\$RECYCLE.BIN\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\SYSTEM VOLUME INFORMATION\", StringComparison.OrdinalIgnoreCase))
            return new(OwnerKind.System, "Managed by Windows");
        // Cryptomator stores each file as one encrypted .c9r; deleting one silently loses a file in the vault.
        if (Path.GetExtension(path) is { } ext && (ext.Equals(".c9r", StringComparison.OrdinalIgnoreCase) || ext.Equals(".c9s", StringComparison.OrdinalIgnoreCase)))
            return new(OwnerKind.System, "Encrypted Cryptomator vault data — manage it inside the unlocked vault");
        foreach (var (root, provider) in _syncRoots)
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new(OwnerKind.Synced, $"In {provider} — Recycle deletes it from {provider} too; Free up space keeps it online-only", provider);
        if (p.Contains(@"\STEAMAPPS\", StringComparison.OrdinalIgnoreCase))
            return new(OwnerKind.InstalledApp, "Part of a Steam library — remove the game in Steam");
        foreach (var root in _systemRoots)
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return new(OwnerKind.System, "Managed by Windows or an installed app");

        foreach (var root in _dataRoots)
        {
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            // Same length as p (only the casing differs), so this slices the original-cased path.
            var full = Path.GetFullPath(path);
            var first = full.Length > root.Length ? full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
            if (first is null || first.Equals("Temp", StringComparison.OrdinalIgnoreCase)) break; // temp files are free to go
            if (_appNames.TryGetValue(LeftoverScanner.NormalizeName(first), out var app))
                return new(OwnerKind.AppData, $"Data for {app} — it may need this file");
            // Not every app registers itself (LTX Desktop doesn't), but AppData always belongs to something.
            return new(OwnerKind.AppData, $"App data ({first}) — whatever uses it may need it");
        }
        return new(OwnerKind.Free, "");
    }

    /// <summary>True when another program has the file open (an exclusive open fails). Checked when you
    /// click Recycle, not for every listed file.</summary>
    public static bool IsInUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\').ToUpperInvariant() + "\\";
}
