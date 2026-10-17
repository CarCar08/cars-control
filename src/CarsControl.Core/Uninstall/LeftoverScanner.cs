using System.Text.RegularExpressions;
using CarsControl.Core.Models;
using CarsControl.Core.Storage;
using Microsoft.Win32;

namespace CarsControl.Core.Uninstall;

public enum LeftoverKind { Folder, RegistryKey, Shortcut }
public enum Confidence { High, Medium, Low }

public sealed record LeftoverCandidate(LeftoverKind Kind, string Path, long SizeBytes, Confidence Confidence, string Reason)
{
    /// <summary>Only High is ticked by default in the review list.</summary>
    public bool Preselected => Confidence == Confidence.High;
}

/// <summary>Which half of the machine to scan: your profile (in-process) or machine-wide locations (helper).</summary>
public enum LeftoverScope { User, Machine }

/// <summary>
/// Finds what an uninstaller left behind. Deliberately conservative:
///   - High: the recorded install folder still exists, or a shortcut points into it / at nothing.
///   - Medium: a folder or registry key named exactly like the app (optionally under its publisher).
///   - Low: a publisher folder whose only contents relate to this app.
/// Never proposes: system or root folders, anything under a Microsoft/Windows/Common Files segment,
/// any folder containing another installed app's install location, or registry keys outside
/// Software\{App} and Software\{Publisher}\{App}.
/// </summary>
public sealed partial class LeftoverScanner(IReadOnlyList<AppEntry> otherInstalledApps)
{
    public IReadOnlyList<LeftoverCandidate> Scan(AppEntry removed, LeftoverScope scope)
    {
        var results = new List<LeftoverCandidate>();
        var name = NormalizeName(removed.Name);
        var publisher = NormalizePublisher(removed.Publisher);
        if (name.Length < 3) return results; // too generic to match safely

        void AddFolder(string path, Confidence c, string reason)
        {
            if (!Directory.Exists(path) || !IsSafeFolder(path)) return;
            if (results.Any(r => r.Kind == LeftoverKind.Folder && (IsUnder(path, r.Path) || string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))) return;
            results.Add(new LeftoverCandidate(LeftoverKind.Folder, path, SafeSize(path), c, reason));
        }

        // 1) The recorded install folder.
        if (removed.InstallLocation is { } loc && IsInScope(loc, scope))
            AddFolder(loc, Confidence.High, "The app's install folder still exists");

        // Another installed app with the same normalized name (e.g. the Notepad++ shell-extension
        // MSIX vs the Notepad++ program): name matching can't tell them apart, so stop at the
        // install-folder and shortcut evidence.
        var nameCollision = otherInstalledApps.Any(o => o.Key != removed.Key && NormalizeName(o.Name) == name);

        // 2) Folders named after the app (optionally under the publisher).
        foreach (var root in nameCollision ? [] : Roots(scope))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirectories(root))
            {
                var folder = NormalizeName(System.IO.Path.GetFileName(dir));
                if (folder == name) AddFolder(dir, Confidence.Medium, $"Folder named \"{System.IO.Path.GetFileName(dir)}\"");
                else if (publisher.Length >= 3 && folder == publisher)
                {
                    foreach (var sub in SafeDirectories(dir))
                        if (NormalizeName(System.IO.Path.GetFileName(sub)) == name)
                            AddFolder(sub, Confidence.Medium, $"Folder \"{System.IO.Path.GetFileName(dir)}\\{System.IO.Path.GetFileName(sub)}\"");
                    // Publisher folder that now contains nothing belonging to another installed app.
                    if (!OtherAppsUnderPublisher(removed, publisher) && SafeDirectories(dir).All(s => NormalizeName(System.IO.Path.GetFileName(s)) == name))
                        AddFolder(dir, Confidence.Low, $"Publisher folder \"{System.IO.Path.GetFileName(dir)}\" with nothing else installed");
                }
            }
        }

        // 2b) Save/settings folders whatever the company is called: Unity games write to
        //     LocalLow\{Company}\{Game} (How to Fish left "Dazed Games\How to Fish" behind), others use
        //     Documents\My Games\{Game} or Saved Games\{Game}. Saves matter, so these are never pre-ticked.
        if (!nameCollision && scope == LeftoverScope.User)
        {
            foreach (var company in SafeDirectories(LocalLow))
                foreach (var sub in SafeDirectories(company))
                    if (NormalizeName(System.IO.Path.GetFileName(sub)) == name)
                        AddFolder(sub, Confidence.Medium, $"Saves and settings in \"{System.IO.Path.GetFileName(company)}\\{System.IO.Path.GetFileName(sub)}\"");
            foreach (var root in SaveRoots())
                foreach (var dir in SafeDirectories(root))
                    if (NormalizeName(System.IO.Path.GetFileName(dir)) == name)
                        AddFolder(dir, Confidence.Medium, $"Your saves, settings or mods in \"{System.IO.Path.GetFileName(root)}\\{System.IO.Path.GetFileName(dir)}\" — keep it unless you're done with it");
        }

        // 2c) Steam's uninstall keeps the shader cache, Workshop downloads and anything you added to the
        //     game folder (mods). Steam's folders are writable by you, so this runs in-process.
        if (scope == LeftoverScope.User && SteamAppId(removed) is { } appId)
        {
            var libraries = GameLibraries.SteamLibraryRoots().Select(l => System.IO.Path.Combine(l, "steamapps")).ToList();
            if (removed.InstallLocation is { } gameDir && libraries.Any(l => IsDirectChild(gameDir, System.IO.Path.Combine(l, "common"))))
                AddSteamFolder(gameDir, Confidence.Medium, "Steam left the game folder behind (usually mods or files you added)");
            foreach (var steamapps in libraries)
            {
                AddSteamFolder(System.IO.Path.Combine(steamapps, "shadercache", appId), Confidence.High, "Steam shader cache for this game");
                AddSteamFolder(System.IO.Path.Combine(steamapps, "compatdata", appId), Confidence.High, "Steam compatibility data for this game");
                AddSteamFolder(System.IO.Path.Combine(steamapps, "workshop", "content", appId), Confidence.Medium, "Steam Workshop items for this game");
            }
        }

        // Steam folders live inside Steam's own install folder, which the generic check protects.
        void AddSteamFolder(string path, Confidence c, string reason)
        {
            if (!Directory.Exists(path) || results.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return;
            }
            catch
            {
                return;
            }
            // Never a folder that holds another installed app or game.
            if (_protectedDirs.Value.Any(d => IsUnder(d, path) || string.Equals(d.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            results.Add(new LeftoverCandidate(LeftoverKind.Folder, path, SafeSize(path), c, reason));
        }

        // 3) Registry: Software\{App} and Software\{Publisher}\{App}.
        foreach (var (hive, view, label) in nameCollision ? [] : RegistryRoots(scope))
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var software = baseKey.OpenSubKey("SOFTWARE");
            if (software is null) continue;
            foreach (var sub in software.GetSubKeyNames())
            {
                var n = NormalizeName(sub);
                if (n == name && !IsProtectedKeyName(sub))
                    results.Add(new LeftoverCandidate(LeftoverKind.RegistryKey, $@"{label}\SOFTWARE\{sub}", 0, Confidence.Medium, $"Registry key named \"{sub}\""));
                else if (publisher.Length >= 3 && n == publisher && !IsProtectedKeyName(sub))
                {
                    using var pub = software.OpenSubKey(sub);
                    foreach (var app in pub?.GetSubKeyNames() ?? [])
                        if (NormalizeName(app) == name)
                            results.Add(new LeftoverCandidate(LeftoverKind.RegistryKey, $@"{label}\SOFTWARE\{sub}\{app}", 0, Confidence.Medium, $"Registry key \"{sub}\\{app}\""));
                }
            }
        }

        // 4) Shortcuts that point into the removed install folder, or are named after the app and broken.
        foreach (var dir in ShortcutRoots(scope).Where(Directory.Exists))
        {
            foreach (var lnk in SafeFiles(dir, "*.lnk"))
            {
                var target = Shortcuts.ResolveTarget(lnk);
                var named = NormalizeName(System.IO.Path.GetFileNameWithoutExtension(lnk)) == name;
                var intoInstall = target is not null && removed.InstallLocation is { } il && IsUnder(target, il);
                var broken = target is not null && !File.Exists(target) && !Directory.Exists(target);
                if (intoInstall || (named && broken))
                    results.Add(new LeftoverCandidate(LeftoverKind.Shortcut, lnk, new FileInfo(lnk).Length, Confidence.High,
                        intoInstall ? "Shortcut into the removed app's folder" : "Broken shortcut named after the app"));
            }
        }

        return results;
    }

    // ---------- safety ----------

    private static readonly string[] ForbiddenSegments = [@"\Microsoft\", @"\Windows\", @"\Common Files\", @"\WindowsApps\", @"\Packages\", @"\Temp\"];

    internal bool IsSafeFolder(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        if (DirectorySizer.IsTooBroadToMeasure(full)) return false;
        if (ForbiddenSegments.Any(s => (full + "\\").Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch
        {
            return false;
        }
        // Must not contain (or be inside) another installed app's files: its install folder, or — for
        // the many ARP entries without InstallLocation — the folder of its icon or uninstaller.
        foreach (var dir in _protectedDirs.Value)
        {
            if (IsUnder(dir, full) || IsUnder(full, dir) || string.Equals(dir, full, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private readonly Lazy<List<string>> _protectedDirs = new(() => ProtectedDirectories(otherInstalledApps));

    internal static List<string> ProtectedDirectories(IEnumerable<AppEntry> apps)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
                // System folders (msiexec, rundll32) host many apps' uninstallers; they are protected elsewhere.
                if (full.Length > 3 && !full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    dirs.Add(full);
            }
            catch
            {
                // Malformed registry paths are ignored.
            }
        }
        foreach (var a in apps)
        {
            Add(a.InstallLocation);
            if (a.DisplayIcon is { } icon) Add(System.IO.Path.GetDirectoryName(icon));
            if (UninstallCommand.Parse(a.UninstallString) is { } u) Add(System.IO.Path.GetDirectoryName(u.Exe));
        }
        return [.. dirs];
    }

    private bool OtherAppsUnderPublisher(AppEntry removed, string publisher) =>
        otherInstalledApps.Any(o => o.Key != removed.Key && NormalizePublisher(o.Publisher) == publisher);

    private static readonly string[] ProtectedKeyNames = ["Microsoft", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "Intel", "NVIDIA Corporation", "ODBC"];

    private static bool IsProtectedKeyName(string sub) => ProtectedKeyNames.Contains(sub, StringComparer.OrdinalIgnoreCase);

    internal static bool IsUnder(string path, string root)
    {
        var p = System.IO.Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var r = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInScope(string path, LeftoverScope scope)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var inProfile = IsUnder(path, profile);
        return scope == LeftoverScope.User ? inProfile : !inProfile;
    }

    // ---------- where to look ----------

    private static string LocalLow => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    private static IEnumerable<string> SaveRoots()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return [docs, System.IO.Path.Combine(docs, "My Games"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games")];
    }

    /// <summary>"Steam App 4001890" → "4001890".</summary>
    internal static string? SteamAppId(AppEntry a) =>
        a.Arp?.KeyName is { } k && k.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) && k["Steam App ".Length..].Trim() is { Length: > 0 } id && id.All(char.IsAsciiDigit)
            ? id
            : null;

    private static bool IsDirectChild(string path, string parent) =>
        string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path).TrimEnd('\\')), System.IO.Path.GetFullPath(parent).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Roots(LeftoverScope scope) => scope == LeftoverScope.User
        ?
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        ]
        :
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];

    private static IEnumerable<(RegistryHive, RegistryView, string)> RegistryRoots(LeftoverScope scope) => scope == LeftoverScope.User
        ? [(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU")]
        : [(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"), (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32")];

    private static IEnumerable<string> ShortcutRoots(LeftoverScope scope) => scope == LeftoverScope.User
        ? [Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)]
        : [Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)];

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); } catch { return []; }
    }

    private static IEnumerable<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 }).ToList(); }
        catch { return []; }
    }

    private static long SafeSize(string path)
    {
        try { return DirectorySizer.Measure(path); } catch { return 0; }
    }

    // ---------- name normalization ----------

    /// <summary>"LocalSend version 1.17.0" → "localsend"; "Notepad++ (64-bit x64)" → "notepad++".</summary>
    internal static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.ToLowerInvariant();
        t = Parenthetical().Replace(t, " ");
        t = VersionTail().Replace(t, " ");
        t = EditionWords().Replace(t, " ");
        t = NonWord().Replace(t, "");
        return t;
    }

    internal static string NormalizePublisher(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = CompanySuffix().Replace(s.ToLowerInvariant(), " ");
        return NonWord().Replace(t, "");
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"\b(version\s*)?v?\d+(\.\d+)+[a-z0-9.-]*\b")]
    private static partial Regex VersionTail();

    [GeneratedRegex(@"\b(x64|x86|64-bit|32-bit|amd64|arm64)\b")]
    private static partial Regex EditionWords();

    [GeneratedRegex(@"[^a-z0-9+]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\b(inc|llc|ltd|gmbh|corp|corporation|co|limited|software|technologies|s\.?a\.?)\b\.?")]
    private static partial Regex CompanySuffix();
}
