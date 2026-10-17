using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CarsControl.Core.Storage;

public sealed record SyncRoot(string Path, string Provider);

/// <summary>
/// Folders a cloud provider keeps in sync (iCloud Drive, Proton Drive, OneDrive…), read from
/// HKLM\…\Explorer\SyncRootManager for the current user. On this PC: A:\Windows (Backup)\iCloudDrive and
/// A:\Windows (Backup)\Proton Drive\My files.
/// Deleting a synced file deletes it from the cloud too. "Free up space" (unpin) keeps it in the cloud
/// and only drops the local copy, which is what you want for a 20 GB movie on a full drive.
/// </summary>
public static class CloudSync
{
    private const int Pinned = 0x80000, Unpinned = 0x100000;

    // Attributes SetFileAttributes accepts alongside the pin state; the rest belong to the provider.
    private const int Settable = 0x1 | 0x2 | 0x4 | 0x20 | 0x100 | 0x2000; // ReadOnly Hidden System Archive Temporary NotContentIndexed

    /// <summary>Set by the app's screenshot (demo) mode: no cloud folders are reported, so none of this PC's paths appear.</summary>
    public static bool HiddenForScreenshots { get; set; }

    public static IReadOnlyList<SyncRoot> Roots()
    {
        if (HiddenForScreenshots) return [];
        var list = new List<SyncRoot>();
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            using var manager = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (manager is null || sid is null) return list;
            foreach (var id in manager.GetSubKeyNames())
            {
                using var roots = manager.OpenSubKey($@"{id}\UserSyncRoots");
                if (roots?.GetValue(sid) is string path && path.Length > 3)
                    list.Add(new SyncRoot(path.TrimEnd('\\'), ProviderName(id)));
            }
        }
        catch
        {
            // No sync roots: nothing gets the cloud treatment.
        }
        return list;
    }

    /// <summary>"iCloudDrive!S-1-5-…!Personal" → "iCloud Drive".</summary>
    public static string ProviderName(string syncRootId) => syncRootId.Split('!')[0] switch
    {
        "iCloudDrive" => "iCloud Drive",
        "ProtonDrive" => "Proton Drive",
        "OneDrive" => "OneDrive",
        "GoogleDrive" or "DriveFS" => "Google Drive",
        var p => p,
    };

    /// <summary>Asks the provider to make the file online-only, exactly like Explorer's "Free up space"
    /// (attrib -P +U). Nothing is deleted: the provider drops the local copy once the cloud has it, and
    /// refuses if it doesn't. Returns false for files that aren't cloud files.</summary>
    public static bool RequestFreeUp(string path)
    {
        var a = (int)File.GetAttributes(path);
        if ((a & (int)FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            File.SetAttributes(path, (FileAttributes)((a & Settable) | Unpinned));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall back to the tool Explorer documents for this.
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "attrib.exe")) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add("+U");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        return ((int)File.GetAttributes(path) & Unpinned) != 0;
    }

    /// <summary>Space the file actually takes on disk (0 once the provider has freed it).</summary>
    public static long OnDiskBytes(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        return low == uint.MaxValue && Marshal.GetLastWin32Error() != 0 ? -1 : ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}
