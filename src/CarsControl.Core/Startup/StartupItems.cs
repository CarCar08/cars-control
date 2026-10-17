using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CarsControl.Core.Startup;

public enum StartupKind { Run, Folder, Service, Task }

public enum StartupScope { You, Everyone }

/// <summary>One thing that starts by itself: a Run entry, a Startup-folder shortcut, a service or a scheduled task.</summary>
public sealed record StartupItem(
    string Id,
    StartupKind Kind,
    StartupScope Scope,
    string Name,
    string Command,
    string? Exe,
    string? Publisher,
    bool Enabled,
    string Where,
    string? Note = null)
{
    /// <summary>Changing it needs the admin helper (machine-wide Run entries, services, other accounts' tasks).</summary>
    public bool NeedsAdmin => Kind == StartupKind.Service || Scope == StartupScope.Everyone;

    public string KindText => Kind switch { StartupKind.Run => "Startup entry", StartupKind.Folder => "Startup folder", StartupKind.Service => "Service", _ => "Scheduled task" };
    public override string ToString() => $"{Name} ({KindText}, {(Enabled ? "on" : "off")})";
}

/// <summary>
/// Task Manager's own bookkeeping for the Startup tab: "StartupApproved" values hold 12 bytes per entry, the
/// first byte even (02, 06) when it runs and odd (03, 07) when disabled, then when it was disabled as a FILETIME.
/// Using the same values means Task Manager and C.A.R.S. always agree.
/// </summary>
public static class StartupApproval
{
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 1) == 0;

    public static byte[] Enabled() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Disabled(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    public const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string MachineExplorerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
}

public static class CommandLines
{
    /// <summary>The program in a start-up command: <c>"C:\Program Files\X\x.exe" --tray</c> → the path in quotes; <c>C:\X\x.exe /s</c> → up to ".exe".</summary>
    public static string? Executable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = Regex.Match(c, @"^(.+?\.(?:exe|com|bat|cmd|lnk))(?:\s|$)", RegexOptions.IgnoreCase);
        if (exe.Success) return exe.Groups[1].Value;
        var space = c.IndexOf(' ');
        return space < 0 ? c : c[..space];
    }
}

