using System.Management;
using Microsoft.Win32;

namespace CarsControl.Core.Health;

public sealed record GpuInfo(string Name, string DriverVersion, long VramBytes, bool IsDiscrete);

public sealed record RamModule(long Bytes, int SpeedMHz, string Maker, string Part, string Slot);

public sealed record SystemSpecs(
    string ComputerName,
    string Cpu, int Cores, int Threads, int MaxMHz,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<RamModule> Ram,
    string Board, string BoardMaker,
    string BiosVersion, DateTime? BiosDate,
    string WindowsEdition, string WindowsVersion, string WindowsBuild,
    DateTime LastBoot)
{
    public long RamBytes => Ram.Sum(r => r.Bytes);

    public TimeSpan Uptime => DateTime.Now - LastBoot;

    /// <summary>"2 × 16 GB DDR5-6000".</summary>
    public string RamSummary => Ram.Count == 0 ? "unknown"
        : $"{Ram.Count} × {Ram[0].Bytes / (1024 * 1024 * 1024)} GB at {Ram[0].SpeedMHz} MHz ({RamBytes / (1024 * 1024 * 1024)} GB total)";
}

/// <summary>What this PC is, from WMI and the registry. Read-only, no admin.</summary>
public static class SystemSpecsReader
{
    /// <summary>Display adapters that aren't graphics cards: remote-desktop and streaming drivers, virtual monitors.</summary>
    private static readonly string[] NotARealGpu = ["Parsec", "Virtual Desktop", "Microsoft Basic", "Microsoft Remote", "Hyper-V", "Meta Virtual", "Citrix", "TeamViewer", "AnyDesk", "Remote Display", "Idd", "DisplayLink"];

    public static bool IsRealGpu(string name) => !NotARealGpu.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static SystemSpecs Read()
    {
        string One(string cls, string prop) => Query(cls).Select(o => o[prop]?.ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        var cpu = Query("Win32_Processor").FirstOrDefault();
        var os = Query("Win32_OperatingSystem").FirstOrDefault();
        var vram = DedicatedVideoMemory();
        var gpus = Query("Win32_VideoController")
            .Select(g => (Name: g["Name"]?.ToString() ?? "", Driver: g["DriverVersion"]?.ToString() ?? "", Ram: Convert.ToInt64(g["AdapterRAM"] ?? 0L)))
            .Where(g => g.Name.Length > 0 && IsRealGpu(g.Name))
            .Select(g => new GpuInfo(g.Name, g.Driver, vram.TryGetValue(g.Name, out var real) ? real : g.Ram, IsDiscrete(g.Name)))
            .ToList();
        var ram = Query("Win32_PhysicalMemory")
            .Select(m => new RamModule(Convert.ToInt64(m["Capacity"] ?? 0L), Convert.ToInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0),
                m["Manufacturer"]?.ToString()?.Trim() ?? "", m["PartNumber"]?.ToString()?.Trim() ?? "", m["DeviceLocator"]?.ToString()?.Trim() ?? ""))
            .ToList();
        var bios = Query("Win32_BIOS").FirstOrDefault();

        return new SystemSpecs(
            Environment.MachineName,
            cpu?["Name"]?.ToString()?.Trim() ?? "", Convert.ToInt32(cpu?["NumberOfCores"] ?? 0), Convert.ToInt32(cpu?["NumberOfLogicalProcessors"] ?? 0), Convert.ToInt32(cpu?["MaxClockSpeed"] ?? 0),
            gpus, ram,
            One("Win32_BaseBoard", "Product"), One("Win32_BaseBoard", "Manufacturer"),
            bios?["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "", ParseWmiDate(bios?["ReleaseDate"]?.ToString()),
            os?["Caption"]?.ToString()?.Replace("Microsoft ", "").Trim() ?? "Windows", RegistryDisplayVersion(), os?["BuildNumber"]?.ToString() ?? "",
            ParseWmiDate(os?["LastBootUpTime"]?.ToString()) ?? DateTime.Now);
    }

    private static bool IsDiscrete(string name) =>
        name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || name.Contains("RTX", StringComparison.OrdinalIgnoreCase) || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Arc ", StringComparison.OrdinalIgnoreCase) || (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !name.Contains("Graphics", StringComparison.OrdinalIgnoreCase));

    private static List<WmiRow> Query(string cls, string scope = @"root\cimv2")
    {
        try
        {
            return WmiQuery.Read(scope, $"SELECT * FROM {cls}");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>WMI's AdapterRAM is a 32-bit number (4095 MB for a 16 GB card); the driver's own registry key has the real size.</summary>
    private static Dictionary<string, long> DedicatedVideoMemory()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])) ?? [])
            {
                using var k = cls!.OpenSubKey(name);
                var desc = k?.GetValue("DriverDesc") as string;
                var size = k?.GetValue("HardwareInformation.qwMemorySize") switch { long l => l, ulong u => (long)u, int i => i, _ => 0L };
                if (desc is not null && size > 0) map[desc] = size;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Without it, the WMI figure is used.
        }
        return map;
    }

    private static string RegistryDisplayVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
    }

    /// <summary>WMI's 20260309190000.000000-300 form.</summary>
    internal static DateTime? ParseWmiDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
