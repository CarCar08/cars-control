using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CarsControl.Core.Health;

/// <summary>The NVMe "SMART / Health Information" log page (page 2, 512 bytes), the drive's own record of its health.</summary>
public sealed record NvmeHealth(
    byte CriticalWarning,
    int TempC,
    int AvailableSparePercent,
    int AvailableSpareThreshold,
    int PercentageUsed,
    long DataReadBytes,
    long DataWrittenBytes,
    long PowerCycles,
    long PowerOnHours,
    long UnsafeShutdowns,
    long MediaErrors,
    long ErrorLogEntries)
{
    /// <summary>Bits of the critical warning byte, in words.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            if ((CriticalWarning & 0x01) != 0) w.Add("its spare area is below the safe level");
            if ((CriticalWarning & 0x02) != 0) w.Add("it is too hot or too cold");
            if ((CriticalWarning & 0x04) != 0) w.Add("its reliability is degraded by errors");
            if ((CriticalWarning & 0x08) != 0) w.Add("it has switched itself to read-only");
            if ((CriticalWarning & 0x10) != 0) w.Add("its backup power has failed");
            return w;
        }
    }
}

/// <summary>The few ATA SMART attributes that matter, from a SATA drive's attribute table.</summary>
public sealed record AtaSmart(long? PowerOnHours, int? TempC, long? Reallocated, long? Pending, long? Uncorrectable, long? PowerCycles, long? UnsafeShutdowns, long? LbasWritten)
{
    public long? DataWrittenBytes => LbasWritten * 512;
}

public static class DriveSmartParser
{
    private const int Kelvin = 273;

    /// <summary>Parses the 512-byte NVMe health log. Null when the buffer is too short.</summary>
    public static NvmeHealth? ParseNvme(ReadOnlySpan<byte> log)
    {
        if (log.Length < 192) return null;
        var b = log.ToArray();
        long Counter(int offset) => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset, 8)), long.MaxValue);
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(1, 2));
        // Data units are thousands of 512-byte blocks.
        long Units(int offset) => Counter(offset) > long.MaxValue / 512_000 ? long.MaxValue : Counter(offset) * 512_000;
        return new NvmeHealth(
            b[0], kelvin == 0 ? 0 : kelvin - Kelvin, b[3], b[4], b[5],
            Units(32), Units(48), Counter(112), Counter(128), Counter(144), Counter(160), Counter(176));
    }

    /// <summary>Parses the 512-byte ATA SMART READ DATA block: 30 attributes of 12 bytes from offset 2.</summary>
    public static AtaSmart? ParseAta(ReadOnlySpan<byte> data)
    {
        if (data.Length < 362) return null;
        var raw = new Dictionary<int, long>();
        for (var i = 0; i < 30; i++)
        {
            var a = data.Slice(2 + i * 12, 12);
            if (a[0] == 0) continue;
            long value = 0;
            for (var b = 0; b < 6; b++) value |= (long)a[5 + b] << (8 * b);
            raw[a[0]] = value;
        }
        if (raw.Count == 0) return null;
        long? Get(int id, long mask = 0xFFFF_FFFF_FFFF) => raw.TryGetValue(id, out var v) ? v & mask : null;
        // Power-on hours: many drives use only the low 32 bits (the rest is minutes or vendor data); temperature is the low byte.
        var temp = Get(194, 0xFF) ?? Get(190, 0xFF);
        return new AtaSmart(Get(9, 0xFFFF_FFFF), temp is > 0 and < 120 ? (int)temp : null, Get(5), Get(197), Get(198), Get(12), Get(192) ?? Get(174), Get(241));
    }
}

/// <summary>
/// Reads the drive's own health data (admin only, so it runs in the helper): the NVMe health log through
/// IOCTL_STORAGE_QUERY_PROPERTY, or the ATA SMART table through SMART_RCV_DRIVE_DATA. Windows' own storage counters
/// leave most of these empty for NVMe drives.
/// </summary>
public static class DriveSmartReader
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    private static SafeFileHandle? Open(int diskNumber)
    {
        // Read/write access needs admin; a handle with no access rights is enough for some queries, so try that too.
        foreach (var access in new[] { GENERIC_READ | GENERIC_WRITE, 0u })
        {
            var h = CreateFileW($@"\\.\PhysicalDrive{diskNumber}", access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        return null;
    }

    public static NvmeHealth? ReadNvme(int diskNumber)
    {
        using var h = Open(diskNumber);
        if (h is null) return null;
        // STORAGE_PROPERTY_QUERY (PropertyId, QueryType) + STORAGE_PROTOCOL_SPECIFIC_DATA (10 DWORDs) + room for the 512-byte page.
        const int header = 8, protocolData = 40, page = 512;
        var buffer = new byte[header + protocolData + page];
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value);
        Put(0, 50);           // StorageDeviceProtocolSpecificProperty
        Put(4, 0);            // PropertyStandardQuery
        Put(8, 3);            // ProtocolTypeNvme
        Put(12, 2);           // NVMeDataTypeLogPage
        Put(16, 2);           // NVME_LOG_PAGE_HEALTH_INFO
        Put(20, 0);           // sub value
        Put(24, protocolData); // ProtocolDataOffset (from the start of STORAGE_PROTOCOL_SPECIFIC_DATA)
        Put(28, page);        // ProtocolDataLength
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < header + protocolData + 192)
            return null;
        // Output: STORAGE_PROTOCOL_DATA_DESCRIPTOR = Version, Size, then the same STORAGE_PROTOCOL_SPECIFIC_DATA.
        var offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(header + 16, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(header + 20, 4));
        var start = header + offset;
        if (offset <= 0 || length < 192 || start + Math.Min(length, page) > buffer.Length) return null;
        return DriveSmartParser.ParseNvme(buffer.AsSpan(start, Math.Min(length, page)));
    }

    public static AtaSmart? ReadAta(int diskNumber)
    {
        using var h = Open(diskNumber);
        if (h is null) return null;
        // SENDCMDINPARAMS: cBufferSize, IDEREGS (8 bytes), bDriveNumber, reserved…  → 32 bytes is plenty for the input.
        var input = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), 512);
        input[4] = 0xD0; // bFeaturesReg: SMART READ DATA
        input[5] = 1;    // bSectorCountReg
        input[6] = 1;    // bSectorNumberReg
        input[7] = 0x4F; // bCylLowReg
        input[8] = 0xC2; // bCylHighReg
        input[9] = 0xA0; // bDriveHeadReg
        input[10] = 0xB0; // bCommandReg: SMART
        input[12] = (byte)diskNumber;
        // SENDCMDOUTPARAMS: cBufferSize (4) + DRIVERSTATUS (12) + 512 bytes of data.
        var output = new byte[16 + 512];
        if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 16 + 362) return null;
        return DriveSmartParser.ParseAta(output.AsSpan(16, 512));
    }

    /// <summary>Only for messages: why a read failed.</summary>
    public static string LastError => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
