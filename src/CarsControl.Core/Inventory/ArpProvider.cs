using System.Globalization;
using System.Text.RegularExpressions;
using CarsControl.Core.Models;
using Microsoft.Win32;

namespace CarsControl.Core.Inventory;

/// <summary>Reads the three Add/Remove Programs registry views (HKLM 64-bit, HKLM 32-bit, HKCU) the
/// same way Settings › Apps does: hidden system components, patches and child entries are skipped.</summary>
public static partial class ArpProvider
{
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<AppEntry> ReadAll()
    {
        var result = new List<AppEntry>();
        foreach (var hive in Enum.GetValues<ArpHive>())
        {
            using var uninstall = OpenUninstallKey(hive);
            if (uninstall is null) continue;

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var sub = uninstall.OpenSubKey(keyName);
                if (sub is null) continue;
                var values = sub.GetValueNames().ToDictionary(n => n, n => sub.GetValue(n), StringComparer.OrdinalIgnoreCase);
                if (Parse(hive, keyName, values) is { } entry) result.Add(entry);
            }
        }
        return result;
    }

    /// <summary>HKLM entries plus another user's HKCU entries (via HKEY_USERS\{sid}). The elevated helper
    /// runs as the admin account, so its own HKCU is the wrong one; this is how it sees your per-user apps.</summary>
    public static IReadOnlyList<AppEntry> ReadMachineAndUser(string? userSid)
    {
        var result = new List<AppEntry>();
        foreach (var hive in new[] { ArpHive.LocalMachine64, ArpHive.LocalMachine32 })
        {
            using var k = OpenUninstallKey(hive);
            ReadInto(result, hive, k);
        }
        if (userSid is not null)
        {
            using var k = Registry.Users.OpenSubKey($@"{userSid}\{UninstallKeyPath}");
            ReadInto(result, ArpHive.CurrentUser, k);
        }
        return result;
    }

    private static void ReadInto(List<AppEntry> result, ArpHive hive, RegistryKey? uninstall)
    {
        if (uninstall is null) return;
        foreach (var keyName in uninstall.GetSubKeyNames())
        {
            using var sub = uninstall.OpenSubKey(keyName);
            if (sub is null) continue;
            var values = sub.GetValueNames().ToDictionary(n => n, n => sub.GetValue(n), StringComparer.OrdinalIgnoreCase);
            if (Parse(hive, keyName, values) is { } entry) result.Add(entry);
        }
    }

    /// <summary>Re-reads one entry (after an update or uninstall). Null when the key is gone or hidden.</summary>
    public static AppEntry? ReadOne(ArpLocation loc)
    {
        using var uninstall = OpenUninstallKey(loc.Hive);
        using var sub = uninstall?.OpenSubKey(loc.KeyName);
        if (sub is null) return null;
        var values = sub.GetValueNames().ToDictionary(n => n, n => sub.GetValue(n), StringComparer.OrdinalIgnoreCase);
        return Parse(loc.Hive, loc.KeyName, values);
    }

    public static bool Exists(ArpLocation loc)
    {
        using var uninstall = OpenUninstallKey(loc.Hive);
        using var sub = uninstall?.OpenSubKey(loc.KeyName);
        return sub is not null;
    }

    public static RegistryKey? OpenUninstallKey(ArpHive hive, bool writable = false) => hive switch
    {
        ArpHive.LocalMachine64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallKeyPath, writable),
        ArpHive.LocalMachine32 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(UninstallKeyPath, writable),
        ArpHive.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey(UninstallKeyPath, writable),
        _ => null,
    };

    /// <summary>Turns one Uninstall subkey's values into an entry, or null when Settings would hide it.</summary>
    public static AppEntry? Parse(ArpHive hive, string keyName, IReadOnlyDictionary<string, object?> v)
    {
        var name = Str(v, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Dword(v, "SystemComponent") == 1) return null;
        if (!string.IsNullOrEmpty(Str(v, "ParentKeyName"))) return null;
        var releaseType = Str(v, "ReleaseType");
        if (releaseType is not null && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;

        var isMsi = Dword(v, "WindowsInstaller") == 1;
        var productCode = isMsi && GuidKey().IsMatch(keyName) ? keyName.ToUpperInvariant() : null;
        var uninstall = Str(v, "UninstallString");
        // MSI entries sometimes omit UninstallString; msiexec /x {code} still works.
        if (productCode is null && uninstall is not null && MsiExecGuid().Match(uninstall) is { Success: true } m && isMsi)
            productCode = m.Groups[1].Value.ToUpperInvariant();

        var loc = new ArpLocation(hive, keyName);
        var sizeKb = Dword(v, "EstimatedSize");

        return new AppEntry
        {
            Key = $"arp:{hive}:{keyName}",
            Name = name.Trim(),
            Publisher = Str(v, "Publisher")?.Trim(),
            Version = Str(v, "DisplayVersion")?.Trim(),
            Scope = loc.Scope,
            Source = AppSource.Arp,
            Arp = loc,
            UninstallString = uninstall,
            QuietUninstallString = Str(v, "QuietUninstallString"),
            MsiProductCode = productCode,
            NoRemove = Dword(v, "NoRemove") == 1,
            DisplayIcon = CleanIconPath(Str(v, "DisplayIcon")),
            InstallLocation = NormalizeDir(Str(v, "InstallLocation")),
            InstallDate = ParseInstallDate(Str(v, "InstallDate")),
            SizeBytes = sizeKb is > 0 ? sizeKb.Value * 1024L : null,
        };
    }

    internal static DateTime? ParseInstallDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    /// <summary>"C:\x\app.exe",0  →  C:\x\app.exe (index dropped; icon loader uses index 0).</summary>
    internal static string? CleanIconPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            s = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        }
        return string.IsNullOrWhiteSpace(s) ? null : Environment.ExpandEnvironmentVariables(s.Trim());
    }

    internal static string? NormalizeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')).TrimEnd('\\');
        return d.Length < 3 ? null : d; // "C:" alone is never a real install location
    }

    private static string? Str(IReadOnlyDictionary<string, object?> v, string name) =>
        v.TryGetValue(name, out var o) && o is string s && s.Length > 0 ? s : null;

    private static long? Dword(IReadOnlyDictionary<string, object?> v, string name) => v.TryGetValue(name, out var o) ? o switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s, out var p) => p,
        _ => null,
    } : null;

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    internal static partial Regex GuidKey();

    [GeneratedRegex(@"msiexec(?:\.exe)?\s+/[IXix]\s*(\{[0-9A-Fa-f-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiExecGuid();
}
