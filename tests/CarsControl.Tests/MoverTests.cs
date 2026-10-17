using System.Diagnostics;
using CarsControl.Core.Storage;

namespace CarsControl.Tests;

public class SteamMoverTests
{
    private static (TempTree Tree, string From, string To, GameEntry Game) Setup()
    {
        var t = new TempTree();
        var from = t.Dir("libA");
        var to = t.Dir("libB");
        Directory.CreateDirectory(Path.Combine(to, "steamapps"));
        t.File(@"libA\steamapps\common\Game One\bin\game.exe", new string('x', 4000));
        t.File(@"libA\steamapps\common\Game One\data\level1.pak", new string('y', 9000));
        t.File(@"libA\steamapps\appmanifest_123.acf", "\"AppState\" { \"appid\" \"123\" }");
        t.File(@"libA\steamapps\workshop\content\123\mod1\mod.bin", new string('m', 1000));
        t.File(@"libA\steamapps\workshop\appworkshop_123.acf", "workshop");
        t.File(@"libA\steamapps\shadercache\123\cache.bin", new string('s', 500));
        var game = new GameEntry("Game One", "Steam", from, Path.Combine(from, @"steamapps\common\Game One"), 13000, null, null, "123");
        return (t, from, to, game);
    }

    private static SteamMovePlan Plan(GameEntry g, string to, string from, bool steam = false, bool running = false) =>
        SteamMover.Plan(g, to, [from, to], () => steam, _ => running);

