using CarsControl.Core.Models;

namespace CarsControl.Core.Storage;

public sealed record OldWindowsPart(string Path, long Bytes, string Advice, IReadOnlyList<string> StillUsedBy);

public sealed record OldWindowsInstall(string Drive, IReadOnlyList<OldWindowsPart> Parts, IReadOnlyList<string> KeepFolders)
{
    public long TotalBytes => Parts.Sum(p => p.Bytes);
}

/// <summary>
/// Finds a previous Windows installation left on a non-system drive (D: here: ~128 GB next to live
/// game libraries; it is also the usual cause of broken shortcuts). Report only: the folders are owned
/// by TrustedInstaller and some may still be referenced by installed apps.
/// </summary>
public static class OldWindowsReport
{
    public static IReadOnlyList<OldWindowsInstall> Find(IReadOnlyList<AppEntry> inventory)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var result = new List<OldWindowsInstall>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            if (string.Equals(root, system, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(root, @"Windows\System32\ntoskrnl.exe"))) continue;

            var parts = new List<OldWindowsPart>();
            foreach (var (folder, advice) in new[]
            {
                ("Windows", "The old operating system itself. Safe to remove once nothing below is still in use."),
                ("Users", "Old profiles — check Documents/Desktop/Downloads for anything you still want first."),
                ("Program Files", "Programs from the old install. Remove only folders no current app points into."),
                ("Program Files (x86)", "32-bit programs from the old install. Same rule as Program Files."),
                ("ProgramData", "Old shared app data."),
            })
            {
                var path = Path.Combine(root, folder);
                if (!Directory.Exists(path)) continue;
                var users = inventory
                    .Where(a => a.InstallLocation is { } l && l.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Name)
                    .Distinct()
                    .ToList();
                parts.Add(new OldWindowsPart(path, SafeMeasure(path), advice, users));
            }

            var keep = Directory.EnumerateDirectories(root)
                .Where(d => !File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) // "Documents and Settings" is a junction
                .Select(Path.GetFileName)
                .Where(n => n is not null && !new[] { "Windows", "Users", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs" }
                    .Contains(n, StringComparer.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList();
            result.Add(new OldWindowsInstall(root, parts, keep));
        }
        return result;
    }

    private static long SafeMeasure(string path)
    {
        try { return DirectorySizer.Measure(path); } catch { return 0; }
    }
}
