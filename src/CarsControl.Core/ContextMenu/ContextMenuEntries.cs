using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CarsControl.Core.ContextMenu;

public enum MenuPlace { Files, Everything, Folders, FolderBackground, Drives }

public enum MenuEntryKind { Verb, Handler }

/// <summary>One item (or the extension behind several items) of Explorer's right-click menu.</summary>
public sealed record MenuEntry(
    string Id,
    MenuPlace Place,
    MenuEntryKind Kind,
    string Name,
    string? Command,
    string? Owner,
    bool Hidden,
    string? Clsid,
    bool IsOurs,
    string? Warning)
{
    public string PlaceText => ContextMenuEntries.PlaceName(Place);
    public string KindText => Kind == MenuEntryKind.Verb ? "Menu item" : "Extension";
    public bool Locked => Inventory.ProtectedApps.IsAlwaysProtectedName(Name) || Inventory.ProtectedApps.IsAlwaysProtectedName(Command)
        || Inventory.ProtectedApps.IsAlwaysProtectedName(Owner) || Warning?.StartsWith("C.A.R.S. leaves", StringComparison.Ordinal) == true;
    public override string ToString() => $"{Name} ({PlaceText}, {(Hidden ? "hidden" : "shown")})";
}

/// <summary>
/// Explorer's right-click menu is assembled from "verbs" (shell\NAME\command) and "handlers" (extensions listed
/// under shellex\ContextMenuHandlers). Hiding is done per user and is fully reversible:
/// a verb gets a <c>LegacyDisable</c> value in your own registry hive, an extension's CLSID goes on your own
/// "Blocked" list. Nothing is deleted and no admin rights are involved.
/// </summary>
public static class ContextMenuEntries
{
    public static string PlaceName(MenuPlace p) => p switch
    {
        MenuPlace.Files => "Files",
        MenuPlace.Everything => "Files and folders",
        MenuPlace.Folders => "Folders",
        MenuPlace.FolderBackground => "Empty space in a folder",
        _ => "Drives",
    };

    private static readonly (MenuPlace Place, string Root)[] Places =
    [
        (MenuPlace.Files, @"*"),
        (MenuPlace.Everything, @"AllFilesystemObjects"),
        (MenuPlace.Folders, @"Directory"),
        (MenuPlace.FolderBackground, @"Directory\Background"),
        (MenuPlace.Drives, @"Drive"),
    ];

    public const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    public const string MachineBlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    // ---------- reading ----------

