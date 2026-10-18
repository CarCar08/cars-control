using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CarsControl.Core.Optimize;

/// <summary>Windows 11's power mode (Settings › System › Power): an overlay on the Balanced plan.</summary>
public enum PowerMode { BestEfficiency, Balanced, BestPerformance }

/// <summary>
/// The Optimization page's switches that aren't plain registry values. Power mode and animations are per-user and need no admin;
/// hibernation is read here and changed through the helper (<see cref="Contracts.HibernationOp"/>).
/// </summary>
public static class SystemSwitches
{
    // Power mode overlays, as Windows' Settings app uses them.
    private static readonly Guid BestEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static Guid OverlayFor(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => BestEfficiencyOverlay,
        PowerMode.BestPerformance => BestPerformanceOverlay,
        _ => Guid.Empty, // Balanced = no overlay
    };

    public static PowerMode ModeFor(Guid overlay) =>
        overlay == BestEfficiencyOverlay ? PowerMode.BestEfficiency : overlay == BestPerformanceOverlay ? PowerMode.BestPerformance : PowerMode.Balanced;

    public static string Describe(PowerMode mode) => mode switch
    {
        PowerMode.BestEfficiency => "Best power efficiency",
        PowerMode.BestPerformance => "Best performance",
        _ => "Balanced",
    };

    /// <summary>
    /// The current power mode, or null when power modes aren't available: they only exist on top of the Balanced plan, so a PC
    /// set to High performance (or a maker's own plan) has none.
    /// </summary>
    public static PowerMode? ReadPowerMode()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return null;
            var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            LocalFree(schemePtr);
            if (scheme != BalancedPlan) return null;
            return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? ModeFor(overlay) : PowerMode.Balanced;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // Windows 10 before 1709 has no power modes
        }
    }

    public static bool SetPowerMode(PowerMode mode)
    {
        try
        {
            return PowerSetActiveOverlayScheme(OverlayFor(mode)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static Guid BalancedPlanId => BalancedPlan;

    /// <summary>The active power plan and its name ("Ultimate Performance"), or null when it can't be read.</summary>
    public static (Guid Id, string Name)? ReadActivePlan()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return null;
            var id = Marshal.PtrToStructure<Guid>(schemePtr);
            LocalFree(schemePtr);
            uint size = 512;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var name = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0 ? Marshal.PtrToStringUni(buffer) : null;
                return (id, string.IsNullOrWhiteSpace(name) ? "another plan" : name);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Makes a power plan active (no admin needed; the plan must exist).</summary>
    public static bool SetActivePlan(Guid plan)
    {
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The "Animation effects" switch in Settings › Accessibility › Visual effects (window and menu animations).</summary>
    public static bool ReadAnimations()
    {
        var on = true;
        return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref on, 0) || on;
    }

    public static bool SetAnimations(bool on)
    {
        var value = on;
        return SystemParametersInfo(SpiSetClientAreaAnimation, 0, value ? new IntPtr(1) : IntPtr.Zero, SpifUpdateIniFile | SpifSendChange);
    }

    /// <summary>Whether hibernation is on, and how big the hibernation file is (it's reserved on the system drive).</summary>
    public static (bool On, long FileBytes) ReadHibernation()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        var on = k?.GetValue("HibernateEnabled") is int n ? n != 0 : true;
        long size = 0;
        try
        {
            // Listing the folder reads the size without opening the file (opening hiberfil.sys is refused even to admins).
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            size = new DirectoryInfo(root).EnumerateFiles("hiberfil.sys", new EnumerationOptions { AttributesToSkip = 0 }).FirstOrDefault()?.Length ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // size unknown
        }
        return (on, size);
    }

    private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;
    private const uint SpifUpdateIniFile = 0x1, SpifSendChange = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
