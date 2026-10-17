using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace CarsControl.Core.Winget;

/// <summary>Activates the winget COM API (out-of-process server inside the App Installer package).
/// An unpackaged app can't use WinRT activation for these classes, so they are created by CLSID.
/// The CLSIDs were verified as registered under HKCR\PackagedCom\ClassIndex on the development PC.
/// Only works unelevated: the OOP server refuses elevated callers, which is fine because the helper
/// uses the winget CLI instead.</summary>
internal static class WingetCom
{
    private static readonly Guid PackageManagerClsid = new("C53A4F16-787E-42A4-B304-29EFFB4BF597");
    private static readonly Guid FindPackagesOptionsClsid = new("572DED96-9C60-4526-8F92-EE7D91D38C1A");
    private static readonly Guid CreateCompositePackageCatalogOptionsClsid = new("526534B8-7E46-47C8-8416-B1685C327D37");
    private static readonly Guid InstallOptionsClsid = new("1095F097-EB96-453B-B4E6-1613637F3B14");
    private static readonly Guid UninstallOptionsClsid = new("E1D9A11E-9F85-4D87-9C17-2B93143ADB8D");
    private static readonly Guid PackageMatchFilterClsid = new("D02C9DAF-99DC-429C-B503-4E504E4AB000");

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    public static PackageManager CreatePackageManager() => Create(PackageManagerClsid, PackageManager.FromAbi);
    public static FindPackagesOptions CreateFindPackagesOptions() => Create(FindPackagesOptionsClsid, FindPackagesOptions.FromAbi);
    public static CreateCompositePackageCatalogOptions CreateCompositeOptions() => Create(CreateCompositePackageCatalogOptionsClsid, CreateCompositePackageCatalogOptions.FromAbi);
    public static InstallOptions CreateInstallOptions() => Create(InstallOptionsClsid, InstallOptions.FromAbi);
    public static UninstallOptions CreateUninstallOptions() => Create(UninstallOptionsClsid, UninstallOptions.FromAbi);
    public static PackageMatchFilter CreatePackageMatchFilter() => Create(PackageMatchFilterClsid, PackageMatchFilter.FromAbi);

    private static T Create<T>(Guid clsid, Func<IntPtr, T> fromAbi)
    {
        var hr = CoCreateInstance(clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, IID_IUnknown, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return fromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr); // FromAbi takes its own reference
        }
    }
}
