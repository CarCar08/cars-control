using CarsControl.Core.Models;
using CarsControl.Core.Winget;

namespace CarsControl.Core.Inventory;

/// <summary>Attaches winget identity/updates to ARP and MSIX entries. winget's ProductCodes are the
/// ARP subkey names and its PackageFamilyNames are MSIX families, so matching is exact, not by name.</summary>
public static class Correlator
{
    public static void Apply(IReadOnlyList<AppEntry> apps, IReadOnlyList<WingetInstalled> winget)
    {
        var arpByKey = apps.Where(a => a.Arp is not null).ToLookup(a => a.Arp!.KeyName, StringComparer.OrdinalIgnoreCase);
        var msixByFamily = apps.Where(a => a.MsixFamilyName is not null).ToLookup(a => a.MsixFamilyName!, StringComparer.OrdinalIgnoreCase);

        foreach (var w in winget)
        {
            var target = FindTarget(w, arpByKey, msixByFamily);
            if (target is null) continue;

            if (w.IsCorrelated)
            {
                target.WingetId = w.Id;
                target.WingetSource = w.SourceName;
                target.AvailableVersion = w.AvailableVersion;
            }
            target.InstallLocation ??= w.InstalledLocation;
        }
    }

    internal static AppEntry? FindTarget(WingetInstalled w, ILookup<string, AppEntry> arpByKey, ILookup<string, AppEntry> msixByFamily)
    {
        foreach (var code in w.ProductCodes)
        {
            var candidates = arpByKey[code].ToList();
            if (candidates.Count > 0) return Pick(candidates, w);
        }
        foreach (var family in w.FamilyNames)
        {
            var hit = msixByFamily[family].FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>The same subkey name can exist in more than one hive. winget only reports scope for
    /// per-user installs, so Unknown means machine-wide.</summary>
    private static AppEntry Pick(List<AppEntry> candidates, WingetInstalled w)
    {
        if (candidates.Count == 1) return candidates[0];
        var wantHive = w.Scope == InstallScope.User ? new[] { ArpHive.CurrentUser } : [ArpHive.LocalMachine64, ArpHive.LocalMachine32];
        return candidates.FirstOrDefault(c => wantHive.Contains(c.Arp!.Hive) && c.Version == w.InstalledVersion)
            ?? candidates.FirstOrDefault(c => wantHive.Contains(c.Arp!.Hive))
            ?? candidates.FirstOrDefault(c => c.Version == w.InstalledVersion)
            ?? candidates[0];
    }
}
