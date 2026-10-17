using System.Globalization;
using Microsoft.Win32;

namespace CarsControl.Core.Health;

/// <summary>One registry change the helper makes for a Windows Update setting. A null <see cref="Value"/> deletes the value.</summary>
public sealed record PolicyWrite(string SubKey, string Name, RegistryValueKind Kind, object? Value);

public sealed record PolicyPlan(bool Valid, string? Error, IReadOnlyList<PolicyWrite> Writes)
{
    public static PolicyPlan Reject(string why) => new(false, why, []);
    public static PolicyPlan Of(params PolicyWrite[] writes) => new(true, null, writes);
}

/// <summary>What Windows Update is currently set to (all readable without admin).</summary>
public sealed record WindowsUpdateState(DateTime? PausedUntilUtc, bool ExcludeDrivers, int? DeferFeatureDays, int? ActiveHoursStart, int? ActiveHoursEnd, bool PausedByGamingMode)
{
    public bool IsPaused(DateTime utcNow) => PausedUntilUtc is { } u && u > utcNow;
}

/// <summary>
/// The Windows Update settings C.A.R.S. may change, and exactly which registry values each one touches.
/// <see cref="Plan"/> turns (setting, value) into a fixed list of writes, or rejects it; the helper does nothing else.
/// </summary>
public static class WindowsUpdatePolicy
{
    public const string UxSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string GamingMarker = "CarsControlGamingPause";
    public const int MaxPauseDays = 35;

    private static readonly string[] PauseValues =
    [
        "PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime",
        "PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime",
    ];

    public static readonly IReadOnlyList<string> Settings = ["pause-until", "exclude-drivers", "defer-feature-days", "active-hours"];

    /// <summary>Windows stores pause times as UTC "2026-10-13T03:00:00Z".</summary>
    public static string PauseTime(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static PolicyPlan Plan(string setting, string value, DateTime utcNow)
    {
        value = value.Trim();
        switch (setting)
        {
            case "pause-until" when value == "resume":
                return PolicyPlan.Of(PauseValues.Select(n => Delete(UxSettings, n)).ToArray());

            case "pause-until":
                if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return PolicyPlan.Reject("Pick a date (yyyy-MM-dd), or \"resume\".");
                // Windows Settings pauses until a local midnight; an end-of-day UTC expiry is close enough and never early.
                var until = DateTime.SpecifyKind(day.Date.AddHours(23).AddMinutes(59), DateTimeKind.Local).ToUniversalTime();
                if (until <= utcNow) return PolicyPlan.Reject("That date has already passed.");
                if (until > utcNow.AddDays(MaxPauseDays + 1)) return PolicyPlan.Reject($"Windows Update can be paused for at most {MaxPauseDays} days.");
                return PolicyPlan.Of(PauseValues.Select(n => Set(UxSettings, n, RegistryValueKind.String, n.Contains("Start") ? PauseTime(utcNow) : PauseTime(until))).ToArray());

            case "exclude-drivers" when value is "on":
                return PolicyPlan.Of(Set(PolicyKey, "ExcludeWUDriversInQualityUpdate", RegistryValueKind.DWord, 1));
            case "exclude-drivers" when value is "default":
                return PolicyPlan.Of(Delete(PolicyKey, "ExcludeWUDriversInQualityUpdate"));

            case "defer-feature-days" when value == "default":
                return PolicyPlan.Of(Delete(PolicyKey, "DeferFeatureUpdates"), Delete(PolicyKey, "DeferFeatureUpdatesPeriodInDays"));
            case "defer-feature-days":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 0 or > 365)
                    return PolicyPlan.Reject("Feature updates can be deferred by 0 to 365 days.");
                return days == 0
                    ? PolicyPlan.Of(Delete(PolicyKey, "DeferFeatureUpdates"), Delete(PolicyKey, "DeferFeatureUpdatesPeriodInDays"))
                    : PolicyPlan.Of(Set(PolicyKey, "DeferFeatureUpdates", RegistryValueKind.DWord, 1), Set(PolicyKey, "DeferFeatureUpdatesPeriodInDays", RegistryValueKind.DWord, days));

            case "active-hours" when value == "default":
                return PolicyPlan.Of(Delete(UxSettings, "ActiveHoursStart"), Delete(UxSettings, "ActiveHoursEnd"), Set(UxSettings, "SmartActiveHoursState", RegistryValueKind.DWord, 1));
            case "active-hours":
                var parts = value.Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                    || first is < 0 or > 23 || last is < 0 or > 23)
                    return PolicyPlan.Reject("Active hours look like 8-23 (hours 0 to 23).");
                // Windows allows at most 18 hours between start and end.
                if (((last - first + 24) % 24) is 0 or > 18) return PolicyPlan.Reject("Active hours can span 1 to 18 hours.");
                return PolicyPlan.Of(Set(UxSettings, "ActiveHoursStart", RegistryValueKind.DWord, first), Set(UxSettings, "ActiveHoursEnd", RegistryValueKind.DWord, last),
                    Set(UxSettings, "SmartActiveHoursState", RegistryValueKind.DWord, 2));

            default:
                return PolicyPlan.Reject($"'{setting}' isn't a setting C.A.R.S. changes.");
        }
    }

    private static PolicyWrite Set(string key, string name, RegistryValueKind kind, object value) => new(key, name, kind, value);
    private static PolicyWrite Delete(string key, string name) => new(key, name, RegistryValueKind.Unknown, null);

    /// <summary>Reads the current state from HKLM (no admin needed).</summary>
    public static WindowsUpdateState Read()
    {
        try
        {
            using var ux = Registry.LocalMachine.OpenSubKey(UxSettings);
            using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
            DateTime? until = DateTime.TryParse(ux?.GetValue("PauseUpdatesExpiryTime") as string, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) ? u : null;
            int? Int(RegistryKey? k, string n) => k?.GetValue(n) is int i ? i : null;
            return new WindowsUpdateState(until, Int(policy, "ExcludeWUDriversInQualityUpdate") == 1,
                Int(policy, "DeferFeatureUpdates") == 1 ? Int(policy, "DeferFeatureUpdatesPeriodInDays") : null,
                Int(ux, "ActiveHoursStart"), Int(ux, "ActiveHoursEnd"), ux?.GetValue(GamingMarker) is not null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new WindowsUpdateState(null, false, null, null, null, false);
        }
    }
}
