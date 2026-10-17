using System.Management;

namespace CarsControl.Core.Health;

public enum DriveKind { Unknown, HardDisk, Ssd, Nvme, Usb }

/// <summary>What Windows knows about one physical drive. The counters (temperature, wear, hours, errors) are
/// only readable with admin rights, so they're null until the admin helper has read them.</summary>
public sealed record DriveReading(
    string Name,
    DriveKind Kind,
    long SizeBytes,
    string Health,
    IReadOnlyList<string> Letters,
    int? TempC = null,
    int? WearPercent = null,
    long? PowerOnHours = null,
    long? ReadErrorsUncorrected = null,
    long? WriteErrorsUncorrected = null,
    int? TempLimitC = null,
    long? DataWrittenBytes = null,
    long? UnsafeShutdowns = null,
    long? PowerCycles = null,
    long? MediaErrors = null,
    int? AvailableSparePercent = null,
    int? AvailableSpareThreshold = null,
    long? ReallocatedSectors = null,
    long? PendingSectors = null,
    IReadOnlyList<string>? DriveWarnings = null,
    string? CountersFrom = null)
{
    public bool HasCounters => TempC is not null || WearPercent is not null || PowerOnHours is not null || CountersFrom is not null;

    /// <summary>The admin-only numbers of an earlier reading, kept when a plain (non-admin) refresh comes in.</summary>
    public DriveReading WithCountersOf(DriveReading old) => this with
    {
        TempC = old.TempC, WearPercent = old.WearPercent, PowerOnHours = old.PowerOnHours, ReadErrorsUncorrected = old.ReadErrorsUncorrected,
        WriteErrorsUncorrected = old.WriteErrorsUncorrected, TempLimitC = old.TempLimitC, DataWrittenBytes = old.DataWrittenBytes,
        UnsafeShutdowns = old.UnsafeShutdowns, PowerCycles = old.PowerCycles, MediaErrors = old.MediaErrors, AvailableSparePercent = old.AvailableSparePercent,
        AvailableSpareThreshold = old.AvailableSpareThreshold, ReallocatedSectors = old.ReallocatedSectors, PendingSectors = old.PendingSectors,
        DriveWarnings = old.DriveWarnings, CountersFrom = old.CountersFrom,
    };

    /// <summary>Merges what the drive itself reports (NVMe health log) over Windows' counters, which leave most of it empty for NVMe.</summary>
    public DriveReading With(NvmeHealth n) => this with
    {
        TempC = n.TempC is > 0 and < 150 ? n.TempC : TempC,
        WearPercent = n.PercentageUsed,
        PowerOnHours = n.PowerOnHours,
        DataWrittenBytes = n.DataWrittenBytes,
        UnsafeShutdowns = n.UnsafeShutdowns,
        PowerCycles = n.PowerCycles,
        MediaErrors = n.MediaErrors,
        AvailableSparePercent = n.AvailableSparePercent,
        AvailableSpareThreshold = n.AvailableSpareThreshold,
        DriveWarnings = n.Warnings,
        CountersFrom = "the drive's own health log",
    };

    /// <summary>Merges a SATA drive's SMART attributes over Windows' counters.</summary>
    public DriveReading With(AtaSmart a) => this with
    {
        TempC = a.TempC ?? TempC,
        PowerOnHours = a.PowerOnHours ?? PowerOnHours,
        DataWrittenBytes = a.DataWrittenBytes ?? DataWrittenBytes,
        UnsafeShutdowns = a.UnsafeShutdowns ?? UnsafeShutdowns,
        PowerCycles = a.PowerCycles ?? PowerCycles,
        ReallocatedSectors = a.Reallocated,
        PendingSectors = (a.Pending ?? 0) + (a.Uncorrectable ?? 0) is > 0 and var p ? p : a.Pending,
        CountersFrom = "the drive's SMART data",
    };
    public string LettersText => Letters.Count == 0 ? "no drive letter" : string.Join(", ", Letters.Select(l => l + ":"));
}

