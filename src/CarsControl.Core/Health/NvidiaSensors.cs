using System.Runtime.InteropServices;
using System.Text;

namespace CarsControl.Core.Health;

public sealed record GpuReading(
    string Name, string DriverVersion,
    int? TempC, int? GpuLoadPct, int? MemoryLoadPct,
    int? CoreMHz, int? MemoryMHz, int? FanPct,
    double? PowerWatts, double? PowerLimitWatts,
    long VramUsedBytes, long VramTotalBytes);

/// <summary>
/// NVIDIA GPU temperature, load, clocks, fan, power and memory, read from NVML (nvml.dll, installed with
/// every NVIDIA driver). No admin rights and no extra driver. Where there's no NVIDIA card, or the library is
/// missing, it simply reports nothing.
/// </summary>
public static class NvidiaSensors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private const string Dll = "nvml.dll";

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint count);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] private static extern int GetName(IntPtr device, StringBuilder name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtilization(IntPtr device, out Utilization u);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr device, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] private static extern int GetFan(IntPtr device, out uint percent);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] private static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] private static extern int GetMemory(IntPtr device, out MemoryInfo info);
    [DllImport(Dll, EntryPoint = "nvmlSystemGetDriverVersion")] private static extern int GetDriverVersion(StringBuilder version, uint length);

    /// <summary>One reading per NVIDIA GPU; empty when there's none.</summary>
    public static IReadOnlyList<GpuReading> Read()
    {
        try
        {
            if (Init() != 0) return [];
            try
            {
                if (GetCount(out var count) != 0) return [];
                var driver = new StringBuilder(80);
                GetDriverVersion(driver, 80);
                var list = new List<GpuReading>();
                for (uint i = 0; i < count; i++)
                {
                    if (GetHandle(i, out var d) != 0) continue;
                    var name = new StringBuilder(96);
                    GetName(d, name, 96);
                    list.Add(new GpuReading(
                        name.ToString(), driver.ToString(),
                        Value(GetTemperature(d, 0, out var t), t), GetUtilization(d, out var u) == 0 ? (int)u.Gpu : null, GetUtilization(d, out u) == 0 ? (int)u.Memory : null,
                        Value(GetClock(d, 0, out var core), core), Value(GetClock(d, 2, out var mem), mem), Value(GetFan(d, out var fan), fan),
                        GetPower(d, out var mw) == 0 ? mw / 1000.0 : null, GetPowerLimit(d, out var lim) == 0 ? lim / 1000.0 : null,
                        GetMemory(d, out var m) == 0 ? (long)m.Used : 0, m.Total > 0 ? (long)m.Total : 0));
                }
                return list;
            }
            finally
            {
                Shutdown();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return [];
        }
    }

    private static int? Value(int status, uint value) => status == 0 ? (int)value : null;
}
