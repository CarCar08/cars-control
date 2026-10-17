using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Gaming;

namespace CarsControl.Tests;

public class GameDetectorTests
{
    private static readonly GameFolder[] Folders =
    [
        new("Schedule I", @"D:\SteamLibrary\steamapps\common\Schedule I"),
        new("Grand Theft Auto V Enhanced", @"C:\Program Files\Rockstar Games\Grand Theft Auto V Enhanced"),
        new("Fortnite", @"C:\Program Files\Epic Games\Fortnite"),
        new("Fortnite Tools", @"C:\Program Files\Epic Games\Fortnite\Tools"),
    ];

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Schedule I\Schedule I.exe", "Schedule I")]
    [InlineData(@"d:\steamlibrary\STEAMAPPS\common\schedule i\Schedule I.exe", "Schedule I")]
    [InlineData(@"C:\Program Files\Rockstar Games\Grand Theft Auto V Enhanced\GTA5_Enhanced.exe", "Grand Theft Auto V Enhanced")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", "Fortnite")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\Tools\Editor.exe", "Fortnite Tools")] // deepest folder wins
    public void Programs_inside_a_game_folder_are_that_game(string exe, string game) =>
        Assert.Equal(game, GameDetector.Match(exe, Folders));

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Schedule I\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData(@"C:\Program Files\Rockstar Games\Grand Theft Auto V Enhanced\PlayGTAV_Launcher.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Schedule I\_CommonRedist\vcredist\VC_redist.x64.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Schedule I\unins000.exe")]
    public void Launchers_crash_reporters_and_installers_never_count(string exe) =>
        Assert.Null(GameDetector.Match(exe, Folders));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Schedule II\Game.exe")] // a sibling with a longer name isn't inside
    [InlineData(@"C:\Windows\explorer.exe")]
    public void Other_programs_are_not_games(string exe) =>
        Assert.Null(GameDetector.Match(exe, Folders));

    [Fact]
    public void Too_broad_folders_are_never_used()
    {
        GameFolder[] bad = [new("Broken", @"D:\"), new("Library", @"D:\SteamLibrary\steamapps\common"), new("PF", @"C:\Program Files")];
        Assert.Null(GameDetector.Match(@"D:\SteamLibrary\steamapps\common\Anything\x.exe", bad));
        Assert.Null(GameDetector.Match(@"C:\Program Files\Notepad++\notepad++.exe", bad));
    }

    [Fact]
    public void Your_lists_win()
    {
        Assert.Equal("eldenring", GameDetector.Match(@"E:\Games\ELDEN RING\Game\eldenring.exe", Folders, extraGames: ["EldenRing.exe"]));
        Assert.Null(GameDetector.Match(@"D:\SteamLibrary\steamapps\common\Schedule I\Schedule I.exe", Folders, ignored: ["schedule i.exe"]));
    }
}

public class GamingOpsTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WinDefend")]
    [InlineData("SaladBowl")]
    [InlineData("WSearch & calc")]
    public async Task Only_windows_search_can_be_stopped(string service)
    {
        var r = await Ops().ExecuteAsync(new ServiceControlOp(service, Start: false), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task Gaming_ops_in_dev_mode_are_dry_runs_only()
    {
        var ops = Ops(dev: true);
        var r = await ops.ExecuteAsync(new PurgeStandbyListOp(), dryRun: false, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
        var dry = await ops.ExecuteAsync(new PurgeStandbyListOp(), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.DryRun, dry.Status);
    }

    [Fact]
    public void Pause_times_use_windows_format_and_a_running_pause_is_detected()
    {
        var now = new DateTime(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-13T20:00:00Z", GamingOps.PauseTime(now));
        Assert.True(GamingOps.IsPaused("2026-10-14T08:00:00Z", now));
        Assert.False(GamingOps.IsPaused("2026-10-13T08:00:00Z", now));
        Assert.False(GamingOps.IsPaused(null, now));
        Assert.False(GamingOps.IsPaused("garbage", now));
    }

    [Fact]
    public void Ops_round_trip_through_the_pipe_format()
    {
        HelperOp[] ops = [new WindowsUpdatePauseOp(true), new PurgeStandbyListOp(), new ServiceControlOp("WSearch", false)];
        foreach (var op in ops)
        {
            var json = HelperJson.Serialize(new HelperRequest(1, op));
            Assert.Equal(op, HelperJson.Deserialize<HelperRequest>(json)!.Op);
        }
    }
}

public class NotAGameTests
{
    [Fact]
    public void Always_on_steam_tools_are_not_games()
    {
        GameFolder[] folders = [new("Wallpaper Engine", @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine")];
        Assert.Null(GameDetector.Match(@"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", folders));
    }
}