public enum DriveLevel { Good, Watch, Bad }

public sealed record DriveVerdict(DriveLevel Level, IReadOnlyList<string> Notes)
{
    public string Summary => Level switch
    {
        DriveLevel.Bad => "Needs attention",
        DriveLevel.Watch => "Keep an eye on it",
        _ => "Healthy",
    };
}

/// <summary>Turns the raw numbers into plain-language findings.</summary>
public static class DriveHealthRules
{
    public static DriveVerdict Evaluate(DriveReading d)
    {
        var level = DriveLevel.Good;
        var notes = new List<string>();
        void Raise(DriveLevel l, string note)
        {
            notes.Add(note);
            if (l > level) level = l;
        }

        switch (d.Health.ToLowerInvariant())
        {
            case "healthy" or "ok":
                break;
            case "warning":
                Raise(DriveLevel.Watch, "Windows reports this drive as \"Warning\": it has seen errors it could correct. Back up what matters and check its vendor's tool.");
                break;
            case "unhealthy":
                Raise(DriveLevel.Bad, "Windows reports this drive as \"Unhealthy\". Back it up now and plan to replace it.");
                break;
            default:
                Raise(DriveLevel.Watch, $"Windows can't tell this drive's health (\"{d.Health}\").");
                break;
        }

        if (d.TempC is { } t)
        {
            var (warn, bad) = d.Kind == DriveKind.HardDisk ? (50, 60) : (70, 80);
            if (t >= bad) Raise(DriveLevel.Bad, $"It's running at {t} °C, which is too hot for it. Check airflow and any heatsink.");
            else if (t >= warn) Raise(DriveLevel.Watch, $"It's warm ({t} °C). Fine for a short while, not for hours.");
        }

        if (d.WearPercent is { } w && d.Kind is DriveKind.Ssd or DriveKind.Nvme)
        {
            if (w >= 95) Raise(DriveLevel.Bad, $"{w}% of its rated write endurance is used up. Replace it soon.");
            else if (w >= 80) Raise(DriveLevel.Watch, $"{w}% of its rated write endurance is used. It still works; plan a replacement.");
        }

        foreach (var w2 in d.DriveWarnings ?? []) Raise(DriveLevel.Bad, "The drive itself warns that " + w2 + ". Back it up now.");
        if (d.AvailableSparePercent is { } spare && d.AvailableSpareThreshold is { } floor && floor > 0 && spare <= floor + 10)
            Raise(spare <= floor ? DriveLevel.Bad : DriveLevel.Watch, $"Its spare area is down to {spare}% (the drive's own limit is {floor}%).");
        if (d.MediaErrors is > 0) Raise(DriveLevel.Watch, $"It has logged {d.MediaErrors} data errors it couldn't fix on its own. Keep a backup current.");
        if (d.ReallocatedSectors is > 0) Raise(d.ReallocatedSectors > 50 ? DriveLevel.Bad : DriveLevel.Watch, $"{d.ReallocatedSectors} bad sectors have been swapped for spares. A growing number means the drive is wearing out.");
        if (d.PendingSectors is > 0) Raise(DriveLevel.Bad, $"{d.PendingSectors} sectors couldn't be read and are waiting to be replaced. Back it up now.");

        if (d.ReadErrorsUncorrected is > 0 || d.WriteErrorsUncorrected is > 0)
            Raise(DriveLevel.Bad, $"It has {(d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0)} read/write errors the drive couldn't correct. Back it up now.");

        return new DriveVerdict(level, notes);
    }
}

