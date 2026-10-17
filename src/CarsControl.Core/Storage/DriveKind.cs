using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CarsControl.Core.Storage;

/// <summary>
/// Spinning disk or SSD, via IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceSeekPenaltyProperty). Works
/// unelevated: a volume handle opened with zero access rights may still be queried. Parallel directory
/// enumeration is great on NVMe but makes an HDD seek back and forth, which is why scanning A: (HDD-1)
/// with 4 threads "took forever".
/// </summary>
public static class DriveKind
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref STORAGE_PROPERTY_QUERY inBuf, int inSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR outBuf, int outSize, out int returned, IntPtr overlapped);

    private static readonly Dictionary<char, bool?> Cache = new();

    /// <summary>True for spinning disks, false for SSD/NVMe, null when Windows won't say.</summary>
    public static bool? IsRotational(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return null;
        var letter = char.ToUpperInvariant(root[0]);
        lock (Cache)
            if (Cache.TryGetValue(letter, out var cached)) return cached;

        bool? result = null;
        using (var h = CreateFile($@"\\.\{letter}:", 0, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero))
        {
            if (!h.IsInvalid)
            {
                var q = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
                if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref q, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                        out var d, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(), out _, IntPtr.Zero))
                    result = d.IncursSeekPenalty;
            }
        }
        lock (Cache) Cache[letter] = result;
        return result;
    }

    /// <summary>Directory-enumeration threads for a path: 1 on HDD (sequential is fastest), 6 on SSD/NVMe.</summary>
    public static int ScanParallelism(string path) => IsRotational(path) switch
    {
        true => 1,
        false => 6,
        null => 2,
    };
}
