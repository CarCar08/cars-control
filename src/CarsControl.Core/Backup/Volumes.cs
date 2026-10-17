using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Backup;

public sealed record VolumeInfo(string Root, string Label, string Serial, DriveType Type, long FreeBytes, long TotalBytes, bool IsUsb)
{
    public string Display => $"{Root.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(Label) ? "(no name)" : Label)}";
}

/// <summary>
/// Recognises a drive by its volume ID, not its letter (a USB stick gets a different letter every time), and says which
/// physical disk a drive letter sits on (so a backup that lands on the same disk as the files can be flagged).
/// </summary>
public static class Volumes
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder label, int labelSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, int fsSize);

    public static string? SerialOf(string root)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        return GetVolumeInformationW(root, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity) ? serial.ToString("X8") : null;
    }

    /// <summary>The volume IDs of every ready drive: cheap enough to call once a minute (no WMI), for noticing a drive being plugged in.</summary>
    public static IReadOnlyList<string> Serials()
    {
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady && SerialOf(d.Name) is { } s) list.Add(s);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while listing.
            }
        }
        return list;
    }

    /// <summary>Every ready local or removable drive, with whether it's a USB one (asks WMI, so not for tight loops).</summary>
    public static IReadOnlyList<VolumeInfo> List()
    {
        var usb = UsbLetters();
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var serial = SerialOf(d.Name);
                if (serial is null) continue;
                list.Add(new VolumeInfo(d.Name, d.VolumeLabel, serial, d.DriveType, d.AvailableFreeSpace, d.TotalSize, d.DriveType == DriveType.Removable || usb.Contains(char.ToUpperInvariant(d.Name[0]))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while listing.
            }
        }
        return list;
    }

    /// <summary>The drive whose volume ID is <paramref name="serial"/>, if it's plugged in.</summary>
    public static VolumeInfo? Find(string serial) => List().FirstOrDefault(v => v.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase));

    private static HashSet<char> UsbLetters()
    {
        var set = new HashSet<char>();
        try
        {
            using var disks = new ManagementObjectSearcher(@"root\cimv2", "SELECT DeviceID FROM Win32_DiskDrive WHERE InterfaceType='USB'");
            foreach (var disk in disks.Get().Cast<ManagementObject>())
            {
                var id = disk["DeviceID"]?.ToString()?.Replace("\\", "\\\\");
                using var parts = new ManagementObjectSearcher(@"root\cimv2", $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{id}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (var part in parts.Get().Cast<ManagementObject>())
                {
                    using var logical = new ManagementObjectSearcher(@"root\cimv2", $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                    foreach (var l in logical.Get().Cast<ManagementObject>())
                        if (l["DeviceID"]?.ToString() is { Length: 2 } device) set.Add(char.ToUpperInvariant(device[0]));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // Without WMI, only drives Windows calls removable count as USB.
        }
        return set;
    }

    /// <summary>"Disk #1, Partition #0" → 1. Null when it can't be told.</summary>
    public static int? PhysicalDiskOf(char letter)
    {
        try
        {
            using var parts = new ManagementObjectSearcher(@"root\cimv2", $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{char.ToUpperInvariant(letter)}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var p in parts.Get().Cast<ManagementObject>())
                if (ParseDiskNumber(p["DeviceID"]?.ToString()) is { } n) return n;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            // Unknown.
        }
        return null;
    }

    public static int? ParseDiskNumber(string? partitionDeviceId) =>
        partitionDeviceId is not null && Regex.Match(partitionDeviceId, @"Disk\s*#(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>True when the folders being backed up and the backup itself are on one physical disk: a dying disk takes both.</summary>
    public static bool SameDisk(string pathA, string pathB)
    {
        if (pathA.Length < 2 || pathB.Length < 2 || pathA[1] != ':' || pathB[1] != ':') return false;
        if (char.ToUpperInvariant(pathA[0]) == char.ToUpperInvariant(pathB[0])) return true;
        return PhysicalDiskOf(pathA[0]) is { } a && PhysicalDiskOf(pathB[0]) is { } b && a == b;
    }
}
