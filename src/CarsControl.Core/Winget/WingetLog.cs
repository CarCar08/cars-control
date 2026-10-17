using System.Text.RegularExpressions;
using CarsControl.Core.Util;

namespace CarsControl.Core.Winget;

/// <summary>winget's catalog offered one version, but the installer it downloaded is an older one.</summary>
public sealed record CatalogMismatch(string Offered, string Installer)
{
    public string Explanation =>
        $"winget's catalog offers {Offered}, but the installer it downloads is still {Installer}. That's a mistake in winget's catalog, not on your PC, " +
        "so trying again won't help until the catalog is fixed.";
}

/// <summary>
/// Reads the installer log winget writes with <c>--log</c> to tell *why* an update "succeeded" without changing the version.
/// Inno Setup logs name the setup file ("Original Setup EXE: …\VSCodeSetup-x64-1.141.2.exe"); MSI logs name the product
/// version ("Property(S): ProductVersion = 1.2.3").
/// </summary>
public static partial class WingetLog
{
    /// <summary>The version of the installer that actually ran, if the log says.</summary>
    public static string? InstallerVersion(IEnumerable<string> lines)
    {
        foreach (var line in lines.Take(400))
        {
            if (InnoSetupExe().Match(line) is { Success: true } inno)
            {
                var file = Path.GetFileNameWithoutExtension(inno.Groups["path"].Value.Trim());
                if (VersionIn().Matches(file).LastOrDefault() is { } v) return v.Value;
            }
            if (MsiProductVersion().Match(line) is { Success: true } msi) return msi.Groups["v"].Value;
        }
        return null;
    }

    /// <summary>A mismatch when the installer that ran is older than the version winget offered.</summary>
    public static CatalogMismatch? Check(IEnumerable<string> lines, string? offered)
    {
        if (string.IsNullOrWhiteSpace(offered) || InstallerVersion(lines) is not { } installer) return null;
        return VersionCompare.IsNewer(offered, installer) ? new CatalogMismatch(offered, installer) : null;
    }

    /// <summary>Reads the log file (it can be several MB; only the start matters).</summary>
    public static CatalogMismatch? CheckFile(string? logPath, string? offered)
    {
        if (logPath is null || !File.Exists(logPath)) return null;
        try
        {
            return Check(File.ReadLines(logPath), offered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"Original Setup EXE:\s*(?<path>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoSetupExe();

    [GeneratedRegex(@"Property\([SC]\):\s*ProductVersion\s*=\s*(?<v>\d+(\.\d+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiProductVersion();

    [GeneratedRegex(@"\d+\.\d+(\.\d+){0,2}")]
    private static partial Regex VersionIn();
}

/// <summary>
/// Apps that update themselves well. When winget can't update one, C.A.R.S. says how to use the app's own updater.
/// Matched on the winget id (exact or prefix).
/// </summary>
public static class SelfUpdatingApps
{
    private static readonly (string Id, string How)[] Known =
    [
        ("Microsoft.VisualStudioCode", "Open VS Code and choose Help › Check for Updates (or the gear icon › Check for Updates)."),
        ("Microsoft.VisualStudioCode.Insiders", "Open VS Code Insiders and choose Help › Check for Updates."),
        ("Google.Chrome", "Open Chrome › ⋮ › Help › About Google Chrome; it updates itself there."),
        ("Microsoft.Edge", "Open Edge › … › Help and feedback › About Microsoft Edge."),
        ("Mozilla.Firefox", "Open Firefox › ☰ › Help › About Firefox."),
        ("Brave.Brave", "Open Brave › ☰ › About Brave."),
        ("Discord.Discord", "Discord updates itself when it starts: quit it from the tray and open it again."),
        ("SlackTechnologies.Slack", "Slack updates itself when it restarts: quit and reopen it."),
        ("Spotify.Spotify", "Spotify updates itself when it restarts: quit and reopen it."),
        ("Zoom.Zoom", "Open Zoom › your profile picture › Check for Updates."),
        ("Notion.Notion", "Notion updates itself when it restarts."),
        ("Anysphere.Cursor", "Open Cursor and choose Help › Check for Updates."),
        ("Obsidian.Obsidian", "Open Obsidian › Settings › General › Check for updates."),
    ];

    public static string? HowToUpdate(string? wingetId) =>
        wingetId is null ? null : Known.FirstOrDefault(k => string.Equals(k.Id, wingetId, StringComparison.OrdinalIgnoreCase)).How;
}
