using System.Management;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Health;

public enum ProcessRole
{
    /// <summary>One of your apps: it can be ended.</summary>
    Normal,
    /// <summary>Salad's miner and its helpers: the PC's earnings. Never ended from here.</summary>
    Salad,
    /// <summary>Part of Windows or its security: ending it could crash the PC.</summary>
    Windows,
    /// <summary>C.A.R.S. itself.</summary>
    Self,
    /// <summary>Another account's, or one Windows won't say anything about (anti-cheat, protected): not ended from here.</summary>
    Protected,
}

public sealed record ProcessUse(int Pid, string Name, double CpuPercent, long RamBytes, long DiskBytesPerSec, double GpuPercent, string? Path, ProcessRole Role)
{
    public bool CanEnd => Role == ProcessRole.Normal;

    public string RoleLabel => Role switch
    {
        ProcessRole.Salad => "Salad: your earnings",
        ProcessRole.Windows => "Windows",
        ProcessRole.Self => "C.A.R.S. Control",
        ProcessRole.Protected => "Protected",
        _ => "",
    };
}

public static class ProcessRules
{
    private static readonly HashSet<string> SaladNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "salad", "saladbowl", "saladbowlservice", "saladbowl.service", "salad.bowl.service", "xmrig", "t-rex", "trex", "nbminer", "lolminer", "gminer", "phoenixminer", "bzminer", "teamredminer",
    };

    /// <summary>The virtual machine behind WSL: on this PC it is where Salad's workloads run.</summary>
    private static readonly HashSet<string> WslNames = new(StringComparer.OrdinalIgnoreCase) { "vmmem", "vmmemwsl", "vmwp", "wsl", "wslhost", "wslservice", "wslrelay", "wslg" };

    private static readonly HashSet<string> WindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "fontdrvhost", "dwm", "sihost", "ctfmon",
        "explorer", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "audiodg", "msmpeng", "nissrv", "securityhealthservice",
        "securityhealthsystray", "smartscreen", "wudfhost", "spoolsv", "conhost", "lsaiso", "vmcompute", "vmms",
    };

    public static ProcessRole Classify(string name, string? path, bool saladRunning, string? selfExe = null, bool ownedByYou = true)
    {
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (selfExe is not null && path is not null && string.Equals(path, selfExe, StringComparison.OrdinalIgnoreCase)) return ProcessRole.Self;
        if (bare.StartsWith("CarsControl", StringComparison.OrdinalIgnoreCase)) return ProcessRole.Self;
        if (SaladNames.Contains(bare)) return ProcessRole.Salad;
        if (path is not null && path.Contains(@"\Salad\", StringComparison.OrdinalIgnoreCase)) return ProcessRole.Salad;
        if (saladRunning && WslNames.Contains(bare)) return ProcessRole.Salad;
        if (WindowsNames.Contains(bare)) return ProcessRole.Windows;
        if (path is not null)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase)) return ProcessRole.Windows;
        }
        // No path, or another account's process: Windows won't let us manage it, and neither should we.
        return path is null || !ownedByYou ? ProcessRole.Protected : ProcessRole.Normal;
    }

    /// <summary>"chrome#12" → "chrome" (performance counters number duplicate names).</summary>
    public static string CleanName(string counterName) => Regex.Replace(counterName, @"#\d+$", "");

    /// <summary>GPU engine instances are named "pid_1234_luid_0x…_phys_0_eng_0_engtype_3D".</summary>
    public static (int Pid, string EngineType)? ParseGpuEngine(string instance)
    {
        var m = Regex.Match(instance, @"^pid_(?<pid>\d+)_.*_engtype_(?<type>.+)$");
        return m.Success ? (int.Parse(m.Groups["pid"].Value), m.Groups["type"].Value) : null;
    }

    /// <summary>Task Manager shows the busiest engine, not the sum of all of them.</summary>
    public static IReadOnlyDictionary<int, double> GpuPerProcess(IEnumerable<(string Instance, double Percent)> engines)
    {
        var perEngine = new Dictionary<(int Pid, string Type), double>();
        foreach (var (instance, percent) in engines)
        {
            if (ParseGpuEngine(instance) is not { } e) continue;
            perEngine[e] = perEngine.GetValueOrDefault(e) + percent;
        }
        return perEngine.GroupBy(kv => kv.Key.Pid).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(kv => kv.Value)));
    }
}

/// <summary>All the processes of one program added together, as Task Manager's "Apps" view shows them.</summary>
public sealed record AppUse(string Name, ProcessRole Role, string? Path, double CpuPercent, double GpuPercent, long RamBytes, long DiskBytesPerSec, IReadOnlyList<int> Pids)
{
    public int Instances => Pids.Count;
    public bool CanEnd => Role == ProcessRole.Normal;
    public string RoleLabel => new ProcessUse(0, Name, 0, 0, 0, 0, Path, Role).RoleLabel;
}