    public static IReadOnlyList<MenuEntry> Read()
    {
        var list = new List<MenuEntry>();
        var blocked = BlockedClsids();
        foreach (var (place, root) in Places)
        {
            ReadVerbs(list, place, root);
            ReadHandlers(list, place, root, blocked);
        }
        return list.OrderBy(e => e.Place).ThenBy(e => e.Hidden ? 1 : 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadVerbs(List<MenuEntry> into, MenuPlace place, string root)
    {
        try
        {
            using var shell = Registry.ClassesRoot.OpenSubKey(root + @"\shell");
            if (shell is null) return;
            foreach (var verb in shell.GetSubKeyNames())
            {
                using var k = shell.OpenSubKey(verb);
                if (k is null) continue;
                var command = (k.OpenSubKey("command")?.GetValue("") as string)?.Trim();
                var exe = Startup.CommandLines.Executable(command);
                var owner = Startup.StartupReader.PublisherOf(exe);
                var label = DisplayName(k, verb);
                // A verb with no command and no subcommands is a leftover; Windows ignores it too.
                if (command is null && k.GetValue("SubCommands") is null && k.GetValue("ExplorerCommandHandler") is null) continue;
                var ours = verb.StartsWith(CarsEntries.Prefix, StringComparison.Ordinal);
                var hidden = k.GetValue("LegacyDisable") is not null || k.GetValue("ProgrammaticAccessOnly") is not null;
                into.Add(new MenuEntry($"verb:{place}:{verb}", place, MenuEntryKind.Verb, label, command, owner, hidden, null, ours, Warn(label, owner, command)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not listed.
        }
    }

    private static void ReadHandlers(List<MenuEntry> into, MenuPlace place, string root, HashSet<string> blocked)
    {
        try
        {
            using var handlers = Registry.ClassesRoot.OpenSubKey(root + @"\shellex\ContextMenuHandlers");
            if (handlers is null) return;
            foreach (var name in handlers.GetSubKeyNames())
            {
                using var k = handlers.OpenSubKey(name);
                var clsid = (k?.GetValue("") as string)?.Trim();
                if (clsid is null || !IsClsid(clsid)) clsid = IsClsid(name) ? name : null;
                if (clsid is null) continue;
                var (friendly, dll) = Describe(clsid);
                var owner = Startup.StartupReader.PublisherOf(dll);
                var label = !string.IsNullOrWhiteSpace(friendly) ? friendly! : name;
                if (!label.Equals(name, StringComparison.OrdinalIgnoreCase) && !IsClsid(name)) label = $"{label} ({name})";
                into.Add(new MenuEntry($"handler:{place}:{name}", place, MenuEntryKind.Handler, label, dll, owner, blocked.Contains(clsid), clsid, false, Warn(label, owner, dll)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not listed.
        }
    }

    private static string DisplayName(RegistryKey verbKey, string verbName)
    {
        var mui = verbKey.GetValue("MUIVerb") as string;
        var def = verbKey.GetValue("") as string;
        var text = new[] { mui, def }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith('@'));
        return string.IsNullOrWhiteSpace(text) ? verbName : text.Replace("&", "");
    }

    private static (string? Friendly, string? Dll) Describe(string clsid)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid);
            var friendly = k?.GetValue("") as string;
            var dll = Environment.ExpandEnvironmentVariables((k?.OpenSubKey("InprocServer32")?.GetValue("") as string) ?? "");
            return (friendly, string.IsNullOrWhiteSpace(dll) ? null : dll);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>Things to think twice about: C.A.R.S. won't touch Nilesoft Shell (it draws the whole menu).</summary>
    public static string? Warn(string name, string? owner, string? command)
    {
        var text = $"{name} {owner} {command}";
        if (text.Contains("Nilesoft", StringComparison.OrdinalIgnoreCase)) return "C.A.R.S. leaves Nilesoft Shell alone: it draws your whole right-click menu.";
        if (text.Contains("Windows.Shell", StringComparison.OrdinalIgnoreCase) || (owner?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true && name.Contains("New", StringComparison.OrdinalIgnoreCase) && name.Length <= 4))
            return "Part of Windows' own menu.";
        return null;
    }

    public static bool IsClsid(string s) => Regex.IsMatch(s, @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");

    private static HashSet<string> BlockedClsids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, key) in new[] { (Registry.CurrentUser, BlockedKey), (Registry.LocalMachine, MachineBlockedKey) })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k is not null) foreach (var n in k.GetValueNames()) set.Add(n);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable: nothing known to be blocked there.
            }
        }
        return set;
    }

    // ---------- hiding and showing ----------

    /// <summary>Hides or shows an entry for your account only. Returns false when it isn't something that can be changed.</summary>
    public static bool SetHidden(MenuEntry entry, bool hidden)
    {
        if (entry.Locked) return false;
        if (entry.Kind == MenuEntryKind.Handler)
        {
            if (entry.Clsid is not { } clsid || !IsClsid(clsid)) return false;
            using var k = Registry.CurrentUser.CreateSubKey(BlockedKey, writable: true);
            if (hidden) k.SetValue(clsid, "", RegistryValueKind.String);
            else k.DeleteValue(clsid, throwOnMissingValue: false);
            return true;
        }

        var id = entry.Id.Split(':', 3);
        if (id.Length != 3 || !Enum.TryParse<MenuPlace>(id[1], out var place) || !SafeVerb(id[2])) return false;
        var root = Places.First(p => p.Place == place).Root;
        // The verb's own key in your hive merges with the machine one, so the value hides it without editing the machine's copy.
        using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{root}\shell\{id[2]}", writable: true);
        if (hidden) verb.SetValue("LegacyDisable", "", RegistryValueKind.String);
        else
        {
            verb.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            // A key this call created just to carry the value is removed again, so nothing stays behind.
            if (verb.ValueCount == 0 && verb.SubKeyCount == 0)
            {
                verb.Close();
                try { Registry.CurrentUser.DeleteSubKey($@"Software\Classes\{root}\shell\{id[2]}", throwOnMissingSubKey: false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* in use: harmless */ }
            }
        }
        return true;
    }

    public static bool SafeVerb(string verb) => verb.Length is > 0 and <= 120 && verb.IndexOfAny(['\\', '/', '\0']) < 0 && !verb.Contains("..");
}

/// <summary>The few right-click items C.A.R.S. itself can add (all under your own registry hive).</summary>
public static class CarsEntries
{
    public const string Prefix = "CarsControl.";

    public sealed record Spec(string KeySuffix, string Label, string Arguments);

    /// <summary>Where each lives and what it runs. %1 is the clicked folder or drive, %V the folder whose empty space was clicked.</summary>
    public static readonly IReadOnlyList<(string Root, Spec Spec)> All =
    [
        (@"Directory", new Spec(Prefix + "FolderSize", "Folder size in C.A.R.S.", "--explore \"%1\"")),
        (@"Directory\Background", new Spec(Prefix + "FolderSize", "Folder size in C.A.R.S.", "--explore \"%V\"")),
        (@"Drive", new Spec(Prefix + "ScanDrive", "Scan this drive in C.A.R.S.", "--explore \"%1\"")),
    ];

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\Directory\shell\{Prefix}FolderSize");
        return k is not null;
    }

    public static void Install(string exePath)
    {
        foreach (var (root, spec) in All)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{root}\shell\{spec.KeySuffix}", writable: true);
            k.SetValue("", spec.Label);
            k.SetValue("Icon", $"\"{exePath}\"");
            using var c = k.CreateSubKey("command", writable: true);
            c.SetValue("", $"\"{exePath}\" {spec.Arguments}");
        }
    }

    public static void Remove()
    {
        foreach (var (root, spec) in All)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{root}\shell\{spec.KeySuffix}", throwOnMissingSubKey: false); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* in use: harmless */ }
        }
    }
}

/// <summary>The argument a right-click entry hands to the app: checked before anything is opened.</summary>
public static class ExploreArgument
{
    public const string Prefix = "explore:";

    /// <summary>A real folder or drive root on this PC, as a full path. No relative parts, no device paths, no files.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.Contains('\0')) return null;
        path = path.Trim().Trim('"');
        if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || path.StartsWith(@"\\")) return null;
        if (!Path.IsPathFullyQualified(path) || path.Split('\\', '/').Any(s => s == "..")) return null;
        var full = Path.GetFullPath(path);
        return Directory.Exists(full) ? full : null;
    }

    public static string ToActivation(string path) => Prefix + path;

    public static string? FromActivation(string? text) => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal) ? Validate(text[Prefix.Length..]) : null;
}
