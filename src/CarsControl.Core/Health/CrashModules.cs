namespace CarsControl.Core.Health;

/// <summary>What the module a crash dump blames usually means, in plain words, and what to try next.</summary>
public static class CrashModules
{
    public static string? Explain(string? module, string? failureBucket = null)
    {
        if (string.IsNullOrWhiteSpace(module)) return null;
        var name = module.Trim().ToLowerInvariant();
        var bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        if (!name.Contains('.')) name += ".sys";

        var text = name switch
        {
            "dxgkrnl.sys" or "dxgmms2.sys" or "watchdog.sys" =>
                "This is Windows' graphics kernel, the part between games and the graphics driver. Crashes here almost always come from the graphics driver itself, an unstable graphics-card overclock or undervolt, or (less often) faulty memory. Try: compare the dates with the NVIDIA driver versions on the Drivers tab, install a different driver version cleanly, and remove any graphics overclock.",
            "nvlddmkm.sys" =>
                "This is the NVIDIA graphics driver. Try a clean install of a different driver version (Drivers tab shows which ones you've had) and remove any graphics overclock or undervolt.",
            "amdkmdag.sys" or "atikmdag.sys" or "atikmpag.sys" =>
                "This is the AMD graphics driver. Try a clean install of a different driver version and remove any graphics overclock or undervolt.",
            "igdkmd64.sys" or "igdkmdn64.sys" or "igdkmdnd64.sys" =>
                "This is the Intel graphics driver. Try a newer (or the PC maker's) Intel graphics driver.",
            "ntoskrnl.exe" or "ntkrnlmp.exe" or "hal.dll" =>
                "This is the core of Windows. It's almost never the real cause: another driver or the hardware (memory, an overclock, power) usually broke something earlier. Test the memory and undo overclocks; if it repeats, the failure group helps a search.",
            "win32kbase.sys" or "win32kfull.sys" or "win32k.sys" =>
                "This is Windows' window and input system. Usually a symptom of a faulty graphics, input or overlay driver.",
            "tcpip.sys" or "ndis.sys" or "netio.sys" or "afd.sys" =>
                "This is Windows' network stack. Usually caused by a network adapter, VPN or firewall driver: update the network driver and check VPN software.",
            "storport.sys" or "stornvme.sys" or "storahci.sys" or "iastora.sys" or "ntfs.sys" =>
                "This is the storage system. Check the Drives tab for drive problems, and update the storage (chipset/NVMe) drivers.",
            "wdf01000.sys" =>
                "This is the framework other drivers are built on; the real cause is one of those drivers.",
            _ when name.StartsWith("memory_corruption") =>
                "The debugger found corrupted memory: often faulty or overclocked RAM (an XMP/EXPO profile) or a driver writing where it shouldn't. Test the memory.",
            _ => null,
        };
        if (text is null) return null;
        if (failureBucket?.StartsWith("AV_", StringComparison.OrdinalIgnoreCase) == true)
            text += " (\"AV\" in the failure group means it touched memory it wasn't allowed to.)";
        return text;
    }
}
