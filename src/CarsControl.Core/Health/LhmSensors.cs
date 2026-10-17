using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace CarsControl.Core.Health;

/// <summary>One sensor from LibreHardwareMonitor. Type is its name for the sensor kind: Temperature, Fan, Voltage, Power, Load, Clock…</summary>
public sealed record SensorReading(string Hardware, string HardwareKind, string Name, string Type, double? Value, double? Min, double? Max);

/// <summary>
/// CPU, motherboard and fan sensors through LibreHardwareMonitor. It reads model-specific registers and the
/// motherboard's Super I/O chip, so it needs the PawnIO driver and administrator rights: it runs only inside the
/// elevated helper (the app never loads it). Without PawnIO the CPU temperature can't be read at all.
/// </summary>
public static class LhmSensors
{
    private static readonly object Gate = new();
    private static Computer? _computer;

    /// <summary>
    /// The computer stays open for the helper's lifetime: opening it scans every chip (slow), and only an open one keeps
    /// the minimum and maximum each sensor has seen since the helper started.
    /// </summary>
    public static IReadOnlyList<SensorReading> Read()
    {
        lock (Gate)
        {
            if (_computer is null)
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsMemoryEnabled = true,
                    // The GPU comes from NVML (no admin) and the drives from their own health logs.
                    IsGpuEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsBatteryEnabled = false,
                    IsPsuEnabled = false,
                };
                _computer.Open();
            }
            var list = new List<SensorReading>();
            foreach (var hw in _computer.Hardware) Collect(hw, list);
            return list;
        }
    }

    /// <summary>Called when the helper exits.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _computer?.Close();
            _computer = null;
        }
    }

    private static void Collect(IHardware hw, List<SensorReading> into)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
            into.Add(new SensorReading(hw.Name, hw.HardwareType.ToString(), s.Name, s.SensorType.ToString(), Finite(s.Value), Finite(s.Min), Finite(s.Max)));
        foreach (var sub in hw.SubHardware) Collect(sub, into);
    }

    private static double? Finite(float? v) => v is { } f && float.IsFinite(f) ? Math.Round(f, 2) : null;
}

/// <summary>PawnIO: the signed kernel driver LibreHardwareMonitor uses for hardware access (replacing WinRing0).</summary>
public static class PawnIo
{
    /// <summary>The release this build of C.A.R.S. installs. The same file and SHA-256 are in winget's manifest for namazso.PawnIO 2.2.0.</summary>
    public const string Version = "2.2.0";
    public const string DownloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    public const string Sha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    public const long Size = 3_410_960;

    /// <summary>Its installer's silent switches, from the winget manifest.</summary>
    public static readonly IReadOnlyList<string> SilentArgs = ["-install", "-silent"];

    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static string? InstalledVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>The handful of readings people actually look at, picked out of the 100-200 a board reports.</summary>
public sealed record SensorSummary(double? CpuTempC, double? CpuPowerW, double? CpuLoadPct, double? BoardTempC, IReadOnlyList<(string Name, double Rpm)> Fans);

public static class SensorRules
{
    /// <summary>Keep board-specific names supplied by LHM. Generic chip channels do not identify their wiring.</summary>
    public static string DisplayName(SensorReading s)
    {
        if (s.HardwareKind == "Cpu")
        {
            if (s.Type == "Temperature" && (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || s.Name == "CPU Package")) return $"CPU temperature · {s.Name}";
            if (s.Type == "Power" && s.Name == "Package") return "CPU package power";
            if (s.Type == "Load" && s.Name == "CPU Total") return "CPU usage";
        }
        if (s.HardwareKind == "SuperIO" && s.Type == "Fan" && s.Name.StartsWith("Fan #", StringComparison.Ordinal))
            return $"Fan header {s.Name[4..]} (connection unknown)";
        if (s.HardwareKind == "SuperIO" && s.Type == "Control" && s.Name.StartsWith("Fan #", StringComparison.Ordinal))
            return $"Fan header {s.Name[4..]} power setting";
        // AMD reports each core's multiplier and CPU cores' clocks under the same name ("Core #1"): say which is which.
        if (s.HardwareKind == "Cpu" && s.Type == "Factor" && s.Name.StartsWith("Core #", StringComparison.Ordinal))
            return $"{s.Name} multiplier";
        if (s.HardwareKind == "SuperIO" && s.Type == "Temperature" && s.Name.StartsWith("Temperature #", StringComparison.Ordinal))
            return $"Unassigned temperature probe {s.Name[12..]}";
        return s.Name;
    }

    /// <summary>False for readings that can't be real: an unconnected temperature probe (3 °C, 255 °C) or a fan header with nothing on it.</summary>
    public static bool IsPlausible(SensorReading s) => s.Value is { } v && s.Type switch
    {
        "Temperature" => v is >= 5 and <= 125,
        "Fan" => v is > 0 and < 10_000,
        "Voltage" => v > 0,
        _ => true,
    };

    public static SensorSummary Summarize(IReadOnlyList<SensorReading> sensors)
    {
        double? Pick(Func<SensorReading, bool> where, params string[] names)
        {
            foreach (var n in names)
                if (sensors.FirstOrDefault(s => where(s) && IsPlausible(s) && s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit.Value;
            return null;
        }
        bool Cpu(SensorReading s, string type) => s.HardwareKind == "Cpu" && s.Type == type;
        // AMD reports "Core (Tctl/Tdie)", Intel "CPU Package"; the motherboard chip also has a "CPU" probe as a fallback.
        var cpuTemp = Pick(s => Cpu(s, "Temperature"), "Tctl", "Tdie", "Package", "Core Max", "Core Average")
            ?? Pick(s => s.HardwareKind != "Cpu" && s.Type == "Temperature", "CPU");
        var cpuPower = Pick(s => Cpu(s, "Power"), "Package", "CPU Cores");
        var cpuLoad = Pick(s => Cpu(s, "Load"), "Total");
        var board = Pick(s => s.HardwareKind is "Motherboard" or "SuperIO" && s.Type == "Temperature", "System", "Motherboard", "SYSTIN", "Temperature #1");
        var fans = sensors.Where(s => s.Type == "Fan" && IsPlausible(s)).Select(s => (DisplayName(s), s.Value!.Value)).ToList();
        return new SensorSummary(cpuTemp, cpuPower, cpuLoad, board, fans);
    }
}
