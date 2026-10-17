using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Storage;

namespace CarsControl.Tests;

public class EpicMoverTests
{
    private sealed class Observe(Action<MoveProgress> action) : IProgress<MoveProgress>
    {
        public void Report(MoveProgress value) => action(value);
    }

    private static string Item(string folder) => new JsonObject
    {
        ["InstallLocation"] = folder, ["ManifestLocation"] = folder + @"\.egstore",
        ["StagingLocation"] = folder + @"\.egstore/bps", ["DisplayName"] = "Game One", ["AppName"] = "app-id"
    }.ToJsonString();

    private static string Installed(string folder) => new JsonObject
    {
        ["InstallationList"] = new JsonArray(new JsonObject { ["InstallLocation"] = folder, ["AppName"] = "app-id" },
            new JsonObject { ["InstallLocation"] = @"D:\Epic Games\Other", ["AppName"] = "other" }), ["OtherField"] = 42
    }.ToJsonString();

    private static (EpicMovePlan Plan, string Installed) Setup(TempTree t)
    {
        var from = t.Dir(@"original\Game One");
        t.File(@"original\Game One\bin\game.exe", "executable");
        t.File(@"original\Game One\data.pak", "game data");
        var to = Path.Combine(t.Root, @"destination\Game One");
        var item = t.File(@"manifests\game-id.item", Item(from));
        var installed = t.File("LauncherInstalled.dat", Installed(from));
        var game = new GameEntry("Game One", "Epic", Path.GetDirectoryName(from)!, from, 19, null, null, null);
        return (new EpicMovePlan(game, item, from, to, 19, null), installed);
    }

