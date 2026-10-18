using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Tweaks;
using Microsoft.Win32;

namespace CarsControl.Tests;

public class TweakCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_every_switch_changes_something()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in TweakCatalog.All)
        {
            Assert.NotEmpty(t.Values);
            foreach (var v in t.Values)
            {
                Assert.True(v.Recommended is int or string, $"{t.Id}: only DWORD and string values");
                Assert.True(t.RestoresWindowsDefault || !Equals(v.Recommended, v.Default), $"{t.Id}/{v.Name}: recommended equals the Windows default, so the switch would do nothing");
                Assert.False(v.Key.StartsWith('\\') || v.Key.StartsWith("HK", StringComparison.OrdinalIgnoreCase), $"{t.Id}: keys are relative to the hive");
            }
        }
    }

    [Fact]
    public void Scopes_match_where_the_values_live()
    {
        foreach (var t in TweakCatalog.All)
        foreach (var v in t.Values)
        {
            var policy = v.Key.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase);
            switch (t.Scope)
            {
                case TweakScope.Machine:
                    Assert.True(v.Key.StartsWith("SOFTWARE\\", StringComparison.Ordinal) || t.IsOptimization && v.Key.StartsWith(@"SYSTEM\CurrentControlSet\Control\", StringComparison.Ordinal),
                        $"{t.Id}: machine values live under SOFTWARE (optimizations may also use the SYSTEM control set)");
                    break;
                case TweakScope.UserPolicy:
                    Assert.True(policy, $"{t.Id}: user policies live under Software\\Policies");
                    break;
                default:
                    Assert.False(policy, $"{t.Id}: you can't write your own policies without admin, so this must be UserPolicy");
                    break;
            }
        }
    }

    [Fact]
    public void Nothing_weakens_security()
    {
        string[] forbidden = ["Windows Defender", "SmartScreen", "WindowsFirewall", "EnableLUA", "ConsentPrompt", "WindowsUpdate", "Windows Error Reporting", "SpyNet"];
        foreach (var t in TweakCatalog.All)
        foreach (var v in t.Values)
            Assert.DoesNotContain(forbidden, f => v.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || v.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_limits_hide_switches_that_do_not_exist()
    {
        Assert.DoesNotContain(TweakCatalog.Available(19045), t => t.Id == "recall");
        Assert.Contains(TweakCatalog.Available(26300), t => t.Id == "recall");
        Assert.DoesNotContain(TweakCatalog.Available(26300), t => t.Id == "chat-button");
    }
}

public class TweakEngineTests : IDisposable
{
    private readonly string _path = $@"Software\CarsControlTest\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public TweakEngineTests() => _root = Registry.CurrentUser.CreateSubKey(_path, writable: true);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        TestKeys.RemoveParentIfEmpty();
    }

    private static Tweak Sample => new()
    {
        Id = "test", Group = TweakGroup.Ads, Scope = TweakScope.User, Title = "t", Description = "d",
        Values = [new(@"A\B", "Existing", 0, 1), new(@"A\C", "Missing", 0, 1), new(@"A\B", "Text", "off", "on")],
    };

    [Fact]
    public void Apply_then_undo_restores_exactly_what_was_there()
    {
        using (var b = _root.CreateSubKey(@"A\B")) b.SetValue("Existing", 7, RegistryValueKind.DWord);
        var t = Sample;
        Assert.Equal(TweakState.Off, TweakEngine.Read(t, _root));

        var saved = TweakEngine.Snapshot(t, _root);
        TweakEngine.Apply(t, _root);
        Assert.Equal(TweakState.On, TweakEngine.Read(t, _root));

        // Through JSON, like cars.db / the helper's file.
        TweakEngine.Restore(t, TweakEngine.Deserialize(TweakEngine.Serialize(saved))!, _root);
        using var after = _root.OpenSubKey(@"A\B")!;
        Assert.Equal(7, after.GetValue("Existing"));            // put back
        Assert.Null(after.GetValue("Text"));                     // didn't exist: deleted again
        using var c = _root.OpenSubKey(@"A\C");
        Assert.Null(c?.GetValue("Missing"));
        Assert.Equal(TweakState.Off, TweakEngine.Read(t, _root));
    }

    [Fact]
    public void Partly_applied_is_reported_and_defaults_reset()
    {
        var t = Sample;
        using (var b = _root.CreateSubKey(@"A\B")) b.SetValue("Existing", 0, RegistryValueKind.DWord);
        Assert.Equal(TweakState.Partly, TweakEngine.Read(t, _root));
        TweakEngine.SetDefaults(t, _root);
        using var after = _root.OpenSubKey(@"A\B")!;
        Assert.Equal(1, after.GetValue("Existing"));
        Assert.Equal("on", after.GetValue("Text"));
    }

    [Fact]
    public void Restore_never_writes_values_outside_the_tweak()
    {
        var t = Sample;
        TweakEngine.Restore(t, [new SavedValue(@"Evil", "Run", true, RegistryValueKind.String, "calc.exe")], _root);
        Assert.Null(_root.OpenSubKey("Evil"));
    }

    [Fact]
    public void Every_user_switch_round_trips()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.Scope == TweakScope.User))
        {
            var saved = TweakEngine.Snapshot(t, _root);
            TweakEngine.Apply(t, _root);
            Assert.Equal(TweakState.On, TweakEngine.Read(t, _root));
            TweakEngine.Restore(t, saved, _root);
            if (!t.RestoresWindowsDefault) Assert.NotEqual(TweakState.On, TweakEngine.Read(t, _root)); // Game Mode: absent = Windows' default, on
        }
    }
}

