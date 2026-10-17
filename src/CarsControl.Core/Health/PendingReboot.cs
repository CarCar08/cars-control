using CarsControl.Core.Settings;
using Microsoft.Win32;

namespace CarsControl.Core.Health;

/// <summary>Why Windows wants a restart, from keys every account can read. <see cref="Reasons"/> are real
/// (an update needs one); <see cref="Minor"/> ones, like files waiting to be replaced, are left behind by many
/// installers and don't make a restart urgent.</summary>
public sealed record PendingRebootState(IReadOnlyList<string> Reasons, IReadOnlyList<string>? Minor = null)
{
    public bool Pending => Reasons.Count > 0;

    public string Summary =>
        Pending ? "Windows is waiting for a restart: " + string.Join("; ", Reasons) + "."
        : Minor is { Count: > 0 } ? "No update needs a restart (" + string.Join("; ", Minor) + " at the next one)."
        : "No restart is waiting.";
}

public static class PendingReboot
{
    public static PendingRebootState Read()
    {
        var reasons = new List<string>();
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("a Windows component update is installed");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update finished installing");
        return new PendingRebootState(reasons, HasFileRenames() ? ["some files will be replaced"] : []);
    }

    private static bool KeyExists(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool HasFileRenames()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return key?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 } list && list.Any(s => !string.IsNullOrWhiteSpace(s));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

public enum RestartDecision
{
    /// <summary>Nothing to do.</summary>
    Wait,
    /// <summary>Start the 5-minute countdown now.</summary>
    Start,
}

public sealed record RestartCheck(RestartDecision Decision, string Why);

/// <summary>
/// Decides when a pending restart may happen unattended: only in the window you chose, only when a restart is
/// actually waiting, you've been away a while, no game is running and nothing is full screen.
/// </summary>
public static class RestartPlanner
{
    public static readonly TimeSpan Countdown = TimeSpan.FromMinutes(5);

    public static RestartCheck Decide(RestartSettings s, DateTime now, bool pending, TimeSpan idle, bool gaming, bool fullScreen, DateTime? lastAttempt)
    {
        if (!s.Enabled) return new(RestartDecision.Wait, "Restart-when-away is off.");
        if (!pending) return new(RestartDecision.Wait, "No restart is waiting.");
        if (!TimeSpan.TryParse(s.Time, out var at)) return new(RestartDecision.Wait, "The restart time isn't valid.");

        var windowStart = now.Date + at;
        if (now < windowStart) windowStart = windowStart.AddDays(-1);
        var windowEnd = windowStart.AddHours(Math.Clamp(s.WindowHours, 1, 12));
        if (now < windowStart || now >= windowEnd) return new(RestartDecision.Wait, $"Outside the {s.Time} window.");
        if (lastAttempt is { } last && last >= windowStart) return new(RestartDecision.Wait, "Already tried in this window.");
        if (gaming) return new(RestartDecision.Wait, "A game is running.");
        if (fullScreen) return new(RestartDecision.Wait, "Something is full screen.");
        if (idle < TimeSpan.FromMinutes(Math.Max(1, s.IdleMinutes))) return new(RestartDecision.Wait, $"You were active {idle.TotalMinutes:0} min ago.");
        return new(RestartDecision.Start, "A restart is waiting and you're away.");
    }

    /// <summary>The fixed command lines for the countdown: `shutdown /r /t 300` starts it and `shutdown /a` cancels it.</summary>
    public static IReadOnlyList<string> StartArguments(string reason) => ["/r", "/t", ((int)Countdown.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "/c", reason];

    public static IReadOnlyList<string> CancelArguments => ["/a"];
}