    [Fact]
    public void Rewriting_the_item_changes_only_paths_inside_the_game()
    {
        var json = JsonNode.Parse(Item(@"C:\Epic Games\Game One"))!;
        json["StagingLocation"] = @"C:\Epic Games\Game One Extra\stage";
        var result = JsonNode.Parse(EpicMover.RewriteItem(json.ToJsonString(), @"c:\epic games\game one\", @"D:\Epic Games\Game One"))!;
        Assert.Equal(@"D:\Epic Games\Game One", result["InstallLocation"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Game One\.egstore", result["ManifestLocation"]!.GetValue<string>());
        Assert.Equal(@"C:\Epic Games\Game One Extra\stage", result["StagingLocation"]!.GetValue<string>());
        Assert.Equal("app-id", result["AppName"]!.GetValue<string>());
        Assert.Equal("Game One", result["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public void Launcher_separators_and_trailing_slashes_are_supported()
    {
        var item = JsonNode.Parse(Item("C:/Epic Games/Game One"))!;
        item["InstallLocation"] = "C:/Epic Games/Game One/";
        var result = JsonNode.Parse(EpicMover.RewriteItem(item.ToJsonString(), @"C:\Epic Games\Game One", @"D:\Epic Games\Game One"))!;
        Assert.Equal(@"D:\Epic Games\Game One\.egstore/bps", result["StagingLocation"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Game One", EpicMover.TargetFor(@"D:\", "C:/Epic Games/Game One/"));
    }

    [Fact]
    public void Rewriting_the_installation_list_preserves_other_games_and_metadata()
    {
        var result = JsonNode.Parse(EpicMover.RewriteLauncherInstalled(Installed(@"C:\Epic Games\Game One"), @"c:\epic games\GAME ONE\", @"E:\Epic Games\Game One"))!;
        Assert.Equal(@"E:\Epic Games\Game One", result["InstallationList"]![0]!["InstallLocation"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Other", result["InstallationList"]![1]!["InstallLocation"]!.GetValue<string>());
        Assert.Equal(42, result["OtherField"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"InstallationList\":[]}")]
    [InlineData("broken")]
    public void Missing_or_malformed_installation_records_stop_the_move(string json) =>
        Assert.ThrowsAny<JsonException>(() => EpicMover.RewriteLauncherInstalled(json, @"C:\Epic Games\Game", @"D:\Epic Games\Game"));

    [Fact]
    public void A_stale_item_is_rejected() => Assert.Throws<JsonException>(() =>
        EpicMover.RewriteItem(Item(@"C:\Epic Games\Other"), @"C:\Epic Games\Game", @"D:\Epic Games\Game"));

    [Fact]
    public void Duplicate_installation_records_are_rejected()
    {
        var json = JsonNode.Parse(Installed(@"C:\Epic Games\Game"))!;
        ((JsonArray)json["InstallationList"]!).Add(new JsonObject { ["InstallLocation"] = @"C:\Epic Games\Game" });
        Assert.Throws<JsonException>(() => EpicMover.RewriteLauncherInstalled(json.ToJsonString(), @"C:\Epic Games\Game", @"D:\Epic Games\Game"));
    }

    [Fact]
    public void Planning_explains_wrong_launchers_missing_records_busy_games_and_protected_apps()
    {
        using var t = new TempTree();
        var (plan, _) = Setup(t);
        var otherDrive = Path.GetPathRoot(plan.From)![0] == 'Z' ? @"Y:\" : @"Z:\";
        var manifests = Path.GetDirectoryName(plan.ItemFile)!;
        Assert.Contains("Only Epic", EpicMover.Plan(plan.Game with { Source = "Steam" }, otherDrive, () => false, _ => false, manifests).Blocker);
        Assert.Contains("protected", EpicMover.Plan(plan.Game with { Name = "Salad" }, otherDrive, () => false, _ => false, manifests).Blocker);
        Assert.Contains("already", EpicMover.Plan(plan.Game, Path.GetPathRoot(plan.From)!, () => false, _ => false, manifests).Blocker);
        Assert.Contains("Close the Epic", EpicMover.Plan(plan.Game, otherDrive, () => true, _ => false, manifests).Blocker);
        Assert.Contains("game is running", EpicMover.Plan(plan.Game, otherDrive, () => false, _ => true, manifests).Blocker);
        File.Delete(plan.ItemFile);
        Assert.Contains("wasn't found", EpicMover.Plan(plan.Game, otherDrive, () => false, _ => false, manifests).Blocker);
    }

    [Fact]
    public void FindItem_ignores_corrupt_files_and_matches_normalized_paths()
    {
        using var t = new TempTree();
        t.File(@"manifests\broken.item", "broken");
        var item = t.File(@"manifests\game.item", Item(@"C:\Epic Games\Game"));
        Assert.Equal(item, EpicMover.FindItemFile("c:/epic games/game/", t.Dir("manifests")));
        Assert.Null(EpicMover.FindItemFile(@"C:\Epic Games\Missing", t.Dir("manifests")));
    }

    private static string Egi(string folder) => new JsonObject
    {
        ["v4"] = new JsonObject
        {
            ["installationId"] = "game-id", ["dir"] = folder, ["metaDir"] = folder + @"\.egstore",
            ["manifestPath"] = folder + @"\.egstore\game-id.manifest", ["pendingManifestPath"] = folder + @"\.egstore\Pending\game-id.manifest",
            ["revision"] = "08DEDAD4D791DABC000000000000001F", ["manifestData"] = new JsonObject { ["launchExe"] = "game.exe" },
        }
    }.ToJsonString();

    [Fact]
    public void Ambiguous_records_block_the_move_before_anything_is_copied()
    {
        using var t = new TempTree();
        var (plan, _) = Setup(t);
        t.File(@"manifests\second.item", Item(plan.From));
        Assert.Null(EpicMover.FindItemFile(plan.From, Path.GetDirectoryName(plan.ItemFile)));
    }

    [Fact]
    public void Rewriting_the_install_helper_record_changes_its_paths_and_keeps_everything_else()
    {
        var result = JsonNode.Parse(EpicMover.RewriteEosh(Egi(@"C:\Program Files\Epic Games\Game"), @"c:\program files\epic games\game\", @"D:\Epic Games\Game"))!["v4"]!;
        Assert.Equal(@"D:\Epic Games\Game", result["dir"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Game\.egstore", result["metaDir"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Game\.egstore\game-id.manifest", result["manifestPath"]!.GetValue<string>());
        Assert.Equal(@"D:\Epic Games\Game\.egstore\Pending\game-id.manifest", result["pendingManifestPath"]!.GetValue<string>());
        Assert.Equal("08DEDAD4D791DABC000000000000001F", result["revision"]!.GetValue<string>());
        Assert.Equal("game.exe", result["manifestData"]!["launchExe"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"v5\":{\"dir\":\"C:\\\\Epic Games\\\\Game\"}}")]
    [InlineData("{\"v4\":{\"dir\":\"C:\\\\Epic Games\\\\Other\"}}")]
    public void Unknown_or_stale_install_helper_records_are_refused(string json) =>
        Assert.ThrowsAny<JsonException>(() => EpicMover.RewriteEosh(json, @"C:\Epic Games\Game", @"D:\Epic Games\Game"));

    [Fact]
    public void An_unknown_install_helper_format_blocks_the_plan()
    {
        using var t = new TempTree();
        var (plan, _) = Setup(t);
        t.File(@"eosh\game-id.egi", "{\"v9\":{}}");
        var other = Path.GetPathRoot(plan.From)![0] == 'Z' ? @"Y:\" : @"Z:\";
        Assert.Contains("install helper", EpicMover.Plan(plan.Game, other, () => false, _ => false, Path.GetDirectoryName(plan.ItemFile), t.Dir("eosh")).Blocker);
    }

    [Fact]
    public async Task A_move_switches_all_three_records_and_keeps_the_original_until_it_is_removed()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var egi = t.File(@"eosh\game-id.egi", Egi(plan.From));
        plan = plan with { EoshFile = egi };
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(plan.From, "data.pak")));
        Assert.Equal(plan.To, JsonNode.Parse(File.ReadAllText(egi))!["v4"]!["dir"]!.GetValue<string>());
        Assert.Equal(Egi(plan.From), File.ReadAllText(egi + ".cars-before-move"));
        Assert.Equal(plan.To, File.ReadAllText(plan.ItemFile + ".cars-moved-to"));

        var state = EpicMover.OldCopyOf(plan.ItemFile, t.Dir("eosh"));
        Assert.Equal((plan.From, plan.To, false), state);
        Assert.Equal("There's no old copy of this game to remove.", await EpicMover.RemoveOldCopyAsync(plan.ItemFile + ".missing", _ => Task.FromResult<string?>(null), CancellationToken.None, () => false, t.Dir("eosh")));
        Assert.Contains("Close the Epic", await EpicMover.RemoveOldCopyAsync(plan.ItemFile, _ => Task.FromResult<string?>(null), CancellationToken.None, () => true, t.Dir("eosh")));
        Assert.True(Directory.Exists(plan.From));
        Assert.Null(await EpicMover.RemoveOldCopyAsync(plan.ItemFile, _ => Task.FromResult<string?>("helper must not be needed"), CancellationToken.None, () => false, t.Dir("eosh")));
        Assert.False(Directory.Exists(plan.From));
        Assert.True(File.Exists(Path.Combine(plan.To, "data.pak")));
        Assert.Null(EpicMover.OldCopyOf(plan.ItemFile, t.Dir("eosh")));
    }

    [Fact]
    public async Task An_earlier_copy_the_game_has_run_from_is_reused_and_its_game_made_files_removed()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var original = Path.GetRelativePath(t.Root, plan.From);
        var copy = Path.GetRelativePath(t.Root, plan.To);
        for (var i = 0; i < 8; i++) t.File(Path.Combine(original, $"content\\pack{i}.pak"), $"pack {i}");
        plan = plan with { Bytes = TreeOps.Measure(plan.From).Bytes };
        // What rc.1 left on D:: every game file (10), plus files the game wrote there.
        foreach (var file in Directory.GetFiles(plan.From, "*", SearchOption.AllDirectories))
            t.File(Path.Combine(copy, Path.GetRelativePath(plan.From, file)), File.ReadAllText(file));
        for (var i = 0; i < 2; i++) t.File(Path.Combine(copy, $"bin\\extra{i}.dat"), "x");
        Assert.Null(TreeOps.ExtrasInEarlierCopy(plan.To, plan.From)); // 10 shared, 2 extra: under 90%, not clearly the same game
        for (var i = 0; i < 2; i++) File.Delete(Path.Combine(plan.To, $"bin\\extra{i}.dat"));
        t.File(Path.Combine(copy, @".sentry-native\run\event"), "crash report");
        Assert.Equal([@".sentry-native\run\event"], TreeOps.ExtrasInEarlierCopy(plan.To, plan.From));

        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(Path.Combine(plan.To, ".sentry-native")));
        Assert.Equal("game data", File.ReadAllText(Path.Combine(plan.To, "data.pak")));
    }

    [Fact]
    public async Task The_old_copy_can_be_removed_after_the_game_writes_its_own_files_into_the_new_one()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Assert.True((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        t.File(Path.Combine(Path.GetRelativePath(t.Root, plan.To), @".sentry-native\settings.dat"), "written by the game");
        Assert.Null(await EpicMover.RemoveOldCopyAsync(plan.ItemFile, _ => Task.FromResult<string?>(null), CancellationToken.None, () => false, t.Dir("eosh")));
        Assert.False(Directory.Exists(plan.From));
        Assert.True(File.Exists(Path.Combine(plan.To, @".sentry-native\settings.dat")));
    }

    [Fact]
    public void A_conflicting_file_means_it_is_not_an_earlier_copy()
    {
        using var t = new TempTree();
        t.File(@"a\game.exe", "original");
        t.File(@"b\game.exe", "different length");
        Assert.Null(TreeOps.ExtrasInEarlierCopy(t.Dir("b"), t.Dir("a")));
    }

    [Fact]
    public async Task The_2026_launcher_leaves_the_legacy_list_empty_and_the_move_still_switches_its_real_records()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        const string empty = "{\n\t\"InstallationList\": []\n}";
        File.WriteAllText(installed, empty);
        var egi = t.File(@"eosh\game-id.egi", Egi(plan.From));
        var result = await EpicMover.RunAsync(plan with { EoshFile = egi }, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(empty, File.ReadAllText(installed));
        Assert.Equal(plan.To, JsonNode.Parse(File.ReadAllText(egi))!["v4"]!["dir"]!.GetValue<string>());
        Assert.Equal(plan.To, JsonNode.Parse(File.ReadAllText(plan.ItemFile))!["InstallLocation"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_install_helper_records_an_empty_legacy_list_still_stops_the_move()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        File.WriteAllText(installed, "{\"InstallationList\":[]}");
        Assert.False((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
    }

    [Fact]
    public async Task The_old_copy_stays_if_the_copies_no_longer_match()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Assert.True((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        File.WriteAllText(Path.Combine(plan.To, "data.pak"), "changed!!");
        Assert.Contains("differ", await EpicMover.RemoveOldCopyAsync(plan.ItemFile, _ => Task.FromResult<string?>(null), CancellationToken.None, () => false, t.Dir("eosh")));
        Assert.True(File.Exists(Path.Combine(plan.From, "data.pak")));
    }

    [Fact]
    public async Task When_Epic_undoes_the_move_the_state_says_so_and_the_copy_it_uses_is_never_removed()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var egi = t.File(@"eosh\game-id.egi", Egi(plan.From));
        plan = plan with { EoshFile = egi };
        Assert.True((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        // What the 2026 launcher did on its next start: its install helper still had the old folder and rebuilt the item from it.
        File.WriteAllText(egi, Egi(plan.From));
        File.WriteAllText(plan.ItemFile, Item(plan.From));
        Assert.Equal((plan.From, plan.To, true), EpicMover.OldCopyOf(plan.ItemFile, t.Dir("eosh")));
        // The unused copy here isn't in an "<drive>:\Epic Games" folder, so it isn't recognised as C.A.R.S.' own copy: both stay.
        Assert.Contains("isn't the copy", await EpicMover.RemoveOldCopyAsync(plan.ItemFile, _ => Task.FromResult<string?>(null), CancellationToken.None, () => false, t.Dir("eosh")));
        Assert.True(Directory.Exists(plan.From));
        Assert.True(Directory.Exists(plan.To));
    }

    [Fact]
    public async Task A_move_switches_both_records_preserves_backups_and_keeps_the_original()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var originalItem = File.ReadAllText(plan.ItemFile);
        var originalList = File.ReadAllText(installed);
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(plan.From));
        Assert.Equal("game data", File.ReadAllText(Path.Combine(plan.To, "data.pak")));
        Assert.Equal(plan.To, JsonNode.Parse(File.ReadAllText(plan.ItemFile))!["InstallLocation"]!.GetValue<string>());
        Assert.Equal(plan.To, JsonNode.Parse(File.ReadAllText(installed))!["InstallationList"]![0]!["InstallLocation"]!.GetValue<string>());
        Assert.Equal(originalItem, File.ReadAllText(plan.ItemFile + ".cars-before-move"));
        Assert.Equal(originalList, File.ReadAllText(installed + ".cars-before-move"));
        Assert.Empty(Directory.EnumerateFiles(t.Root, "*.cars-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_before_copy_is_a_result_and_leaves_both_records_untouched()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await EpicMover.RunAsync(plan, null, cts.Token, installed, () => false);
        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.True(Directory.Exists(plan.From));
        Assert.False(Directory.Exists(plan.To));
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
    }

    [Fact]
    public async Task A_partial_copy_is_reused()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Directory.CreateDirectory(plan.To);
        File.Copy(Path.Combine(plan.From, "data.pak"), Path.Combine(plan.To, "data.pak"));
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(plan.To, @"bin\game.exe")));
    }

    [Fact]
    public async Task A_corrupt_partial_file_with_the_same_size_and_timestamp_is_repaired_on_retry()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Directory.CreateDirectory(plan.To);
        var copy = Path.Combine(plan.To, "data.pak");
        File.WriteAllText(copy, "bad! data");
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(Path.Combine(plan.From, "data.pak")));
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("game data", File.ReadAllText(copy));
    }

    [Fact]
    public void Resuming_requires_only_the_uncopied_bytes_plus_the_safety_margin()
    {
        using var t = new TempTree();
        var (plan, _) = Setup(t);
        var before = EpicMover.RequiredFreeBytes(plan.From, plan.To, plan.Bytes);
        Directory.CreateDirectory(plan.To);
        File.Copy(Path.Combine(plan.From, "data.pak"), Path.Combine(plan.To, "data.pak"));
        Assert.Equal(before - 9, EpicMover.RequiredFreeBytes(plan.From, plan.To, plan.Bytes));
    }

    [Fact]
    public async Task Cancelling_before_the_switch_leaves_every_record_on_the_original()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        using var cts = new CancellationTokenSource();
        var progress = new Observe(p => { if (p.Stage == "Telling the launcher where the game is now") cts.Cancel(); });
        var result = await EpicMover.RunAsync(plan, progress, cts.Token, installed, () => false);
        Assert.False(result.Ok);
        Assert.True(result.Cancelled);
        Assert.True(Directory.Exists(plan.From));
        Assert.Equal(plan.From, JsonNode.Parse(File.ReadAllText(plan.ItemFile))!["InstallLocation"]!.GetValue<string>());
        Assert.Equal(plan.From, JsonNode.Parse(File.ReadAllText(installed))!["InstallationList"]![0]!["InstallLocation"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_corrupt_copy_never_switches_records_or_removes_the_original()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var progress = new Observe(p =>
        {
            if (p.Stage == "Checking the copy") File.WriteAllText(Path.Combine(plan.To, "data.pak"), "bad! data");
        });
        var result = await EpicMover.RunAsync(plan, progress, CancellationToken.None, installed, () => false);
        Assert.False(result.Ok);
        Assert.Contains("differ", result.Message);
        Assert.True(Directory.Exists(plan.From));
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
        Assert.Equal(Installed(plan.From), File.ReadAllText(installed));
    }

    [Fact]
    public async Task Malformed_second_record_leaves_the_first_record_and_original_untouched()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        File.WriteAllText(installed, "broken");
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.False(result.Ok);
        Assert.True(Directory.Exists(plan.From));
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
        Assert.Equal("broken", File.ReadAllText(installed));
    }

    [Fact]
    public async Task A_locked_second_record_rolls_back_the_first()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        using var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false);
        Assert.False(result.Ok);
        Assert.True(Directory.Exists(plan.From));
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
        Assert.Equal(Installed(plan.From), File.ReadAllText(installed));
    }

    [Fact]
    public async Task A_locked_third_record_rolls_back_the_first_two()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        var egi = t.File(@"eosh\game-id.egi", Egi(plan.From));
        plan = plan with { EoshFile = egi };
        using (new FileStream(egi, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        Assert.Equal(Item(plan.From), File.ReadAllText(plan.ItemFile));
        Assert.Equal(Installed(plan.From), File.ReadAllText(installed));
        Assert.Equal(Egi(plan.From), File.ReadAllText(egi));
        Assert.True(Directory.Exists(plan.From));
    }

    [Fact]
    public void Verification_detects_same_size_corruption_and_different_filenames()
    {
        using var t = new TempTree();
        t.File(@"a\file", "abcd");
        t.File(@"b\file", "abce");
        Assert.Contains("contents", EpicMover.VerifyCopy(t.Dir("a"), t.Dir("b"), CancellationToken.None));
        File.Move(Path.Combine(t.Root, @"b\file"), Path.Combine(t.Root, @"b\other"));
        Assert.Contains("missing", EpicMover.VerifyCopy(t.Dir("a"), t.Dir("b"), CancellationToken.None));
    }

    [Fact]
    public async Task Unrelated_destination_content_is_never_overwritten()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Directory.CreateDirectory(plan.To);
        File.WriteAllText(Path.Combine(plan.To, "unrelated"), "precious");
        Assert.False((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(plan.To, "unrelated")));
        Assert.True(Directory.Exists(plan.From));
    }

    [Fact]
    public async Task A_destination_junction_is_rejected_before_copying()
    {
        using var t = new TempTree();
        var (plan, installed) = Setup(t);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.To)!);
        var outside = t.Dir("outside");
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", plan.To, outside }) psi.ArgumentList.Add(arg);
        using (var process = Process.Start(psi)!) { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        try
        {
            Assert.False((await EpicMover.RunAsync(plan, null, CancellationToken.None, installed, () => false)).Ok);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.True(Directory.Exists(plan.From));
        }
        finally { Directory.Delete(plan.To); }
    }
}

public class MovedGameRulesTests
{
    private const string Old = @"C:\Program Files\Epic Games\Game";
    private const string New = @"D:\Epic Games\Game";

    private static string? Validate(string old = Old, string now = New, string[]? locations = null,
        Func<string, bool>? exists = null, Func<string, bool>? links = null) =>
        MovedGameRules.Validate(old, now, locations ?? [New], exists ?? (_ => true), links ?? (_ => false));

    [Theory]
    [InlineData(Old)]
    [InlineData(@"C:\Epic Games\Game")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Game")]
    public void Standard_library_game_folders_are_accepted(string old) => Assert.Null(Validate(old));

    [Theory]
    [InlineData(@"C:\Windows\Epic Games\Game")]
    [InlineData(@"C:\Epic Games")]
    [InlineData(@"C:\")]
    [InlineData(@"relative\Epic Games\Game")]
    [InlineData(@"\\server\Epic Games\Game")]
    [InlineData(@"\\?\C:\Epic Games\Game")]
    [InlineData(@"C:\Epic Games\..\Game")]
    [InlineData(@"C:\Epic Games\Game:stream")]
    [InlineData(@"C:\Epic Games\Game.")]
    [InlineData(@"C:\Epic Games\Game ")]
    [InlineData(@"C:\Epic Games\Game\nested")]
    [InlineData(@"C:\Epic Games\Salad")]
    [InlineData("")]
    public void Arbitrary_roots_aliases_and_protected_folders_are_rejected(string old) => Assert.NotNull(Validate(old));

    [Fact]
    public void Deletion_requires_both_folders_the_same_game_another_drive_and_no_links()
    {
        Assert.NotNull(Validate(exists: p => p != Old));
        Assert.NotNull(Validate(exists: p => p != New));
        Assert.NotNull(Validate(now: Old));
        Assert.NotNull(Validate(now: @"D:\Epic Games\Other"));
        Assert.NotNull(Validate(now: @"C:\Epic Games\Game"));
        Assert.NotNull(Validate(now: @"D:\Other\Game"));
        Assert.NotNull(Validate(links: p => p == Old));
        Assert.NotNull(Validate(links: p => p == New));
        Assert.NotNull(Validate(locations: []));
        Assert.NotNull(Validate(locations: [New, Old]));
        Assert.NotNull(Validate(locations: [New, Old + @"\DLC"]));
        Assert.Null(Validate(locations: ["d:/epic games/game/"]));
    }

    [Theory]
    [InlineData("../game")]
    [InlineData(@"C:\game.item")]
    [InlineData("game.item")]
    [InlineData("game:stream")]
    [InlineData("game\n")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Helper_rejects_invalid_identifiers_before_reading_or_deleting_anything(string? id)
    {
        var helper = new HelperOperations(new HelperLog(), null);
        var result = await helper.ExecuteAsync(new RemoveMovedGameOp(id!), true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, result.Status);
    }

    [Fact]
    public void Protocol_carries_only_the_manifest_identifier()
    {
        HelperOp op = new RemoveMovedGameOp("ABC123_game-id");
        var json = HelperJson.Serialize(op);
        Assert.DoesNotContain("Folder", json);
        Assert.Equal(op, HelperJson.Deserialize<HelperOp>(json));
        Assert.True(MovedGameRules.IsValidManifestId("ABC123_game-id"));
        Assert.False(MovedGameRules.IsValidManifestId(new string('a', 121)));
    }

    [Fact]
    public async Task The_dev_helper_never_deletes_real_games()
    {
        var helper = new HelperOperations(new HelperLog(), null, devUnelevated: true);
        var result = await helper.ExecuteAsync(new RemoveMovedGameOp("game-id"), false, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, result.Status);
        Assert.Contains("only dry runs", result.Message);
    }
}
