using CarsControl.Core.Inventory;
using CarsControl.Core.Models;
using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public class ArpParseTests
{
    private static Dictionary<string, object?> V(params (string k, object? v)[] values) =>
        values.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Hides_system_components_patches_and_nameless_entries()
    {
        Assert.Null(ArpProvider.Parse(ArpHive.LocalMachine64, "x", V(("DisplayName", "VC Runtime"), ("SystemComponent", 1))));
        Assert.Null(ArpProvider.Parse(ArpHive.LocalMachine64, "x", V(("DisplayName", "Patch"), ("ParentKeyName", "Office"))));
        Assert.Null(ArpProvider.Parse(ArpHive.LocalMachine64, "x", V(("DisplayName", "KB1"), ("ReleaseType", "Security Update"))));
        Assert.Null(ArpProvider.Parse(ArpHive.LocalMachine64, "x", V(("UninstallString", "u.exe"))));
    }

    [Fact]
    public void Msi_entry_gets_product_code_and_machine_scope()
    {
        var a = ArpProvider.Parse(ArpHive.LocalMachine64, "{7D9C1C7E-1C6F-4A9C-9B33-0F1E2D3C4B5A}",
            V(("DisplayName", "Tool"), ("WindowsInstaller", 1), ("UninstallString", "MsiExec.exe /X{7D9C1C7E-1C6F-4A9C-9B33-0F1E2D3C4B5A}"), ("EstimatedSize", 2048)))!;
        Assert.Equal("{7D9C1C7E-1C6F-4A9C-9B33-0F1E2D3C4B5A}", a.MsiProductCode);
        Assert.Equal(InstallScope.Machine, a.Scope);
        Assert.Equal(2048L * 1024, a.SizeBytes);
    }

    [Fact]
    public void Hkcu_entries_are_user_scope()
    {
        var a = ArpProvider.Parse(ArpHive.CurrentUser, "Discord", V(("DisplayName", "Discord"), ("UninstallString", "Update.exe --uninstall")))!;
        Assert.Equal(InstallScope.User, a.Scope);
        Assert.Equal("arp:CurrentUser:Discord", a.Key);
    }

    [Theory]
    [InlineData("\"C:\\App\\app.exe\",0", "C:\\App\\app.exe")]
    [InlineData("C:\\App\\app.exe,3", "C:\\App\\app.exe")]
    [InlineData("C:\\App\\icon.ico", "C:\\App\\icon.ico")]
    [InlineData("  ", null)]
    public void Cleans_display_icon(string raw, string? expected) => Assert.Equal(expected, ArpProvider.CleanIconPath(raw));

    [Theory]
    [InlineData("20260921", 2026, 9, 21)]
    public void Parses_install_date(string s, int y, int m, int d) => Assert.Equal(new DateTime(y, m, d), ArpProvider.ParseInstallDate(s));

    [Fact]
    public void Drive_root_is_not_an_install_location() => Assert.Null(ArpProvider.NormalizeDir("C:\\"));
}

public class ClassifierTests
{
    private static AppEntry App(string name, string? wingetId = null, string? location = null, string key = "k") => new()
    {
        Key = key, Name = name, WingetId = wingetId, InstallLocation = location, Source = AppSource.Arp,
        Arp = new ArpLocation(ArpHive.LocalMachine64, key),
    };

    [Fact]
    public void Epic_is_a_launcher_even_though_winget_knows_it() =>
        Assert.Equal(UpdateChannel.Launcher, ChannelClassifier.Classify(App("Epic Games Launcher", "EpicGames.EpicGamesLauncher")));

    [Fact]
    public void Wsl_and_gameinput_avoid_winget() // both failed repeatedly through winget on this PC
    {
        Assert.Equal(UpdateChannel.Wsl, ChannelClassifier.Classify(App("Windows Subsystem for Linux", "Microsoft.WSL")));
        Assert.Equal(UpdateChannel.System, ChannelClassifier.Classify(App("Microsoft GameInput", "Microsoft.GameInput")));
    }

    [Fact]
    public void Steam_games_are_games() =>
        Assert.Equal(UpdateChannel.Game, ChannelClassifier.Classify(App("BeamNG.drive", location: @"D:\SteamLibrary\steamapps\common\BeamNG.drive", key: "Steam App 284160")));

    [Fact]
    public void Correlated_apps_use_winget() =>
        Assert.Equal(UpdateChannel.Winget, ChannelClassifier.Classify(App("Google Chrome", "Google.Chrome")));

    [Fact]
    public void Protected_list_matches_by_prefix()
    {
        var p = new ProtectedApps();
        Assert.True(p.IsProtected(App("Microsoft Visual C++ 2015-2022 Redistributable (x64)")));
        Assert.False(p.IsProtected(App("Tailscale")));     // personal choices aren't in the generic defaults…
        Assert.False(p.IsProtected(App("Spotify")));
        Assert.True(new ProtectedApps(["Tailscale"]).IsProtected(App("Tailscale"))); // …they come from your settings
    }
}

public class CorrelatorTests
{
    [Fact]
    public void Matches_by_product_code_and_prefers_hkcu_for_user_scope()
    {
        var machine = new AppEntry { Key = "m", Name = "LocalSend", Version = "1.17.0", Source = AppSource.Arp, Arp = new ArpLocation(ArpHive.LocalMachine64, "{X}_is1"), Scope = InstallScope.Machine };
        var user = new AppEntry { Key = "u", Name = "LocalSend", Version = "1.17.0", Source = AppSource.Arp, Arp = new ArpLocation(ArpHive.CurrentUser, "{X}_is1"), Scope = InstallScope.User };
        var w = new WingetInstalled("LocalSend.LocalSend", "LocalSend", "1.17.0", "1.18.2", true, "winget", InstallScope.User, null, ["{X}_is1"], []);

        Correlator.Apply([machine, user], [w]);

        Assert.Equal("LocalSend.LocalSend", user.WingetId);
        Assert.Equal("1.18.2", user.AvailableVersion);
        Assert.Null(machine.WingetId);
    }

    [Fact]
    public void Uncorrelated_winget_entries_do_not_set_ids()
    {
        var a = new AppEntry { Key = "a", Name = "Game", Source = AppSource.Arp, Arp = new ArpLocation(ArpHive.LocalMachine64, "Steam App 1") };
        var w = new WingetInstalled(@"ARP\Machine\X64\Steam App 1", "Game", "1", null, false, null, InstallScope.Unknown, @"D:\g", ["Steam App 1"], []);
        Correlator.Apply([a], [w]);
        Assert.Null(a.WingetId);
        Assert.Equal(@"D:\g", a.InstallLocation);
    }
}