public static class DriveHealthReader
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    /// <summary>Drives and their health status (any account). <paramref name="includeCounters"/> adds
    /// temperature, wear, hours and error counts, which Windows only gives to administrators.</summary>
    public static IReadOnlyList<DriveReading> Read(bool includeCounters)
    {
        var letters = Letters();
        var result = new List<DriveReading>();
        foreach (var disk in Query("MSFT_PhysicalDisk"))
        {
            var id = disk["DeviceId"]?.ToString() ?? "";
            var name = disk["FriendlyName"]?.ToString()?.Trim() ?? "Drive";
            var kind = KindOf(Convert.ToInt32(disk["BusType"] ?? 0), Convert.ToInt32(disk["MediaType"] ?? 0));
            var reading = new DriveReading(name, kind, Convert.ToInt64(disk["Size"] ?? 0L), HealthName(Convert.ToInt32(disk["HealthStatus"] ?? 5)),
                letters.TryGetValue(id, out var l) ? l : []);
            // NVMe drives share their own health log without admin; Windows' counters and SATA SMART need the helper.
            if (includeCounters) reading = WithDriveData(WithCounters(reading, disk), id);
            else if (kind == DriveKind.Nvme) reading = WithDriveData(reading, id);
            result.Add(reading);
        }
        return result.OrderBy(r => r.LettersText, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DriveReading WithCounters(DriveReading d, WmiRow disk)
    {
        try
        {
            var objectId = disk["ObjectId"]?.ToString()?.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var c = WmiQuery.Read(Scope,
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{objectId}\"}} WHERE AssocClass = MSFT_PhysicalDiskToStorageReliabilityCounter").FirstOrDefault();
            if (c is null) return d;
            int? Int(string p) => c[p] is null ? null : Convert.ToInt32(c[p]);
            long? Long(string p) => c[p] is null ? null : Convert.ToInt64(c[p]);
            // 0 means "not reported" for the temperature of some drives.
            int? Positive(string p) => Int(p) is > 0 and var v ? v : (int?)null;
            return d with
            {
                TempC = Positive("Temperature"), TempLimitC = Positive("TemperatureMax"),
                // USB sticks and many USB enclosures answer 0 for wear without measuring anything.
                WearPercent = d.Kind == DriveKind.Usb && Int("Wear") is 0 ? null : Int("Wear"), PowerOnHours = Long("PowerOnHours"),
                ReadErrorsUncorrected = Long("ReadErrorsUncorrected"), WriteErrorsUncorrected = Long("WriteErrorsUncorrected"),
            };
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return d;
        }
    }

    /// <summary>The drive's own health data on top of Windows' counters (admin only; a drive that doesn't answer keeps what Windows said).</summary>
    private static DriveReading WithDriveData(DriveReading d, string deviceId)
    {
        if (!int.TryParse(deviceId, out var disk)) return d;
        try
        {
            if (d.Kind == DriveKind.Nvme && DriveSmartReader.ReadNvme(disk) is { } n) return d.With(n);
            if (d.Kind is DriveKind.HardDisk or DriveKind.Ssd or DriveKind.Unknown && DriveSmartReader.ReadAta(disk) is { } a) return d.With(a);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Windows' counters stand.
        }
        return d;
    }

    /// <summary>Physical disk DeviceId → its drive letters (via the partitions on the disk).</summary>
    private static Dictionary<string, string[]> Letters()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var p in Query("MSFT_Partition"))
        {
            var letter = p["DriveLetter"] is { } dl ? Convert.ToChar(dl) : '\0';
            if (letter is < 'A' or > 'Z') continue;
            var disk = p["DiskNumber"]?.ToString();
            if (disk is null) continue;
            if (!map.TryGetValue(disk, out var list)) map[disk] = list = [];
            list.Add(letter.ToString());
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.Order().ToArray());
    }

    private static List<WmiRow> Query(string cls)
    {
        try
        {
            return WmiQuery.Read(Scope, $"SELECT * FROM {cls}");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>MSFT_PhysicalDisk.BusType: 7 USB, 11 SATA, 17 NVMe…; MediaType: 3 HDD, 4 SSD.</summary>
    public static DriveKind KindOf(int busType, int mediaType) =>
        busType == 17 ? DriveKind.Nvme
        : busType == 7 ? DriveKind.Usb
        : mediaType == 3 ? DriveKind.HardDisk
        : mediaType == 4 ? DriveKind.Ssd
        : DriveKind.Unknown;

    public static string HealthName(int status) => status switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };
}
