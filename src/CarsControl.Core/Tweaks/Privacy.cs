using Microsoft.Win32;

namespace CarsControl.Core.Tweaks;

/// <summary>One app's most recent use of the camera, microphone or location.</summary>
public sealed record CapabilityUse(string Capability, string App, string? ExePath, DateTime LastStarted, DateTime? LastStopped)
{
    public bool InUse => LastStopped is null;
}

/// <summary>
/// "Who used your camera, mic and location": Windows records the last start and stop of every app's use
/// in the capability consent store (the same data as Settings › Privacy › Camera › Recent activity). Read
/// only, from your own registry; nothing is changed or sent anywhere.
/// </summary>
public static class ConsentStore
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static readonly IReadOnlyDictionary<string, string> Capabilities = new Dictionary<string, string>
    {
        ["webcam"] = "Camera",
        ["microphone"] = "Microphone",
        ["location"] = "Location",
    };

    public static IReadOnlyList<CapabilityUse> Read(RegistryKey? hive = null)
    {
        hive ??= Registry.CurrentUser;
        var result = new List<CapabilityUse>();
        foreach (var (key, label) in Capabilities)
        {
            using var cap = hive.OpenSubKey($@"{Root}\{key}");
            if (cap is null) continue;
            foreach (var app in cap.GetSubKeyNames().Where(n => n != "NonPackaged"))
                if (Use(cap, app, label, packaged: true) is { } u) result.Add(u);
            using var nonPackaged = cap.OpenSubKey("NonPackaged");
            foreach (var exe in nonPackaged?.GetSubKeyNames() ?? [])
                if (Use(nonPackaged!, exe, label, packaged: false) is { } u) result.Add(u);
        }
        return result.OrderByDescending(r => r.InUse).ThenByDescending(r => r.LastStarted).ToList();
    }

    private static CapabilityUse? Use(RegistryKey parent, string name, string capability, bool packaged)
    {
        using var k = parent.OpenSubKey(name);
        if (k?.GetValue("LastUsedTimeStart") is not long start || start <= 0) return null;
        var stop = k.GetValue("LastUsedTimeStop") as long? ?? 0;
        DateTime? stopped = stop > 0 ? DateTime.FromFileTime(stop) : null;
        var started = DateTime.FromFileTime(start);
        if (stopped < started) stopped = null; // started again after the last stop: in use now
        if (packaged) return new CapabilityUse(capability, PackageName(name), null, started, stopped);
        var path = name.Replace('#', '\\');
        return new CapabilityUse(capability, Path.GetFileNameWithoutExtension(path), path, started, stopped);
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Microsoft.WindowsCamera".</summary>
    internal static string PackageName(string family)
    {
        var i = family.LastIndexOf('_');
        return i > 0 ? family[..i] : family;
    }
}

/// <summary>A Store app that comes with Windows and can be removed for your account (and reinstalled
/// from the Store any time).</summary>
public sealed record StoreAddOn(string PackageName, string Title, string Why, string PublisherId = "8wekyb3d8bbwe")
{
    /// <summary>Its Store page, to get it back.</summary>
    public string StoreLink => $"ms-windows-store://pdp/?PFN={PackageName}_{PublisherId}";
}