public class TweakHelperTests
{
    private static HelperOperations Ops(string? sid = "S-1-5-21-1-2-3-1001") => new(new HelperLog(), parentProfile: null, parentSid: sid);

    [Theory]
    [InlineData("no-such-switch", "apply")]
    [InlineData("advertising-id", "apply")]   // a your-account switch: the app does it, never the admin helper
    [InlineData("diagnostic-data", "delete")]
    [InlineData("../../evil", "apply")]
    public async Task Rejects_anything_off_the_list(string id, string action)
    {
        var r = await Ops().ExecuteAsync(new TweakOp(id, action), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task User_policies_need_to_know_whose_hive()
    {
        var r = await Ops(sid: null).ExecuteAsync(new TweakOp("copilot", "apply"), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
        var bad = await Ops(sid: "S-1-5-18").ExecuteAsync(new TweakOp("copilot", "apply"), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, bad.Status); // never SYSTEM's hive
    }

    [Fact]
    public async Task Dry_runs_describe_the_change()
    {
        var r = await Ops().ExecuteAsync(new TweakOp("diagnostic-data", "apply"), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains("AllowTelemetry = 1", r.Message);
    }
}

public class ConsentStoreTests : IDisposable
{
    private readonly string _path = $@"Software\CarsControlTest\{Guid.NewGuid():N}";
    private readonly RegistryKey _hive;

    public ConsentStoreTests() => _hive = Registry.CurrentUser.CreateSubKey(_path, writable: true);

    public void Dispose()
    {
        _hive.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        TestKeys.RemoveParentIfEmpty();
    }

    [Fact]
    public void Lists_recent_and_current_use()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
        var start = new DateTime(2026, 10, 13, 9, 0, 0).ToFileTime();
        using (var k = _hive.CreateSubKey($@"{store}\webcam\NonPackaged\C:#Program Files#Zoom#bin#Zoom.exe"))
        {
            k.SetValue("LastUsedTimeStart", start, RegistryValueKind.QWord);
            k.SetValue("LastUsedTimeStop", start + TimeSpan.FromMinutes(30).Ticks, RegistryValueKind.QWord);
        }
        using (var k = _hive.CreateSubKey($@"{store}\microphone\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe"))
        {
            k.SetValue("LastUsedTimeStart", start, RegistryValueKind.QWord);
            k.SetValue("LastUsedTimeStop", 0L, RegistryValueKind.QWord);
        }
        using (var k = _hive.CreateSubKey($@"{store}\location\NeverUsed_abc")) k.SetValue("Value", "Allow");

        var uses = ConsentStore.Read(_hive);
        Assert.Equal(2, uses.Count);
        Assert.True(uses[0].InUse);
        Assert.Equal("Microsoft.WindowsSoundRecorder", uses[0].App);
        Assert.Equal("Microphone", uses[0].Capability);
        Assert.Equal("Zoom", uses[1].App);
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe", uses[1].ExePath);
        Assert.False(uses[1].InUse);
    }
}

internal static class TestKeys
{
    public static void RemoveParentIfEmpty()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\CarsControlTest");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            try { Registry.CurrentUser.DeleteSubKey(@"Software\CarsControlTest", throwOnMissingSubKey: false); } catch (InvalidOperationException) { /* another test is using it */ }
    }
}

public class HistoryCsvTests
{
    [Fact]
    public void Escapes_commas_quotes_line_breaks_and_formulas()
    {
        var e = new CarsControl.Core.Data.HistoryEntry(1, new DateTime(2026, 10, 13, 9, 5, 0), null, "update", "k", "App, \"quoted\"", "Winget",
            "1.0", "2.0", true, "Succeeded", 0, "=HYPERLINK(\"http://x\")\nsecond line", null);
        var csv = CarsControl.Core.Data.HistoryCsv.Build([e]);
        var lines = csv.Split("\r\n");
        Assert.Equal("When,Action,Name,Version,Result,Admin,Via,Details", lines[0]);
        Assert.StartsWith("2026-10-13 09:05,update,\"App, \"\"quoted\"\"\",1.0 -> 2.0,Succeeded,yes,Winget,\"'=HYPERLINK(", lines[1]);
        Assert.Contains("second line\"", csv); // the line break stayed inside the quoted value
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("+1", "'+1")]
    [InlineData("@x", "'@x")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("", "")]
    public void Escape_cases(string input, string expected) => Assert.Equal(expected, CarsControl.Core.Data.HistoryCsv.Escape(input));
}
