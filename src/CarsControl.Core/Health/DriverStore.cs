using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace CarsControl.Core.Health;

/// <summary>A driver package in Windows' driver store (oem12.inf) as <c>pnputil /enum-drivers</c> lists it.</summary>
public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider, string ClassName, string Version, DateTime? Date, bool InUse)
{
    public override string ToString() => $"{Provider} {OriginalName} {Version}";
}

public sealed record InstalledDriver(string Device, string Manufacturer, string Version, DateTime? Date, bool Signed, string InfName, string Class);

public static partial class DriverStore
{
    public static readonly Regex PublishedNamePattern = PublishedName();

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedName();

    /// <summary>Parses <c>pnputil /enum-drivers</c> (its English output; other languages fall back to no list).</summary>
    public static IReadOnlyList<DriverPackage> Parse(IEnumerable<string> lines, ISet<string> inUse)
    {
        var list = new List<DriverPackage>();
        Dictionary<string, string>? cur = null;
        void Flush()
        {
            if (cur is null || !cur.TryGetValue("Published Name", out var pub)) return;
            DateTime? date = null;
            string version = "";
            if (cur.TryGetValue("Driver Version", out var dv))
            {
                var parts = dv.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (DateTime.TryParse(parts[0], CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d)) date = d;
                version = parts.Length > 1 ? parts[1] : parts[0];
            }
            list.Add(new DriverPackage(pub, cur.GetValueOrDefault("Original Name") ?? "", cur.GetValueOrDefault("Provider Name") ?? "", cur.GetValueOrDefault("Class Name") ?? "",
                version, date, inUse.Contains(pub)));
        }
        foreach (var raw in lines)
        {
            var m = Regex.Match(raw, @"^\s*(?<k>[A-Za-z][A-Za-z ]+?):\s*(?<v>.*?)\s*$");
            if (!m.Success) continue;
            if (m.Groups["k"].Value == "Published Name")
            {
                Flush();
                cur = new Dictionary<string, string>();
            }
            if (cur is not null) cur[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        Flush();
        return list;
    }

    /// <summary>
    /// Old copies of a driver: same original file, provider and class, but not the newest and not used by any
    /// device. Windows keeps these after every driver update (a graphics driver can leave 500 MB each time).
    /// Deleting one is refused by Windows itself if a device still uses it.
    /// </summary>
    public static IReadOnlyList<DriverPackage> Superseded(IReadOnlyList<DriverPackage> all) =>
        all.GroupBy(p => (p.OriginalName.ToLowerInvariant(), p.Provider.ToLowerInvariant(), p.ClassName.ToLowerInvariant()))
            .SelectMany(g => g.OrderByDescending(p => p.Date ?? DateTime.MinValue).ThenByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase).Skip(1))
            .Where(p => !p.InUse)
            .OrderBy(p => p.Provider).ThenBy(p => p.OriginalName).ToList();

    /// <summary>Drivers of devices that exist (Win32_PnPSignedDriver), and the oemNN.inf names they use.</summary>
    public static (IReadOnlyList<InstalledDriver> Drivers, ISet<string> InUse) ReadInstalled()
    {
        var list = new List<InstalledDriver>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var o in WmiQuery.Read(@"root\cimv2", "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, IsSigned, InfName, DeviceClass FROM Win32_PnPSignedDriver"))
            {
                var inf = o["InfName"]?.ToString() ?? "";
                if (inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) used.Add(inf);
                list.Add(new InstalledDriver(o["DeviceName"]?.ToString() ?? "", o["Manufacturer"]?.ToString() ?? "", o["DriverVersion"]?.ToString() ?? "",
                    SystemSpecsReader.ParseWmiDate(o["DriverDate"]?.ToString()), o["IsSigned"] as bool? ?? false, inf, o["DeviceClass"]?.ToString() ?? ""));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // An empty list is shown as "couldn't read".
        }
        return (list.Where(d => d.Device.Length > 0).OrderBy(d => d.Class).ThenBy(d => d.Device).ToList(), used);
    }
}