    [Fact]
    public async Task A_game_moves_with_its_manifest_workshop_and_shader_cache_and_the_old_copy_is_removed()
    {
        var (t, from, to, game) = Setup();
        using (t)
        {
            var plan = Plan(game, to, from);
            Assert.True(plan.CanRun, plan.Blocker);
            Assert.Equal(13000 + 1000 + 500, plan.Bytes);
            var result = await SteamMover.RunAsync(plan, null, CancellationToken.None);
            Assert.True(result.Ok, result.Message);

            Assert.True(File.Exists(Path.Combine(to, @"steamapps\common\Game One\bin\game.exe")));
            Assert.Equal(new string('y', 9000), File.ReadAllText(Path.Combine(to, @"steamapps\common\Game One\data\level1.pak")));
            Assert.True(File.Exists(Path.Combine(to, @"steamapps\appmanifest_123.acf")));
            Assert.True(File.Exists(Path.Combine(to, @"steamapps\workshop\content\123\mod1\mod.bin")));
            Assert.True(File.Exists(Path.Combine(to, @"steamapps\workshop\appworkshop_123.acf")));
            Assert.True(File.Exists(Path.Combine(to, @"steamapps\shadercache\123\cache.bin")));

            Assert.False(Directory.Exists(Path.Combine(from, @"steamapps\common\Game One")));
            Assert.False(File.Exists(Path.Combine(from, @"steamapps\appmanifest_123.acf")));
            Assert.False(Directory.Exists(Path.Combine(from, @"steamapps\workshop\content\123")));
            Assert.False(Directory.Exists(Path.Combine(from, @"steamapps\shadercache\123")));
            Assert.Empty(Directory.EnumerateFiles(to, "*.cars-tmp", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Cancelling_leaves_the_game_where_it_was()
    {
        var (t, from, to, game) = Setup();
        using (t)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = await SteamMover.RunAsync(Plan(game, to, from), null, cts.Token);
            Assert.False(result.Ok);
            Assert.True(result.Cancelled);
            Assert.True(File.Exists(Path.Combine(from, @"steamapps\common\Game One\bin\game.exe")));
            Assert.True(File.Exists(Path.Combine(from, @"steamapps\appmanifest_123.acf")));
            Assert.False(File.Exists(Path.Combine(to, @"steamapps\appmanifest_123.acf")));
        }
    }

    [Fact]
    public async Task A_second_run_after_an_interruption_reuses_what_was_copied()
    {
        var (t, from, to, game) = Setup();
        using (t)
        {
            // An earlier, interrupted run left one file behind.
            var half = Path.Combine(to, @"steamapps\common\Game One\bin");
            Directory.CreateDirectory(half);
            File.Copy(Path.Combine(from, @"steamapps\common\Game One\bin\game.exe"), Path.Combine(half, "game.exe"));
            File.SetLastWriteTimeUtc(Path.Combine(half, "game.exe"), File.GetLastWriteTimeUtc(Path.Combine(from, @"steamapps\common\Game One\bin\game.exe")));
            Assert.True(Plan(game, to, from).CanRun);
            Assert.True((await SteamMover.RunAsync(Plan(game, to, from), null, CancellationToken.None)).Ok);
            Assert.True(File.Exists(Path.Combine(to, @"steamapps\common\Game One\data\level1.pak")));
        }
    }

    [Fact]
    public void Each_reason_not_to_move_is_explained()
    {
        var (t, from, to, game) = Setup();
        using (t)
        {
            Assert.Contains("Close Steam", Plan(game, to, from, steam: true).Blocker);
            Assert.Contains("running", Plan(game, to, from, running: true).Blocker);
            Assert.Contains("already", Plan(game, from, from).Blocker);
            Assert.Contains("isn't one of Steam's libraries", SteamMover.Plan(game, to, [from], () => false, _ => false).Blocker);
            Assert.Contains("isn't one of Steam's libraries", SteamMover.Plan(game, Path.Combine(t.Root, "nolib"), [from, Path.Combine(t.Root, "nolib")], () => false, _ => false).Blocker);
            Assert.Contains("Only Steam", Plan(game with { Source = "Epic" }, to, from).Blocker);
            Assert.Contains("Only Steam", Plan(game with { SteamAppId = "12; calc" }, to, from).Blocker);

            Directory.CreateDirectory(Path.Combine(from, @"steamapps\downloading\123"));
            Assert.Contains("downloading", Plan(game, to, from).Blocker);
            Directory.Delete(Path.Combine(from, @"steamapps\downloading"), true);

            File.Delete(Path.Combine(from, @"steamapps\appmanifest_123.acf"));
            Assert.Contains("manifest", Plan(game, to, from).Blocker);
        }
    }

    [Fact]
    public void A_folder_with_a_junction_inside_is_refused()
    {
        var (t, from, to, game) = Setup();
        using (t)
        {
            var link = Path.Combine(from, @"steamapps\common\Game One\linked");
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{t.Dir("elsewhere")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
            Assert.Contains("links", Plan(game, to, from).Blocker);
        }
    }

    [Fact]
    public void A_copy_that_differs_is_noticed()
    {
        using var t = new TempTree();
        t.File(@"a\x.bin", "12345");
        t.File(@"b\x.bin", "123");
        Assert.NotNull(TreeOps.Differences(Path.Combine(t.Root, "a"), Path.Combine(t.Root, "b")));
        t.File(@"b\x.bin", "12345");
        Assert.Null(TreeOps.Differences(Path.Combine(t.Root, "a"), Path.Combine(t.Root, "b")));
        Assert.NotNull(TreeOps.Differences(Path.Combine(t.Root, "a"), Path.Combine(t.Root, "missing")));
    }
}

public class FolderLinkMoverTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files\Something")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows")]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    public void Windows_programs_and_whole_drives_are_never_moved(string folder) => Assert.NotNull(FolderLinkMover.Refuse(folder, @"D:\"));

    [Fact]
    public void The_profile_and_appdata_roots_and_packages_are_refused_but_a_folder_inside_appdata_is_allowed()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(FolderLinkMover.Refuse(profile, @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(Path.Combine(profile, "AppData"), @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(Path.Combine(profile, @"AppData\Local"), @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(Path.Combine(profile, @"AppData\Local\Packages"), @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(Path.Combine(profile, @"AppData\Local\Packages\Some.App_abc"), @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(Path.Combine(profile, @"AppData\Local\CarsControl"), @"D:\"));
    }

    [Fact]
    public void An_ordinary_folder_on_another_drive_is_allowed_and_the_same_drive_is_not()
    {
        using var t = new TempTree();
        var folder = t.Dir("big stuff");
        Assert.Null(FolderLinkMover.Refuse(folder, @"D:\"));
        Assert.Contains("different drive", FolderLinkMover.Refuse(folder, Path.GetPathRoot(folder)!));
        Assert.Contains("isn't there", FolderLinkMover.Refuse(Path.Combine(t.Root, "nope"), @"D:\"));
        Assert.NotNull(FolderLinkMover.Refuse(@"relative\path", @"D:\"));
    }

    [Fact]
    public async Task A_folder_moves_leaves_a_working_junction_and_can_be_moved_back()
    {
        using var t = new TempTree();
        t.File(@"data\a.bin", new string('a', 3000));
        t.File(@"data\sub\b.bin", new string('b', 2000));
        var original = Path.Combine(t.Root, "data");
        var target = Path.Combine(t.Root, "moved", "data");

        var moved = await FolderLinkMover.MoveAsync(original, target, null, CancellationToken.None);
        Assert.True(moved.Ok, moved.Message);
        Assert.True(new DirectoryInfo(original).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(new string('a', 3000), File.ReadAllText(Path.Combine(original, "a.bin"))); // through the link
        Assert.True(File.Exists(Path.Combine(target, @"sub\b.bin")));
        Assert.False(Directory.Exists(original + ".cars-old"));

        var back = await FolderLinkMover.MoveBackAsync(new LinkedMove(original, target, DateTime.Now, moved.BytesMoved), null, CancellationToken.None);
        Assert.True(back.Ok, back.Message);
        Assert.False(new DirectoryInfo(original).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(new string('b', 2000), File.ReadAllText(Path.Combine(original, @"sub\b.bin")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task A_target_with_other_content_is_refused_and_nothing_changes()
    {
        using var t = new TempTree();
        t.File(@"data\a.bin", "mine");
        t.File(@"moved\data\unrelated.txt", "someone else's");
        var r = await FolderLinkMover.MoveAsync(Path.Combine(t.Root, "data"), Path.Combine(t.Root, @"moved\data"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(new DirectoryInfo(Path.Combine(t.Root, "data")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(Path.Combine(t.Root, @"data\a.bin")));
    }

    [Fact]
    public async Task A_leftover_old_folder_blocks_a_new_move()
    {
        using var t = new TempTree();
        t.File(@"data\a.bin", "x");
        t.Dir("data.cars-old");
        var r = await FolderLinkMover.MoveAsync(Path.Combine(t.Root, "data"), Path.Combine(t.Root, @"moved\data"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("left over", r.Message);
    }

    [Fact]
    public async Task A_folder_a_program_has_open_stays_put_and_the_copy_is_kept_for_next_time()
    {
        using var t = new TempTree();
        var file = t.File(@"data\busy.bin", "in use");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); // blocks renaming the folder
        var r = await FolderLinkMover.MoveAsync(Path.Combine(t.Root, "data"), Path.Combine(t.Root, @"moved\data"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(new DirectoryInfo(Path.Combine(t.Root, "data")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(file));
    }
}
