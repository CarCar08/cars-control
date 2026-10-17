using System.Text.Json;
using CarsControl.Core.Help;
using CarsControl.Core.Settings;

namespace CarsControl.Tests;

public class FirstRunSetupTests
{
    [Fact]
    public void Recommendations_are_staged_and_respect_the_chosen_profile()
    {
        var settings = new CarsSettings();
        var before = JsonSerializer.Serialize(settings);
        var current = FirstRunSetup.Read(settings, false) with { PcType = PcType.Laptop, RestartAway = true };
        var recommended = FirstRunSetup.Recommend(current, hasBattery: false);
        Assert.False(recommended.RestartAway);
        Assert.True(recommended.StartWithWindows);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.NotEmpty(FirstRunSetup.Preview(current, recommended));
    }

    [Fact]
    public void Undo_restores_every_offered_value_and_reason_without_replacing_unrelated_settings()
    {
        var settings = new CarsSettings();
        settings.Profile.HoldDriverUpdatesReason = "Existing reason";
        settings.Profile.KeepCrashEvidenceReason = "Existing crash reason";
        settings.Sync.Folder = @"D:\Private sync";
        var undo = FirstRunSetup.Capture(settings, true, "\"C:\\Original\\CarsControl.exe\" --background --page home");
        Assert.Equal(undo.StartupCommand, JsonSerializer.Deserialize<SetupUndo>(JsonSerializer.Serialize(undo))!.StartupCommand);
        var choices = undo.Choices with { PcType = PcType.GamingFirst, HoldDrivers = true, KeepCrash = true,
            StartWithWindows = false, MaintenanceOn = true, GamingAuto = true, GamingApprove = true, RestartAway = true, BoardSensors = false };
        FirstRunSetup.Apply(settings, choices);
        Assert.Equal(choices with { StartWithWindows = true }, FirstRunSetup.Read(settings, true));
        settings.CheckIntervalHours = 12;
        FirstRunSetup.Restore(settings, undo);
        Assert.Equal(undo.Choices, FirstRunSetup.Read(settings, true));
        Assert.Equal("Existing reason", settings.Profile.HoldDriverUpdatesReason);
        Assert.Equal("Existing crash reason", settings.Profile.KeepCrashEvidenceReason);
        Assert.Equal(@"D:\Private sync", settings.Sync.Folder);
        Assert.Equal(12, settings.CheckIntervalHours);
    }

    [Fact]
    public void Unknown_profiles_fail_before_changing_settings()
    {
        var s = new CarsSettings();
        var before = JsonSerializer.Serialize(s);
        Assert.Throws<ArgumentException>(() => FirstRunSetup.Apply(s, FirstRunSetup.Read(s, false) with { PcType = (PcType)999 }));
        Assert.Equal(before, JsonSerializer.Serialize(s));
        Assert.Empty(FirstRunSetup.Preview(FirstRunSetup.Read(s, false), FirstRunSetup.Read(s, false)));
    }
}
