namespace CarsControl.Core.Health;

/// <summary>
/// Apps some features are built around (Salad, Proton VPN, Tailscale). Their cards, chips and mentions only appear on PCs that
/// have them; protection rules (Salad is never changed) stay on everywhere, since they cost nothing where the app isn't there.
/// </summary>
public static class PersonalExtras
{
    public static bool SaladInstalled(IEnumerable<string> appNames, Func<string, bool>? folderExists = null)
    {
        folderExists ??= Directory.Exists;
        if (appNames.Any(n => n.StartsWith("Salad", StringComparison.OrdinalIgnoreCase))) return true;
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return folderExists(Path.Combine(programFiles, "Salad")) || folderExists(Path.Combine(local, "Programs", "Salad"));
    }

    public static bool ProtonVpnInstalled(IEnumerable<NetAdapter> adapters, IEnumerable<string> appNames) =>
        adapters.Any(a => a.IsProton)
        || appNames.Any(n => n.Contains("Proton VPN", StringComparison.OrdinalIgnoreCase) || n.Equals("ProtonVPN", StringComparison.OrdinalIgnoreCase));
}
