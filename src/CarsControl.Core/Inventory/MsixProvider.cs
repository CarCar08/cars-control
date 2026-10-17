using CarsControl.Core.Models;
using Windows.ApplicationModel;
using WinPackageManager = Windows.Management.Deployment.PackageManager;

namespace CarsControl.Core.Inventory;

/// <summary>Store/MSIX apps registered for the current user (you). These are always per-user, so
/// removing them never needs admin.</summary>
public static class MsixProvider
{
    public static IReadOnlyList<AppEntry> ReadAll()
    {
        var result = new List<AppEntry>();
        var pm = new WinPackageManager();
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                if (p.SignatureKind == PackageSignatureKind.System) continue;

                var id = p.Id;
                var name = SafeGet(() => p.DisplayName);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    name = id.Name;
                var v = id.Version;

                result.Add(new AppEntry
                {
                    Key = $"msix:{id.FullName}",
                    Name = name,
                    Publisher = SafeGet(() => p.PublisherDisplayName),
                    Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    Scope = InstallScope.User,
                    Source = AppSource.Msix,
                    MsixFullName = id.FullName,
                    MsixFamilyName = id.FamilyName,
                    MsixLogoPath = SafeGet(() => p.Logo?.LocalPath),
                    InstallLocation = SafeGet(() => p.InstalledPath),
                    InstallDate = SafeGet(() => (DateTime?)p.InstalledDate.LocalDateTime),
                    NoRemove = SafeGet(() => p.SignatureKind == PackageSignatureKind.System),
                    Channel = p.SignatureKind == PackageSignatureKind.Store ? UpdateChannel.Store : UpdateChannel.None,
                });
            }
            catch
            {
                // A few system packages throw on property access; skip them rather than fail the list.
            }
        }
        return result;
    }

    private static T? SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }
}
