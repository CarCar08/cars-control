using CarsControl.Core.Models;
using Microsoft.Management.Deployment;

namespace CarsControl.Core.Winget;

/// <summary>One package from winget's installed catalog. Correlated packages carry the winget Id;
/// uncorrelated ones have local ids like ARP\Machine\X64\{GUID} or MSIX\...</summary>
public sealed record WingetInstalled(
    string Id,
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    bool IsCorrelated,
    string? SourceName,
    InstallScope Scope,
    string? InstalledLocation,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> FamilyNames);

public sealed class WingetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Lists installed packages through the winget COM API (as you, unelevated). One composite
/// search returns both per-user and machine-wide installs, each correlated with the winget source,
/// along with ProductCodes/PackageFamilyNames that map deterministically to ARP keys and MSIX.</summary>
public static class WingetProvider
{
    public static Task<IReadOnlyList<WingetInstalled>> GetInstalledAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<WingetInstalled>>(() => GetInstalled(ct), ct);

    private static IReadOnlyList<WingetInstalled> GetInstalled(CancellationToken ct)
    {
        PackageManager pm;
        try
        {
            pm = WingetCom.CreatePackageManager();
        }
        catch (Exception ex)
        {
            throw new WingetUnavailableException("The winget COM server could not be started. Is App Installer installed and is this process unelevated?", ex);
        }

        var catalog = ConnectComposite(pm);
        var options = WingetCom.CreateFindPackagesOptions();
        var found = catalog.FindPackages(options);
        if (found.Status != FindPackagesResultStatus.Ok)
            throw new WingetUnavailableException($"winget search failed: {found.Status}");

        var matches = found.Matches;
        var count = matches.Count;
        var list = new List<WingetInstalled>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                list.Add(Convert(matches[i].CatalogPackage));
            }
            catch
            {
                // One malformed manifest must not hide the rest of the list.
            }
        }
        return list;
    }

    internal static PackageCatalog ConnectComposite(PackageManager pm)
    {
        var remote = pm.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
        var opts = WingetCom.CreateCompositeOptions();
        opts.Catalogs.Add(remote);
        opts.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
        var composite = pm.CreateCompositePackageCatalog(opts);
        var connect = composite.Connect();
        if (connect.Status != ConnectResultStatus.Ok)
            throw new WingetUnavailableException($"Could not open the winget source: {connect.Status}");
        return connect.PackageCatalog;
    }

    private static WingetInstalled Convert(CatalogPackage pkg)
    {
        var installed = pkg.InstalledVersion;
        var available = pkg.DefaultInstallVersion;
        var correlated = available is not null && !pkg.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase)
                                               && !pkg.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase);

        var scope = InstallScope.Unknown;
        string? location = null;
        IReadOnlyList<string> productCodes = [];
        IReadOnlyList<string> familyNames = [];
        if (installed is not null)
        {
            scope = ParseScope(installed.GetMetadata(PackageVersionMetadataField.InstalledScope));
            location = NullIfEmpty(installed.GetMetadata(PackageVersionMetadataField.InstalledLocation));
            productCodes = ByIndex(installed.ProductCodes);
            familyNames = ByIndex(installed.PackageFamilyNames);
        }

        return new WingetInstalled(
            pkg.Id,
            pkg.Name,
            installed?.Version,
            pkg.IsUpdateAvailable ? available?.Version : null,
            correlated,
            correlated ? available!.PackageCatalog.Info.Name : null,
            scope,
            location,
            productCodes,
            familyNames);
    }

    internal static InstallScope ParseScope(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "user" => InstallScope.User,
        "machine" => InstallScope.Machine,
        _ => InstallScope.Unknown,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Copies a WinRT vector view by index. Enumerating it would QI the out-of-process object
    /// for IIterable&lt;T&gt;, which the metadata-based marshaler can't proxy ("No such interface
    /// supported"); IVectorView&lt;T&gt; (Count + GetAt) marshals fine.</summary>
    internal static List<T> ByIndex<T>(IReadOnlyList<T> view)
    {
        var n = view.Count;
        var list = new List<T>(n);
        for (var i = 0; i < n; i++) list.Add(view[i]);
        return list;
    }
}
