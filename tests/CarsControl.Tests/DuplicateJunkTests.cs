using System.Diagnostics;
using CarsControl.Core.Storage;

namespace CarsControl.Tests;

public class DuplicateFinderTests
{
    private static DuplicateOptions Opts(TempTree t, params string[] folders) => new(folders.Select(f => Path.Combine(t.Root, f)).ToList(), MinBytes: 10);

    private static string Big(string seed, int bytes = 200_000) => string.Concat(Enumerable.Repeat(seed, bytes / Math.Max(1, seed.Length) + 1))[..bytes];

    [Fact]
    public async Task Identical_files_in_different_folders_are_one_group()
    {
        using var t = new TempTree();
        t.File(@"a\one.bin", Big("same-content"));
        t.File(@"b\copy of one.bin", Big("same-content"));
        t.File(@"b\other.bin", Big("different--"));
        var groups = await DuplicateFinder.FindAsync(Opts(t, "a", "b"));
        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(200_000, g.Wasted);
        Assert.All(g.Files, f => Assert.Equal(200_000, f.Bytes));
    }

    [Fact]
    public async Task Same_size_but_different_content_is_not_a_duplicate()
    {
        using var t = new TempTree();
        t.File(@"a\x.bin", Big("aaaa"));
        t.File(@"a\y.bin", Big("bbbb"));
        Assert.Empty(await DuplicateFinder.FindAsync(Opts(t, "a")));
    }

    [Fact]
    public async Task Files_that_differ_only_in_the_middle_are_told_apart_by_the_full_hash()
    {
        using var t = new TempTree();
        var first = Big("0123456789", 300_000);
        var second = first[..150_000] + "X" + first[150_001..];
        t.File(@"a\p.bin", first);
        t.File(@"a\q.bin", second);
        Assert.Empty(await DuplicateFinder.FindAsync(Opts(t, "a")));
    }

    [Fact]
    public async Task Small_files_and_a_single_file_are_ignored()
    {
        using var t = new TempTree();
        t.File(@"a\tiny1.txt", "hi");
        t.File(@"a\tiny2.txt", "hi");
        t.File(@"a\lonely.bin", Big("alone"));
        Assert.Empty(await DuplicateFinder.FindAsync(new DuplicateOptions([Path.Combine(t.Root, "a")], MinBytes: 1000)));
    }

    [Fact]
    public async Task Three_copies_make_one_group_with_two_wasted_copies()
    {
        using var t = new TempTree();
        for (var i = 0; i < 3; i++) t.File($@"d{i}\f.bin", Big("triple"));
        var g = Assert.Single(await DuplicateFinder.FindAsync(Opts(t, "d0", "d1", "d2")));
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(2 * 200_000, g.Wasted);
    }

    [Fact]
    public async Task Hard_links_to_one_file_count_once()
    {
        using var t = new TempTree();
        var original = t.File(@"a\orig.bin", Big("linked"));
        var link = Path.Combine(t.Root, @"a\link.bin");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{original}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        Assert.Empty(await DuplicateFinder.FindAsync(Opts(t, "a")));
    }

    [Fact]
    public async Task Skipped_paths_are_never_read()
    {
        using var t = new TempTree();
        t.File(@"a\one.bin", Big("same"));
        t.File(@"a\game\two.bin", Big("same"));
        var opts = Opts(t, "a") with { Skip = p => p.Contains(@"\game", StringComparison.OrdinalIgnoreCase) };
        Assert.Empty(await DuplicateFinder.FindAsync(opts));
    }

