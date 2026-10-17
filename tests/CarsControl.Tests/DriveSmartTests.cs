using System.Buffers.Binary;
using CarsControl.Core.Health;

namespace CarsControl.Tests;

public class DriveSmartTests
{
    private static byte[] NvmeLog(ushort kelvin = 324, byte spare = 100, byte threshold = 10, byte used = 3, ulong unitsWritten = 24_000_000,
        ulong cycles = 812, ulong hours = 9_431, ulong unsafeShutdowns = 41, ulong media = 0, byte critical = 0)
    {
        var b = new byte[512];
        b[0] = critical;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), kelvin);
        b[3] = spare;
        b[4] = threshold;
        b[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), unitsWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(112), cycles);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(128), hours);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(144), unsafeShutdowns);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(160), media);
        return b;
    }

    [Fact]
    public void The_nvme_health_log_is_read_field_by_field()
    {
        var n = DriveSmartParser.ParseNvme(NvmeLog())!;
        Assert.Equal(51, n.TempC);
        Assert.Equal(100, n.AvailableSparePercent);
        Assert.Equal(10, n.AvailableSpareThreshold);
        Assert.Equal(3, n.PercentageUsed);
        Assert.Equal(24_000_000L * 512_000, n.DataWrittenBytes); // ~12.3 TB
        Assert.Equal(812, n.PowerCycles);
        Assert.Equal(9_431, n.PowerOnHours);
        Assert.Equal(41, n.UnsafeShutdowns);
        Assert.Empty(n.Warnings);
    }

    [Fact]
    public void Critical_warning_bits_become_sentences_and_a_bad_verdict()
    {
        var n = DriveSmartParser.ParseNvme(NvmeLog(critical: 0x01 | 0x08))!;
        Assert.Equal(2, n.Warnings.Count);
        var d = new DriveReading("T-FORCE", DriveKind.Nvme, 2_000_000_000_000, "Healthy", ["C"]).With(n);
        var v = DriveHealthRules.Evaluate(d);
        Assert.Equal(DriveLevel.Bad, v.Level);
        Assert.Contains(v.Notes, s => s.Contains("read-only"));
    }

    [Fact]
    public void A_short_buffer_is_not_a_reading() => Assert.Null(DriveSmartParser.ParseNvme(new byte[100]));

    [Fact]
    public void Nvme_data_fills_what_windows_left_empty_and_keeps_the_limit()
    {
        var windows = new DriveReading("Sabrent", DriveKind.Nvme, 1_000_000_000_000, "Healthy", ["D"], TempC: 32, WearPercent: 0, TempLimitC: 90);
        var d = windows.With(DriveSmartParser.ParseNvme(NvmeLog(kelvin: 305, used: 1))!);
        Assert.Equal(32, d.TempC);
        Assert.Equal(1, d.WearPercent);
        Assert.Equal(9_431, d.PowerOnHours);
        Assert.Equal(90, d.TempLimitC);
        Assert.Equal("the drive's own health log", d.CountersFrom);
        Assert.Equal(DriveLevel.Good, DriveHealthRules.Evaluate(d).Level);
    }

    [Fact]
    public void Low_spare_area_is_flagged_before_it_runs_out()
    {
        var d = new DriveReading("X", DriveKind.Nvme, 1, "Healthy", []).With(DriveSmartParser.ParseNvme(NvmeLog(spare: 15, threshold: 10))!);
        Assert.Equal(DriveLevel.Watch, DriveHealthRules.Evaluate(d).Level);
        var worse = new DriveReading("X", DriveKind.Nvme, 1, "Healthy", []).With(DriveSmartParser.ParseNvme(NvmeLog(spare: 8, threshold: 10))!);
        Assert.Equal(DriveLevel.Bad, DriveHealthRules.Evaluate(worse).Level);
    }

    private static byte[] AtaTable(params (byte Id, long Raw)[] attrs)
    {
        var b = new byte[512];
        for (var i = 0; i < attrs.Length; i++)
        {
            var o = 2 + i * 12;
            b[o] = attrs[i].Id;
            for (var k = 0; k < 6; k++) b[o + 5 + k] = (byte)(attrs[i].Raw >> (8 * k));
        }
        return b;
    }

    [Fact]
    public void Ata_smart_attributes_are_read()
    {
        // Hours with vendor data in the high bytes, temperature with min/max packed above the low byte.
        var a = DriveSmartParser.ParseAta(AtaTable((9, 0x0000_1234_0000_4083), (194, 0x0000_2D_0014_1E), (5, 3), (197, 1), (198, 0), (12, 2210), (241, 1_000_000)))!;
        Assert.Equal(0x4083, a.PowerOnHours);
        Assert.Equal(0x1E, a.TempC);
        Assert.Equal(3, a.Reallocated);
        Assert.Equal(1, a.Pending);
        Assert.Equal(2210, a.PowerCycles);
        Assert.Equal(512_000_000, a.DataWrittenBytes);
        var d = new DriveReading("WDC", DriveKind.HardDisk, 1, "Healthy", ["A"]).With(a);
        Assert.Equal(DriveLevel.Bad, DriveHealthRules.Evaluate(d).Level); // a pending sector
    }

    [Fact]
    public void An_empty_smart_table_is_not_a_reading() => Assert.Null(DriveSmartParser.ParseAta(new byte[512]));

    [Fact]
    public void Plain_refreshes_keep_the_admin_numbers()
    {
        var old = new DriveReading("X", DriveKind.Nvme, 1, "Healthy", ["C"]).With(DriveSmartParser.ParseNvme(NvmeLog())!);
        var fresh = new DriveReading("X", DriveKind.Nvme, 1, "Healthy", ["C"]).WithCountersOf(old);
        Assert.Equal(old.PowerOnHours, fresh.PowerOnHours);
        Assert.Equal(old.UnsafeShutdowns, fresh.UnsafeShutdowns);
        Assert.Equal(old.CountersFrom, fresh.CountersFrom);
    }
}
