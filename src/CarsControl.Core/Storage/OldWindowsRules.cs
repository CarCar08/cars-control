namespace CarsControl.Core.Storage;

/// <summary>
/// What may be removed from a leftover Windows installation, and what must stop the removal. The rules are strict on
/// purpose: only the five top-level folders a Windows setup creates, only on a drive that isn't the running Windows or a
/// boot drive, only when the drive really holds an offline Windows (its registry hive is there), and never a folder an
/// installed program still points into.
/// </summary>
public static class OldWindowsRules
{
    public static readonly IReadOnlyList<string> AllowedFolders = ["Windows", "Users", "Program Files", "Program Files (x86)", "ProgramData"];

    /// <summary>Why this folder can't be removed, or null when it can. <paramref name="exists"/> is File/Directory.Exists (injectable for tests).</summary>
    public static string? Validate(string folder, string runningWindowsRoot, Func<string, bool> fileExists, Func<string, bool> isLink)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.Contains('\0')) return "That isn't a full folder path.";
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        var root = Path.GetPathRoot(full)!;
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !parent.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "Only the top-level folders of the old installation can be removed, not anything deeper.";
        var name = Path.GetFileName(full);
        if (!AllowedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"“{name}” isn't one of the folders a Windows installation creates.";
        if (root.TrimEnd('\\').Equals(runningWindowsRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "That's the Windows you're running.";
        if (fileExists(Path.Combine(root, "bootmgr")) || fileExists(Path.Combine(root, @"EFI\Microsoft\Boot\bootmgfw.efi"))) return "That drive holds a boot manager, so something may still start from it.";
        if (isLink(full)) return "That's a link, not a folder.";
        if (!fileExists(Path.Combine(root, @"Windows\System32\config\SYSTEM"))) return "That drive doesn't hold an old Windows installation (its registry hive isn't there).";
        return null;
    }

    /// <summary>Installed programs (their registered install folder, icon or uninstaller) that live inside the folder to remove.</summary>
    public static IReadOnlyList<string> StillUsedBy(string folder, IEnumerable<(string Name, string? Path)> registered)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return registered.Where(r => r.Path is not null && Clean(r.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    /// <summary>Links (junctions, symbolic links) on the running system whose target is inside the folder: removing the folder would break them.</summary>
    public static IReadOnlyList<string> LinkedFrom(string folder, IEnumerable<(string Link, string? Target)> links)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return links.Where(l => l.Target is not null && (Path.GetFullPath(l.Target).TrimEnd('\\') + "\\").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Link).Take(5).ToList();
    }

    /// <summary>Registry values hold things like <c>"D:\Program Files\X\x.exe",0</c> or <c>MsiExec.exe /X{GUID}</c>.</summary>
    internal static string Clean(string value)
    {
        var v = value.Trim().Trim('"');
        var comma = v.LastIndexOf(',');
        if (comma > 3 && int.TryParse(v[(comma + 1)..], out _)) v = v[..comma];
        var quote = v.IndexOf('"');
        return quote > 0 ? v[..quote] : v;
    }
}