    [Fact]
    public async Task Cancelling_stops_the_search()
    {
        using var t = new TempTree();
        for (var i = 0; i < 6; i++) t.File($@"a\f{i}.bin", Big("same"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindAsync(Opts(t, "a"), null, cts.Token));
    }

    private static DuplicateGroup Group(params (string Path, int DaysOld)[] files) =>
        new("h", 100, files.Select(f => new DuplicateFile(f.Path, 100, new DateTime(2026, 10, 1).AddDays(-f.DaysOld))).ToList());

    [Fact]
    public void Keep_rules_choose_one_and_never_all()
    {
        var g = Group((@"D:\old\a.bin", 90), (@"D:\new\a.bin", 1), (@"D:\x.bin", 30));
        Assert.Equal(@"D:\new\a.bin", DuplicateRules.Choose(g, KeepRule.Newest).Keep.Path);
        Assert.Equal(@"D:\old\a.bin", DuplicateRules.Choose(g, KeepRule.Oldest).Keep.Path);
        Assert.Equal(@"D:\x.bin", DuplicateRules.Choose(g, KeepRule.ShortestPath).Keep.Path);
        var inFolder = DuplicateRules.Choose(g, KeepRule.InFolder, @"D:\old");
        Assert.Equal(@"D:\old\a.bin", inFolder.Keep.Path);
        Assert.Equal(2, inFolder.Extras.Count);
        Assert.DoesNotContain(inFolder.Extras, f => f.Path == inFolder.Keep.Path);
        Assert.Equal(g.Files.Count, 1 + DuplicateRules.Choose(g, KeepRule.Newest).Extras.Count);
    }

    [Fact]
    public void A_folder_rule_with_no_match_still_keeps_one()
    {
        var g = Group((@"D:\a\f", 5), (@"D:\b\f", 2));
        var (keep, extras) = DuplicateRules.Choose(g, KeepRule.InFolder, @"D:\nowhere");
        Assert.Equal(@"D:\b\f", keep.Path); // newest as the tie-break
        Assert.Single(extras);
    }

    [Fact]
    public void Folder_matching_respects_boundaries()
    {
        Assert.True(DuplicateRules.IsIn(@"D:\old\a.bin", @"D:\old"));
        Assert.True(DuplicateRules.IsIn(@"D:\old\a.bin", @"d:\OLD\"));
        Assert.False(DuplicateRules.IsIn(@"D:\older\a.bin", @"D:\old"));
    }

    [Fact]
    public void A_file_changed_since_the_scan_is_not_recycled()
    {
        using var t = new TempTree();
        var keepPath = t.File(@"a\keep.bin", "same-same");
        var extraPath = t.File(@"a\extra.bin", "same-same");
        var keep = new DuplicateFile(keepPath, new FileInfo(keepPath).Length, new FileInfo(keepPath).LastWriteTime);
        var extra = new DuplicateFile(extraPath, new FileInfo(extraPath).Length, new FileInfo(extraPath).LastWriteTime);
        Assert.True(DuplicateRules.StillSafeToRecycle(extra, keep));

        File.WriteAllText(extraPath, "changed after the scan!");
        Assert.False(DuplicateRules.StillSafeToRecycle(extra, keep));
        File.Delete(keepPath);
        Assert.False(DuplicateRules.StillSafeToRecycle(extra with { Bytes = new FileInfo(extraPath).Length, Modified = new FileInfo(extraPath).LastWriteTime }, keep));
        Assert.False(DuplicateRules.StillSafeToRecycle(extra, extra));
    }
}

public class JunkFinderTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0);

    [Theory]
    [InlineData("Setup.exe", true)]
    [InlineData("vlc-3.0.20-install.exe", true)]
    [InlineData("ChromeUpdate.exe", true)]
    [InlineData("tool.msi", true)]
    [InlineData("app.msixbundle", true)]
    [InlineData("holiday.exe", false)]
    [InlineData("game.iso", false)]
    [InlineData("notes.txt", false)]
    [InlineData("setup-notes.txt", false)]
    public void Installers_are_recognised_by_kind_and_name(string name, bool installer) => Assert.Equal(installer, JunkFinder.LooksLikeInstaller(name));

    [Fact]
    public void Only_old_installers_in_downloads_are_found()
    {
        using var t = new TempTree();
        t.File(@"Downloads\old-setup.exe", "x", Now.AddDays(-100));
        t.File(@"Downloads\new-setup.exe", "x", Now.AddDays(-5));
        t.File(@"Downloads\photo.jpg", "x", Now.AddDays(-400));
        t.File(@"Downloads\sub\buried-setup.exe", "x", Now.AddDays(-400));
        var found = JunkFinder.Find(new JunkOptions(Path.Combine(t.Root, "Downloads"), [], [], [], Now));
        var item = Assert.Single(found);
        Assert.Equal(JunkKind.Installer, item.Kind);
        Assert.EndsWith("old-setup.exe", item.Path);
        Assert.Contains("100 days", item.Reason);
    }

    [Fact]
    public void The_top_most_empty_folders_are_found_and_recent_or_hidden_ones_are_not()
    {
        using var t = new TempTree();
        var old = Now.AddDays(-60);
        void Dir(string rel, DateTime when) { var p = Path.Combine(t.Root, rel); Directory.CreateDirectory(p); Directory.SetLastWriteTime(p, when); }
        t.File(@"docs\keep\file.txt", "x");
        Dir(@"docs\empty1", old);
        Dir(@"docs\nest\inner\deeper", old);
        Dir(@"docs\nest\inner", old);
        Dir(@"docs\nest", old);
        Dir(@"docs\fresh", Now.AddDays(-1));
        Dir(@"docs\keep\empty2", old);
        var hidden = Path.Combine(t.Root, @"docs\hiddenempty");
        Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.SetLastWriteTime(hidden, old);
        var found = JunkFinder.EmptyFolders(Path.Combine(t.Root, "docs"), Now).Select(i => Path.GetRelativePath(t.Root, i.Path)).Order().ToList();
        Assert.Equal([@"docs\empty1", @"docs\keep\empty2", @"docs\nest"], found);
    }

    [Fact]
    public void A_root_with_nothing_but_empty_folders_lists_them_but_never_the_root()
    {
        using var t = new TempTree();
        Directory.CreateDirectory(Path.Combine(t.Root, @"r\a"));
        Directory.SetLastWriteTime(Path.Combine(t.Root, @"r\a"), Now.AddDays(-30));
        var found = JunkFinder.EmptyFolders(Path.Combine(t.Root, "r"), Now).ToList();
        Assert.Equal(Path.Combine(t.Root, @"r\a"), Assert.Single(found).Path);
    }

    [Fact]
    public void Huge_stale_logs_are_found_and_live_or_small_ones_are_not()
    {
        using var t = new TempTree();
        t.File(@"logs\big-old.log", new string('x', 2000), Now.AddDays(-10));
        t.File(@"logs\big-live.log", new string('x', 2000), Now.AddHours(-1));
        t.File(@"logs\small.log", "x", Now.AddDays(-10));
        t.File(@"logs\big.txt", new string('x', 2000), Now.AddDays(-10));
        var found = JunkFinder.Find(new JunkOptions(null, [], [Path.Combine(t.Root, "logs")], [], Now, HugeLogBytes: 1000));
        Assert.EndsWith("big-old.log", Assert.Single(found).Path);
    }

    [Fact]
    public void Steam_setup_leftovers_are_found_per_game()
    {
        using var t = new TempTree();
        t.File(@"lib\steamapps\common\GameA\_CommonRedist\vcredist\setup.exe", new string('x', 500));
        t.File(@"lib\steamapps\common\GameB\Game.exe", "x");
        var found = JunkFinder.Find(new JunkOptions(null, [], [], [Path.Combine(t.Root, "lib")], Now));
        var item = Assert.Single(found);
        Assert.Equal(JunkKind.GameRedist, item.Kind);
        Assert.True(item.IsFolder);
        Assert.Contains("GameA", item.Reason);
        Assert.True(item.Bytes >= 500);
    }

    [Fact]
    public void Missing_places_find_nothing_and_results_are_biggest_first()
    {
        Assert.Empty(JunkFinder.Find(new JunkOptions(@"C:\no\such\place", [@"C:\no\such"], [@"C:\no\logs"], [@"C:\no\steam"], Now)));
        using var t = new TempTree();
        t.File(@"Downloads\a-setup.msi", new string('x', 10), Now.AddDays(-90));
        t.File(@"Downloads\b-setup.msi", new string('x', 900), Now.AddDays(-90));
        var found = JunkFinder.Find(new JunkOptions(Path.Combine(t.Root, "Downloads"), [], [], [], Now));
        Assert.Equal(["b-setup.msi", "a-setup.msi"], found.Select(f => f.Name));
    }
}