/// <summary>Reads what starts with Windows. Everything here works as a standard user.</summary>
public static class StartupReader
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineRun32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static IReadOnlyList<StartupItem> ReadRunAndFolders()
    {
        var list = new List<StartupItem>();
        ReadRun(list, Registry.CurrentUser, RunKey, StartupApproval.ExplorerKey + @"\Run", Registry.CurrentUser, "HKCU", StartupScope.You, "run:HKCU:");
        ReadRun(list, Registry.LocalMachine, MachineRunKey, StartupApproval.MachineExplorerKey + @"\Run", Registry.LocalMachine, "HKLM", StartupScope.Everyone, "run:HKLM:");
        ReadRun(list, Registry.LocalMachine, MachineRun32Key, StartupApproval.MachineExplorerKey + @"\Run32", Registry.LocalMachine, "HKLM (32-bit)", StartupScope.Everyone, "run:HKLM32:");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupApproval.ExplorerKey + @"\StartupFolder", StartupScope.You, "folder:user:");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, StartupApproval.MachineExplorerKey + @"\StartupFolder", StartupScope.Everyone, "folder:common:");
        return list.OrderBy(i => i.Enabled ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupItem> into, RegistryKey hive, string runKey, string approvedKey, RegistryKey approvalHive, string label, StartupScope scope, string idPrefix)
    {
        try
        {
            using var run = hive.OpenSubKey(runKey);
            if (run is null) return;
            using var approved = approvalHive.OpenSubKey(approvedKey);
            foreach (var name in run.GetValueNames())
            {
                if (run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(name)) continue;
                var exe = CommandLines.Executable(command);
                into.Add(new StartupItem(idPrefix + name, StartupKind.Run, scope, name, command, exe, Publisher(exe), StartupApproval.IsEnabled(approved?.GetValue(name) as byte[]), label));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable key is just not listed.
        }
    }

    private static void ReadFolder(List<StartupItem> into, string folder, RegistryKey approvalHive, string approvedKey, StartupScope scope, string idPrefix)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            using var approved = approvalHive.OpenSubKey(approvedKey);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var exe = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                into.Add(new StartupItem(idPrefix + name, StartupKind.Folder, scope, Path.GetFileNameWithoutExtension(name), file, exe, Publisher(exe), StartupApproval.IsEnabled(approved?.GetValue(name) as byte[]),
                    scope == StartupScope.You ? "Your Startup folder" : "Startup folder (all users)"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not listed.
        }
    }

    // ---------- services ----------

    /// <summary>Automatic services that aren't Microsoft's, with the program behind them.</summary>
    public static IReadOnlyList<StartupItem> ReadServices()
    {
        var list = new List<StartupItem>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, DisplayName, PathName, StartMode, State, StartName, ServiceType, DelayedAutoStart FROM Win32_Service");
            foreach (var o in searcher.Get().Cast<ManagementBaseObject>())
            {
                var mode = o["StartMode"]?.ToString() ?? "";
                if (mode is not ("Auto" or "Manual" or "Disabled")) continue; // boot/system start modes are drivers
                var name = o["Name"]?.ToString() ?? "";
                var exe = CommandLines.Executable(o["PathName"]?.ToString());
                var publisher = Publisher(exe);
                if (!ServiceRules.IsListable(name, exe, publisher)) continue;
                if (mode == "Manual" && !(o["State"]?.ToString() == "Running")) continue; // only what runs or starts itself matters here
                var delayed = o["DelayedAutoStart"] is true;
                list.Add(new StartupItem("svc:" + name, StartupKind.Service, StartupScope.Everyone, o["DisplayName"]?.ToString() is { Length: > 0 } d ? d : name, o["PathName"]?.ToString() ?? "", exe, publisher,
                    mode != "Disabled", $"Service {name}" + (mode == "Manual" ? " (starts on demand, running now)" : delayed ? " (starts shortly after sign-in)" : ""),
                    ServiceRules.IsProtected(name, publisher) ? "C.A.R.S. won't change this one: something on this PC depends on it." : null));
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Empty means "couldn't read".
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- scheduled tasks ----------

    /// <summary>Tasks outside \Microsoft\ that start at sign-in or boot (Task Scheduler, as far as this account may see).</summary>
    public static IReadOnlyList<StartupItem> ReadTasks()
    {
        var list = new List<StartupItem>();
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            Walk(service.GetFolder("\\"), list);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
        {
            // No Task Scheduler access: no tasks listed.
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(dynamic folder, List<StartupItem> into)
    {
        string path = folder.Path;
        if (TaskRules.IsMicrosoftPath(path)) return;
        foreach (dynamic task in folder.GetTasks(1))
        {
            try
            {
                string taskPath = task.Path;
                if (!TaskRules.IsListable(taskPath)) continue;
                var triggers = new List<int>();
                foreach (dynamic t in task.Definition.Triggers) triggers.Add((int)t.Type);
                if (!triggers.Any(t => t is 8 or 9)) continue; // boot / logon
                string command = "";
                foreach (dynamic a in task.Definition.Actions)
                {
                    if ((int)a.Type != 0) continue;
                    command = ((string)a.Path + " " + (string?)a.Arguments).Trim();
                    break;
                }
                var exe = CommandLines.Executable(command);
                string? runAs = null;
                try { runAs = (string?)task.Definition.Principal.UserId; } catch { /* hidden */ }
                var everyone = runAs is not null && (runAs.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || runAs.Contains("SERVICE", StringComparison.OrdinalIgnoreCase) || runAs.StartsWith("S-1-5-18"));
                into.Add(new StartupItem("task:" + taskPath, StartupKind.Task, everyone ? StartupScope.Everyone : StartupScope.You, (string)task.Name, command, exe, Publisher(exe), (bool)task.Enabled,
                    $"Scheduled task, runs {(triggers.Contains(8) ? "at boot" : "at sign-in")}" + (runAs is null ? "" : $" as {runAs}")));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
            {
                // A task this account may not read.
            }
        }
        foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, into);
    }

    // ---------- details ----------

    private static readonly Dictionary<string, string?> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? PublisherOf(string? exe) => Publisher(exe);

    internal static string? Publisher(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        lock (PublisherCache)
        {
            if (PublisherCache.TryGetValue(exe, out var cached)) return cached;
        }
        string? company = null;
        try
        {
            if (File.Exists(exe)) company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            company = null;
        }
        company = string.IsNullOrWhiteSpace(company) ? null : company;
        lock (PublisherCache) PublisherCache[exe] = company;
        return company;
    }

    /// <summary>A .lnk's target through the shell's own COM object.</summary>
    internal static string? ShortcutTarget(string lnk)
    {
        try
        {
            using var com = new Util.ComScope();
            dynamic shell = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!);
            dynamic link = com.Own(shell.CreateShortcut(lnk));
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? lnk : target;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
        {
            return lnk;
        }
    }
}

/// <summary>Which services C.A.R.S. lists and which it will change.</summary>
public static partial class ServiceRules
{
    [GeneratedRegex(@"^[A-Za-z0-9_.\-$]{1,80}$")]
    private static partial Regex ValidName();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>Microsoft's own services, and anything running from the Windows folder, are Windows' business.</summary>
    public static bool IsListable(string name, string? exe, string? publisher)
    {
        if (!IsValidName(name)) return false;
        if (publisher is not null && publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        if (exe is not null)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (exe.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static readonly string[] ProtectedNamePrefixes =
    [
        "SaladBowl", "Salad", // earns money: never touched
        "Tailscale", "ProtonVPN", "Proton", "WireGuard", // the VPN and the tailnet this PC relies on
        "NVDisplay", "NvContainer", "nvagent", // the graphics driver's control services
        "WinDefend", "Sense", "SecurityHealth", "wscsvc", "MpsSvc", // security (also Microsoft's, so normally not listed)
        "CarsControl", "CARS", // this app
        "Cryptomator", "PawnIO", "ExitNodeGuard",
        "LibreHardwareMonitor", "sshd", "ssh-agent",
        "AnyDesk", "Parsec", // remote access this PC may be relied on for
    ];

    /// <summary>Services C.A.R.S. lists but won't change: ending them could lock you out or cost money.</summary>
    public static bool IsProtected(string name, string? publisher) =>
        ProtectedNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || (publisher?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true && name.Contains("Display", StringComparison.OrdinalIgnoreCase));

    /// <summary>"auto" | "manual" | "disabled" → the sc.exe word, or null.</summary>
    public static string? ScStartType(string startType) => startType switch { "auto" => "auto", "manual" => "demand", "disabled" => "disabled", _ => null };
}

public static partial class TaskRules
{
    public static bool MayChangeDefinition(string xml)
    {
        try
        {
            using var input = new StringReader(xml);
            using var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var elements = System.Xml.Linq.XDocument.Load(reader).Descendants().ToList();
            // A task needs at least one action we can read: a program (Exec/Command) or a COM handler (a class id, which
            // can't be Salad's). Anything else is left alone.
            return elements.Any(e => e.Name.LocalName is "Command" or "ClassId")
                && elements.Where(e => e.Name.LocalName is "Command" or "Arguments").All(e => !Inventory.ProtectedApps.IsAlwaysProtectedName(e.Value));
        }
        catch (System.Xml.XmlException) { return false; }
    }

    [GeneratedRegex(@"^\\[A-Za-z0-9 _.\-()\\]{1,200}$")]
    private static partial Regex ValidPath();

    public static bool IsMicrosoftPath(string path) => path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A task C.A.R.S. may list and toggle: a plain path, not Microsoft's, and not this app's own.</summary>
    public static bool IsListable(string taskPath) =>
        ValidPath().IsMatch(taskPath) && !IsMicrosoftPath(taskPath) && !taskPath.StartsWith(@"\CarsControl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tasks other tools depend on; listed, not changeable.</summary>
    public static bool IsProtected(string taskPath) =>
        new[] { "ExitNodeGuard", "SaladBowl", "Salad", "Tailscale", "Proton" }.Any(p => taskPath.Contains(p, StringComparison.OrdinalIgnoreCase));
}
