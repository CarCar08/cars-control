using System.Text.RegularExpressions;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Settings;
using Microsoft.Win32;

namespace CarsControl.Core.Health;

public enum RiskyChange { DriverUpdate, FeatureUpdate, PrivacyBatch, DriverCleanup, OldWindowsRemoval }

/// <summary>Made: there's a restore point now. NotWanted/NoProtection: none was made and that's expected. Cancelled: the admin prompt
/// was declined or no admin session is open. Failed: Windows or the helper wouldn't.</summary>
public enum RestoreOutcome { Made, NotWanted, NoProtection, Cancelled, Failed }

public sealed record RestoreResult(RestoreOutcome Outcome, string? Message = null)
{
    public bool Made => Outcome == RestoreOutcome.Made;
}

/// <summary>Whether Windows System Protection is on, so a restore point can be made before a risky change.</summary>
public sealed record SafetyNetState(bool ProtectionOn, bool BlockedByPolicy)
{
    public bool CanCreate => ProtectionOn && !BlockedByPolicy;
}

/// <summary>A restore point before the changes that are hard to undo by hand.</summary>
public static partial class SafetyNet
{
    public static SafetyNetState Read()
    {
        try
        {
            using var sr = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            // RPSessionInterval is 1 while System Protection is on and 0 when it's off for the machine.
            var on = sr?.GetValue("RPSessionInterval") is 1;
            using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
            var blocked = policy?.GetValue("DisableSR") is 1;
            return new SafetyNetState(on, blocked);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new SafetyNetState(false, false);
        }
    }

    public static bool Wanted(HealthSettings settings, RiskyChange change) => settings.RestorePointBeforeRiskyChanges;

    /// <summary>Windows names its big yearly upgrade "Feature update to Windows 11, version 25H2".</summary>
    public static bool IsFeatureUpdate(string? title) => title?.StartsWith("Feature update", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A restore point before a risky change, when you want one and System Protection is on. Never throws for the
    /// cases a caller should decide about: a declined prompt and an unavailable helper come back as an outcome.
    /// </summary>
    public static async Task<RestoreResult> MakeAsync(IHelperChannel helper, HealthSettings settings, SafetyNetState protection, RiskyChange change,
        string? detail, bool dryRun = false, CancellationToken ct = default)
    {
        if (!Wanted(settings, change)) return new RestoreResult(RestoreOutcome.NotWanted);
        if (!protection.CanCreate) return new RestoreResult(RestoreOutcome.NoProtection, "No restore point was made: System Protection is off.");
        try
        {
            var r = await helper.SendAsync(new CreateRestorePointOp(Description(change, detail)), null, dryRun, ct);
            if (r.Ok) return new RestoreResult(RestoreOutcome.Made, r.Message);
            return r.Status == OpStatus.Cancelled
                ? new RestoreResult(RestoreOutcome.Cancelled, r.Message)
                : new RestoreResult(RestoreOutcome.Failed, r.Message);
        }
        catch (HelperCancelledException)
        {
            return new RestoreResult(RestoreOutcome.Cancelled, "Admin approval was cancelled.");
        }
        catch (HelperSessionClosedException)
        {
            return new RestoreResult(RestoreOutcome.Cancelled, "No admin session is open.");
        }
        catch (HelperUnavailableException ex)
        {
            return new RestoreResult(RestoreOutcome.Failed, ex.Message);
        }
    }

    /// <summary>The restore point's name. The helper accepts only letters, digits, spaces and . , : ( ) _ - up to 64 characters.</summary>
    public static string Description(RiskyChange change, string? detail = null)
    {
        var what = change switch
        {
            RiskyChange.DriverUpdate => "driver update",
            RiskyChange.FeatureUpdate => "feature update",
            RiskyChange.PrivacyBatch => "privacy changes",
            RiskyChange.DriverCleanup => "driver cleanup",
            _ => "old Windows removal",
        };
        var text = "C.A.R.S. before " + what + (string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail);
        text = Unsafe().Replace(text, " ").Trim();
        return text.Length > 64 ? text[..64].TrimEnd() : text;
    }

    [GeneratedRegex(@"[^A-Za-z0-9 .,:()_-]+")]
    private static partial Regex Unsafe();
}
