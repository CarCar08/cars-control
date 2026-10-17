using CarsControl.Core.Cleanup;
using CarsControl.Core.Settings;

namespace CarsControl.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cars-settings-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        ActiveProfile.Current = new ProfileSettings();
    }

    [Fact]
    public void A_fresh_install_gets_generic_defaults()
    {
        var s = new SettingsStore(File, _ => null).Current;
        Assert.Null(s.ProtectedApps); // = Defaults.ProtectedApps
        Assert.DoesNotContain("Tailscale", Defaults.ProtectedApps);
        Assert.False(s.Profile.HoldDriverUpdates);
        Assert.False(s.Profile.KeepCrashEvidence);
        Assert.Equal(ThemeChoice.Dark, s.Appearance.Theme);
        Assert.Equal(AccentChoice.Cars, s.Appearance.Accent);
        Assert.True(System.IO.File.Exists(File));
    }

    [Fact]
    public void An_upgraded_install_keeps_this_pcs_behavior()
    {
        var old = new Dictionary<string, string> { ["run_in_background"] = "1", ["check_interval_hours"] = "8", ["fast_scan"] = "0" };
        var s = new SettingsStore(File, k => old.GetValueOrDefault(k)).Current;
        Assert.Equal(8, s.CheckIntervalHours);
        Assert.False(s.FastScan);
        Assert.Contains("Tailscale", s.ProtectedApps!);       // round 3's list, since none was saved
        Assert.True(s.Profile.HoldDriverUpdates);
        Assert.True(s.Profile.KeepCrashEvidence);
    }

    [Fact]
    public void Saved_choices_round_trip()
    {
        var store = new SettingsStore(File, _ => null);
        store.Update(s =>
        {
            s.Appearance.Backdrop = BackdropChoice.Solid;
            s.Appearance.CustomAccent = "#123ABC";
            s.Ui["storage.files.sort"] = "Bytes:desc";
        });
        var again = new SettingsStore(File).Current;
        Assert.Equal(BackdropChoice.Solid, again.Appearance.Backdrop);
        Assert.Equal("#123ABC", again.Appearance.CustomAccent);
        Assert.Equal("Bytes:desc", again.Ui["storage.files.sort"]);
        Assert.Contains("\"Backdrop\": \"Solid\"", System.IO.File.ReadAllText(File)); // readable, hand-editable JSON
    }

    [Fact]
    public void A_broken_hand_edit_is_kept_aside_not_lost()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var s = new SettingsStore(File).Current;
        Assert.True(s.RunInBackground);
        Assert.True(System.IO.File.Exists(File + ".broken"));
    }

    [Fact]
    public void Crash_evidence_rules_follow_the_profile()
    {
        var reports = CleanupRules.Find("error-reports")!;
        ActiveProfile.Current = new ProfileSettings { KeepCrashEvidence = true, KeepCrashEvidenceReason = "BSOD investigation" };
        Assert.False(reports.DefaultOn);
        Assert.Contains("BSOD investigation", reports.Warning);
        ActiveProfile.Current = new ProfileSettings();
        Assert.True(reports.DefaultOn);
        Assert.Null(reports.Warning);
    }
}
