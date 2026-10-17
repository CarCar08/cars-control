using System.IO.Enumeration;

namespace CarsControl.Core.Storage;

/// <summary>Fast recursive folder size. Skips links (junctions like an admin profile's Documents\Foo → another drive
/// would otherwise double-count or loop) and cloud placeholders (online-only iCloud / Proton Drive /
/// OneDrive files that aren't on disk). Cloud-synced folders and downloaded cloud files are reparse
/// points too, but they hold real data, so they are counted: skipping every reparse point is what hid
/// ~500 GB of downloaded iCloud and Proton Drive files on A: in round 2.</summary>
public static class DirectorySizer
{
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;

    public static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static long Measure(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var e = new FileSystemEnumerable<long>(path, (ref FileSystemEntry entry) => entry.Length, Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && !IsPlaceholder(entry.Attributes),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0 || !IsLink(entry.ToFullPath()),
        };
        foreach (var len in e)
        {
            total += len;
            if ((total & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
        }
        return total;
    }

    /// <summary>Junction, symlink or volume mount point. Other reparse points (cloud-sync folders and files)
    /// return no link target and are real data. An unreadable reparse point is treated as a link.</summary>
    public static bool IsLink(string fullPath)
    {
        try
        {
            return new DirectoryInfo(fullPath).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Online-only cloud file: listed with its full size but not stored on this disk.</summary>
    public static bool IsPlaceholder(FileAttributes a) =>
        ((int)a & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN)) != 0 || a.HasFlag(FileAttributes.Offline);

    /// <summary>True when measuring this folder would be meaningless or huge (a drive root, a shared
    /// Program Files root, a whole profile), which happens with sloppy InstallLocation values.</summary>
    public static bool IsTooBroadToMeasure(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length <= 3) return true;
        string?[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        return broad.Any(b => !string.IsNullOrEmpty(b) && string.Equals(b.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
    }
}
