namespace CarsControl.Core.Updates;

/// <summary>
/// Where a pip package lives decides who upgrades it. Packages under your profile were installed with
/// `pip install --user` (on this PC: %APPDATA%\Python\Python314\site-packages) and shadow the shared copy
/// in Program Files. Upgrading those through the admin helper (as the admin account) upgraded the *shared* copy
/// instead, so pillow_heif and pypdf "succeeded" twice on 2026-10-12 and never changed.
/// </summary>
public static class PipInstall
{
    /// <summary>True when the package is installed in the user's own site-packages (upgrade as the user, with --user).</summary>
    public static bool IsUserLocation(string? location, string? userProfile = null)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        var profile = (userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\') + "\\";
        return Path.GetFullPath(location).StartsWith(profile, StringComparison.OrdinalIgnoreCase);
    }

    public static string[] UpgradeArgs(string package, bool user) =>
        user
            ? ["-m", "pip", "install", "--upgrade", "--user", "--disable-pip-version-check", package]
            : ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", package];

    /// <summary>Reads "Version: x" from `pip show` output.</summary>
    public static string? ParseShowVersion(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))?["Version:".Length..].Trim();
}