public sealed record ActivitySnapshot(IReadOnlyList<ProcessUse> Processes, double TotalCpuPercent, long RamUsedBytes, long RamTotalBytes, double? TotalGpuPercent, DateTime At)
{
    /// <summary>One row per program (chrome's 30 processes become one line).</summary>
    public IReadOnlyList<AppUse> Apps() =>
        Processes.GroupBy(p => (p.Name.ToLowerInvariant(), p.Role)).Select(g => new AppUse(
            g.First().Name, g.Key.Role, g.Select(p => p.Path).FirstOrDefault(p => p is not null),
            Math.Round(g.Sum(p => p.CpuPercent), 1), Math.Round(Math.Min(100, g.Sum(p => p.GpuPercent)), 1), g.Sum(p => p.RamBytes), g.Sum(p => p.DiskBytesPerSec), g.Select(p => p.Pid).ToList())).ToList();

    public IEnumerable<AppUse> TopApps(ActivitySort sort, int count = 12) =>
        (sort switch
        {
            ActivitySort.Cpu => Apps().OrderByDescending(p => p.CpuPercent),
            ActivitySort.Gpu => Apps().OrderByDescending(p => p.GpuPercent),
            ActivitySort.Ram => Apps().OrderByDescending(p => p.RamBytes),
            _ => Apps().OrderByDescending(p => p.DiskBytesPerSec),
        }).Take(count);

    public IEnumerable<ProcessUse> TopBy(ActivitySort sort, int count = 12) =>
        (sort switch
        {
            ActivitySort.Cpu => Processes.OrderByDescending(p => p.CpuPercent),
            ActivitySort.Gpu => Processes.OrderByDescending(p => p.GpuPercent),
            ActivitySort.Ram => Processes.OrderByDescending(p => p.RamBytes),
            _ => Processes.OrderByDescending(p => p.DiskBytesPerSec),
        }).Take(count);
}

public enum ActivitySort { Cpu, Gpu, Ram, Disk }

/// <summary>What's using the PC right now, from Windows' own performance counters (no admin).</summary>
public static class ActivityReader
{
    public static Task<ActivitySnapshot> SampleAsync(CancellationToken ct = default) => Task.Run(() => Sample(ct), ct);

    private static ActivitySnapshot Sample(CancellationToken ct)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        var gpu = ReadGpu();
        var raw = new List<(int Pid, string Name, double Cpu, long Ram, long Disk)>();
        double totalCpu = 0;
        try
        {
            foreach (var o in WmiQuery.Read(@"root\cimv2", "SELECT IDProcess, Name, PercentProcessorTime, WorkingSetPrivate, IODataBytesPersec FROM Win32_PerfFormattedData_PerfProc_Process"))
            {
                ct.ThrowIfCancellationRequested();
                var name = o["Name"]?.ToString() ?? "";
                if (name is "_Total" or "Idle" || name.Length == 0) continue;
                var cpu = Math.Min(100, Convert.ToDouble(o["PercentProcessorTime"] ?? 0) / cores);
                totalCpu += cpu;
                raw.Add((Convert.ToInt32(o["IDProcess"] ?? 0), ProcessRules.CleanName(name), cpu, Convert.ToInt64(o["WorkingSetPrivate"] ?? 0L), Convert.ToInt64(o["IODataBytesPersec"] ?? 0L)));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Show an empty list rather than fail.
        }

        var saladRunning = raw.Any(p => string.Equals(p.Name, "Salad", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("xmrig", StringComparison.OrdinalIgnoreCase));
        var self = Environment.ProcessPath;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var me = identity.User;
        var list = new List<ProcessUse>(raw.Count);
        foreach (var p in raw)
        {
            var path = PathOf(p.Pid);
            var mine = p.Pid <= 4 || path is null || (CarsControl.Core.Elevation.Native.ProcessUser(p.Pid) is { } owner && owner == me);
            list.Add(new ProcessUse(p.Pid, p.Name, Math.Round(p.Cpu, 1), p.Ram, p.Disk, Math.Round(gpu.GetValueOrDefault(p.Pid), 1), path, ProcessRules.Classify(p.Name, path, saladRunning, self, mine)));
        }

        var (used, total) = Memory();
        double? totalGpu = gpu.Count == 0 ? null : Math.Min(100, gpu.Values.Sum());
        return new ActivitySnapshot(list, Math.Min(100, Math.Round(totalCpu, 1)), used, total, totalGpu, DateTime.Now);
    }

    private static IReadOnlyDictionary<int, double> ReadGpu()
    {
        try
        {
            return ProcessRules.GpuPerProcess(WmiQuery.Read(@"root\cimv2", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE UtilizationPercentage > 0").Select(o => (o["Name"]?.ToString() ?? "", Convert.ToDouble(o["UtilizationPercentage"] ?? 0))));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new Dictionary<int, double>();
        }
    }

    private static string? PathOf(int pid) => pid <= 4 ? null : CarsControl.Core.Elevation.Native.ImagePath(pid);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static (long Used, long Total) Memory()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys) : (0, 0);
    }
}