public static class StoreAddOns
{
    public static readonly IReadOnlyList<StoreAddOn> All =
    [
        new("Microsoft.Copilot", "Copilot", "The Copilot app."),
        new("Microsoft.BingSearch", "Bing Search", "Bing web search for the taskbar and Start."),
        new("Microsoft.BingNews", "Microsoft News", "News feed app."),
        new("Microsoft.BingWeather", "Weather", "MSN Weather."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 Copilot", "The Microsoft 365 hub app (Office itself stays)."),
        new("Clipchamp.Clipchamp", "Clipchamp", "Video editor.", "yxz26nhyzhsrt"),
        new("Microsoft.Getstarted", "Tips", "Windows tips."),
        new("Microsoft.GetHelp", "Get Help", "Microsoft support chat."),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Sends feedback to Microsoft."),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Ad-supported card games."),
        new("Microsoft.Windows.DevHome", "Dev Home", "Developer dashboard (retired by Microsoft)."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", "Desktop automation."),
        new("Microsoft.Todos", "Microsoft To Do", "Task lists."),
        new("Microsoft.OutlookForWindows", "Outlook (new)", "The new Outlook app (classic Outlook from Office isn't affected)."),
        new("MSTeams", "Microsoft Teams (free)", "Teams for personal use."),
        new("Microsoft.ZuneMusic", "Media Player", "Windows Media Player (the new one)."),
    ];
}

/// <summary>Finds and removes Store add-ons for your account only (no admin, nothing for other users).</summary>
public static class StoreAddOnManager
{
    /// <summary>Package name → full name, for the add-ons installed for you.</summary>
    public static IReadOnlyDictionary<string, string> Installed()
    {
        var wanted = StoreAddOns.All.Select(a => a.PackageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty))
        {
            try
            {
                if (wanted.Contains(p.Id.Name)) result[p.Id.Name] = p.Id.FullName;
            }
            catch (Exception)
            {
                // A package being serviced can throw on Id; skip it.
            }
        }
        return result;
    }

    /// <summary>
    /// Package name → the name you see ("windows.immersivecontrolpanel" → "Settings"), for <paramref name="packageNames"/> only:
    /// reading DisplayName loads each package's resources, which for every installed package briefly took ~250 MB.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DisplayNames(IEnumerable<string> packageNames)
    {
        var wanted = packageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;
        foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty))
        {
            try
            {
                if (wanted.Contains(p.Id.Name) && !string.IsNullOrWhiteSpace(p.DisplayName) && !p.DisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    result.TryAdd(p.Id.Name, p.DisplayName);
            }
            catch (Exception)
            {
                // Packages being serviced can throw; they keep their package name.
            }
        }
        return result;
    }

    public static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Removes the package for your account. Never waits forever: a removal normally takes a second or two, but on 2026-10-19 Windows
    /// removed News on a family PC within a second while C.A.R.S. kept spinning (the "done" never arrived). So: nothing to do
    /// when it's no longer installed, the call runs on a pool thread, the wait stops after <paramref name="timeout"/>, and the
    /// answer is always what's actually installed afterwards.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RemoveAsync(string fullName, TimeSpan? timeout = null)
    {
        if (!IsInstalledForMe(fullName)) return (true, "Removed.");
        var work = Task.Run(async () =>
        {
            var r = await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(fullName);
            return r.IsRegistered ? (false, r.ErrorText) : (true, "Removed.");
        });
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // a late failure isn't unobserved
        // Windows' "done" doesn't always come back (on a family PC News was gone within a second while C.A.R.S. kept waiting):
        // look every 2 seconds whether the package is gone, and stop at the first of: done, gone, or the time limit.
        var deadline = DateTime.UtcNow + (timeout ?? RemoveTimeout);
        while (!work.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2)));
            if (!work.IsCompleted && !IsInstalledForMe(fullName)) return (true, "Removed.");
        }
        if (!work.IsCompleted)
        {
            return IsInstalledForMe(fullName)
                ? (false, "Windows hasn't done it yet. It installs and removes apps one at a time, and another one (often a Microsoft Store update) is still going. It may still finish on its own; check this list again in a few minutes.")
                : (true, "Removed.");
        }
        try
        {
            var (ok, message) = await work;
            return ok || !IsInstalledForMe(fullName) ? (true, "Removed.") : (false, message);
        }
        catch (Exception ex)
        {
            return IsInstalledForMe(fullName) ? (false, ex.Message) : (true, "Removed.");
        }
    }

    /// <summary>Whether this exact package is still installed for your account. When Windows can't say, it counts as installed.</summary>
    public static bool IsInstalledForMe(string fullName)
    {
        try
        {
            return new Windows.Management.Deployment.PackageManager().FindPackageForUser(string.Empty, fullName) is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
