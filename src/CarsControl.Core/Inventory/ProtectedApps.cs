using CarsControl.Core.Models;

namespace CarsControl.Core.Inventory;

/// <summary>Apps that can't be ticked for uninstall until you unlock them. The default list covers the
/// runtimes and drivers Windows and C.A.R.S. depend on; you add your own (VPN, password manager…) in
/// Settings or with right-click › Protect. Match is a case-insensitive "name starts with".
/// Salad is always protected and cannot be unlocked through this list.</summary>
public sealed class ProtectedApps
{
    /// <summary>A fresh install's list; yours lives in settings.json (see <see cref="Settings.Defaults"/>).</summary>
    public static IReadOnlyList<string> Defaults => Settings.Defaults.ProtectedApps;

    private readonly List<string> _prefixes;

    public ProtectedApps(IEnumerable<string>? prefixes = null) => _prefixes = (prefixes ?? Defaults).ToList();

    public IReadOnlyList<string> Prefixes => _prefixes;
    public static bool IsAlwaysProtectedName(string? name) => name?.Contains("Salad", StringComparison.OrdinalIgnoreCase) == true;
    public static bool IsAlwaysProtected(AppEntry app) => IsAlwaysProtectedName(app.Name) || IsAlwaysProtectedName(app.WingetId)
        || (app.InstallLocation?.Replace('/', '\\').Split('\\').Any(part => part.StartsWith("Salad", StringComparison.OrdinalIgnoreCase)) ?? false);

    public bool IsProtected(AppEntry a) =>
        IsAlwaysProtected(a) || _prefixes.Any(p => a.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
