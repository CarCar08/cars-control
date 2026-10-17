using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using CarsControl.Core.Backup;
using CarsControl.Core.Data;
using CarsControl.Core.Settings;
using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cars-bk-" + Guid.NewGuid().ToString("N")[..8]);

    public TempTree() => Directory.CreateDirectory(Root);

    public string Dir(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string text = "x", DateTime? modified = null)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        if (modified is { } m) System.IO.File.SetLastWriteTime(p, m);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class BackupEngineTests
{
    [Fact]
    public void Labels_cannot_collide_with_another_folders_literal_suffix()
    {
        var labels = BackupEngine.Labels([@"C:\one\docs", @"C:\two\docs", @"C:\three\docs (2)"]);
        Assert.Equal(3, labels.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task An_unreadable_source_subfolder_is_reported_and_its_existing_backup_is_kept()
    {
        using var t = new TempTree();
        t.File(@"docs\locked\a.txt", "source");
        var saved = t.File(@"backup\docs\locked\a.txt", "existing backup");
        var dir = new DirectoryInfo(Path.Combine(t.Root, @"docs\locked"));
        var before = dir.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var blocked = dir.GetAccessControl();
        blocked.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        dir.SetAccessControl(blocked);
        try
        {
            var result = await BackupEngine.RunAsync(Plan(t, ["docs"]));
            Assert.False(result.Ok);
            Assert.True(result.Errors > 0);
            Assert.Equal(0, result.Archived);
            Assert.Equal("existing backup", File.ReadAllText(saved));
        }
        finally { dir.SetAccessControl(before); }
    }

    [Fact]
    public async Task Destination_junction_cannot_write_or_archive_files_outside_the_backup()
    {
        using var t = new TempTree();
        t.File(@"docs\a.txt", "new source");
        var outside = t.Dir("outside");
        var saved = t.File(@"outside\a.txt", "leave this alone");
        t.Dir("backup");
        var junction = Path.Combine(t.Root, @"backup\docs");
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
        try
        {
            Assert.Contains("junction", BackupEngine.Validate(Plan(t, ["docs"])));
            await Assert.ThrowsAsync<InvalidOperationException>(() => BackupEngine.RunAsync(Plan(t, ["docs"])));
            Assert.Equal("leave this alone", File.ReadAllText(saved));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task Retention_does_not_follow_a_linked_versions_directory()
    {
        using var t = new TempTree();
        var outside = t.Dir("outside");
        var saved = t.File(@"outside\20200101-000000\keep.txt", "keep");
        t.Dir("backup");
        var junction = Path.Combine(t.Root, @"backup\.cars-versions");
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
        try
        {
            Assert.Equal(0, BackupEngine.PurgeVersions(Path.Combine(t.Root, "backup"), DateTime.Now, 1));
            Assert.Equal("keep", File.ReadAllText(saved));
        }
        finally { Directory.Delete(junction); }
    }

    private static BackupOptions Plan(TempTree t, string[] sources, string dest = "backup", int keep = 30, string[]? excludes = null, DateTime? now = null) =>
        new(sources.Select(s => Path.Combine(t.Root, s)).ToList(), Path.Combine(t.Root, dest), keep, excludes, now);

    [Fact]
    public async Task The_first_run_copies_everything_and_the_second_finds_nothing_to_do()
    {
        using var t = new TempTree();
        t.File(@"docs\a.txt", "hello");
        t.File(@"docs\sub\b.txt", "world");
        var first = await BackupEngine.RunAsync(Plan(t, ["docs"]));
        Assert.True(first.Ok);
        Assert.Equal(2, first.Copied);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(t.Root, @"backup\docs\a.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(t.Root, @"backup\docs\sub\b.txt")));

        var second = await BackupEngine.RunAsync(Plan(t, ["docs"]));
        Assert.Equal(0, second.Copied + second.Updated + second.Archived);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public async Task A_changed_file_is_copied_and_the_old_one_kept_as_a_version()
    {
        using var t = new TempTree();
        t.File(@"docs\a.txt", "one", new DateTime(2026, 1, 1, 10, 0, 0));
        await BackupEngine.RunAsync(Plan(t, ["docs"]));
        t.File(@"docs\a.txt", "two!", new DateTime(2026, 2, 2, 10, 0, 0));
        var r = await BackupEngine.RunAsync(Plan(t, ["docs"], now: new DateTime(2026, 3, 3, 4, 5, 6)));

        Assert.Equal(1, r.Updated);
        Assert.Equal(1, r.Archived);
        Assert.Equal("two!", File.ReadAllText(Path.Combine(t.Root, @"backup\docs\a.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(t.Root, @"backup\.cars-versions\20260303-040506\docs\a.txt")));
    }

    [Fact]
    public async Task A_file_removed_from_the_source_is_kept_in_versions_not_deleted()
    {
        using var t = new TempTree();
        var file = t.File(@"docs\gone.txt", "precious");
        t.File(@"docs\stay.txt");
        await BackupEngine.RunAsync(Plan(t, ["docs"]));
        File.Delete(file);
        var r = await BackupEngine.RunAsync(Plan(t, ["docs"], now: new DateTime(2026, 5, 5, 1, 2, 3)));
        Assert.Equal(1, r.Archived);
        Assert.False(File.Exists(Path.Combine(t.Root, @"backup\docs\gone.txt")));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(t.Root, @"backup\.cars-versions\20260505-010203\docs\gone.txt")));
        Assert.True(File.Exists(Path.Combine(t.Root, @"backup\docs\stay.txt")));
    }

    [Fact]
    public async Task Old_version_folders_are_purged_and_nothing_else_is()
    {
        using var t = new TempTree();
        t.File(@"docs\a.txt");
        await BackupEngine.RunAsync(Plan(t, ["docs"]));
        var versions = Path.Combine(t.Root, @"backup\.cars-versions");
        Directory.CreateDirectory(Path.Combine(versions, "20260101-000000"));
        File.WriteAllText(Path.Combine(versions, @"20260101-000000\old.txt"), "old");
        Directory.CreateDirectory(Path.Combine(versions, "20260320-000000"));
        Directory.CreateDirectory(Path.Combine(versions, "not-a-timestamp"));
        var r = await BackupEngine.RunAsync(Plan(t, ["docs"], keep: 30, now: new DateTime(2026, 4, 1)));
        Assert.Equal(1, r.PurgedVersions);
        Assert.False(Directory.Exists(Path.Combine(versions, "20260101-000000")));
        Assert.True(Directory.Exists(Path.Combine(versions, "20260320-000000")));
        Assert.True(Directory.Exists(Path.Combine(versions, "not-a-timestamp")));
        Assert.True(File.Exists(Path.Combine(t.Root, @"backup\docs\a.txt")));
    }

    [Fact]
    public async Task Excluded_names_and_folders_are_left_out_and_not_treated_as_deleted()
    {
        using var t = new TempTree();
        t.File(@"docs\keep.txt");
        t.File(@"docs\scratch.tmp");
        t.File(@"docs\node_modules\pkg\x.js");
        var plan = Plan(t, ["docs"], excludes: ["*.tmp", "node_modules"]);
        var r = await BackupEngine.RunAsync(plan);
        Assert.Equal(1, r.Copied);
        Assert.False(File.Exists(Path.Combine(t.Root, @"backup\docs\scratch.tmp")));
        Assert.False(Directory.Exists(Path.Combine(t.Root, @"backup\docs\node_modules")));
        var again = await BackupEngine.RunAsync(plan);
        Assert.Equal(0, again.Archived);
    }

    [Fact]
    public async Task Two_folders_with_the_same_name_do_not_overwrite_each_other()
    {
        using var t = new TempTree();
        t.File(@"a\Photos\1.jpg", "A");
        t.File(@"b\Photos\1.jpg", "B");
        var r = await BackupEngine.RunAsync(Plan(t, [@"a\Photos", @"b\Photos"]));
        Assert.Equal(2, r.Copied);
        Assert.Equal("A", File.ReadAllText(Path.Combine(t.Root, @"backup\Photos\1.jpg")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(t.Root, @"backup\Photos (2)\1.jpg")));
    }

    [Fact]
    public async Task A_file_another_program_holds_exclusively_is_reported_and_the_rest_still_copy()
    {
        using var t = new TempTree();
        var locked = t.File(@"docs\locked.dat", "busy");
        t.File(@"docs\fine.txt");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var r = await BackupEngine.RunAsync(Plan(t, ["docs"]));
        Assert.Equal(1, r.Copied);
        Assert.Equal(1, r.Errors);
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("locked.dat") && p.Contains("open"));
        Assert.True(File.Exists(Path.Combine(t.Root, @"backup\docs\fine.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(t.Root, "backup"), "*.cars-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancelling_stops_cleanly()
    {
        using var t = new TempTree();
        for (var i = 0; i < 20; i++) t.File($@"docs\f{i}.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await BackupEngine.RunAsync(Plan(t, ["docs"]), null, cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Plans_that_could_loop_or_touch_windows_are_refused()
    {
        using var t = new TempTree();
        t.Dir("docs");
        Assert.Contains("inside", BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, "docs")], Path.Combine(t.Root, @"docs\backup")))!);
        Assert.Contains("inside", BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, @"docs")], t.Root))!);
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([], Path.Combine(t.Root, "b"))));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, "nope")], Path.Combine(t.Root, "b"))));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([@"relative\folder"], Path.Combine(t.Root, "b"))));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([Environment.GetFolderPath(Environment.SpecialFolder.Windows)], Path.Combine(t.Root, "b"))));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([@"C:\"], Path.Combine(t.Root, "b"))));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, "docs")], @"C:\")));
        Assert.NotNull(BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, "docs")], "")));
        Assert.Null(BackupEngine.Validate(new BackupOptions([Path.Combine(t.Root, "docs")], Path.Combine(t.Root, "backup"))));
    }

    [Theory]
    [InlineData("*.tmp", "a.tmp", true)]
    [InlineData("*.tmp", "a.txt", false)]
    [InlineData("node_modules", @"src\node_modules\x\y.js", true)]
    [InlineData("Thumbs.db", "thumbs.db", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    public void Exclude_patterns_match_names_and_folders_on_the_way(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, BackupEngine.Excluded(path, [pattern]));
}

public class VolumesTests
{
    [Theory]
    [InlineData("Disk #1, Partition #0", 1)]
    [InlineData("Disk #12, Partition #3", 12)]
    [InlineData("disk#0, Partition #1", 0)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Disk_numbers_come_from_the_partition_id(string? id, int? expected) => Assert.Equal(expected, Volumes.ParseDiskNumber(id));

    [Fact]
    public void The_same_letter_is_the_same_disk_and_odd_paths_are_not()
    {
        Assert.True(Volumes.SameDisk(@"C:\Users", @"c:\Backups"));
        Assert.False(Volumes.SameDisk(@"\\server\share", @"C:\x"));
        Assert.False(Volumes.SameDisk("", "C:"));
    }

    [Fact]
    public void Every_ready_drive_has_a_volume_id_and_it_can_be_found_again()
    {
        var c = Volumes.List().FirstOrDefault(v => v.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(c);
        Assert.Matches("^[0-9A-F]{8}$", c!.Serial);
        Assert.Equal(c.Root, Volumes.Find(c.Serial)!.Root.Length >= 3 ? c.Root : null);
        Assert.Null(Volumes.Find("DEADBEEF-not-a-volume"));
    }
}

public class SetupBundleTests
{
    [Fact]
    public void Malformed_synced_scalar_values_are_skipped_without_blocking_valid_choices()
    {
        var content = new SetupContent(new JsonObject
        {
            ["RunInBackground"] = "not a boolean", ["CheckIntervalHours"] = new JsonArray(1, 2),
            ["HelperIdleMinutes"] = 999999999999L, ["FastScan"] = false
        }, null, null, null, null);
        var target = new CarsSettings();
        var background = target.RunInBackground;
        var report = SetupBundles.Apply(target, new SetupBundle(1, "test", DateTime.UtcNow, "test", content), SetupParts.Preferences);
        Assert.Equal(3, report.Skipped.Count);
        Assert.Equal(background, target.RunInBackground);
        Assert.False(target.FastScan);
        Assert.Contains("Fast scan", report.Changed);
    }

    private static CarsSettings Settings()
    {
        var s = new CarsSettings();
        s.Appearance.Theme = ThemeChoice.Light;
        s.Maintenance.Time = "02:30";
        s.Maintenance.IdleMinutes = 9;
        s.Gaming.ExtraGames = ["mygame.exe"];
        s.Palette.Usage = new() { ["page:home"] = 40 };
        s.Alerts.Enabled = true;
        s.Alerts.Topic = "cars-secrettopic12345";
        s.Alerts.ProtectedToken = "dpapi-blob";
        s.Alerts.DeviceLabel = "Desktop";
        s.Updates.Repository = "someone/else";
        s.Profile.HoldDriverUpdates = true;
        s.FirstRunCompleted = "0.8.0";
        s.ProtectedApps = ["Tailscale", "Windhawk"];
        return s;
    }

    private static readonly Dictionary<string, IgnoreRule> Ignores = new() { ["Foo.Bar"] = new IgnoreRule("Foo.Bar", "1.2", DateTime.Now) };

    [Fact]
    public void The_bundle_carries_choices_but_never_secrets_or_the_update_source()
    {
        var b = SetupBundles.Build(Settings(), Ignores, [new AppListEntry("Git.Git", "Git")], SetupParts.Default | SetupParts.Alerts, "DESK-PC", "0.8.0");
        var json = SetupBundles.Serialize(b);
        Assert.DoesNotContain("dpapi-blob", json);
        Assert.DoesNotContain("someone/else", json);
        Assert.DoesNotContain("Desktop", json);
        Assert.DoesNotContain("HoldDriverUpdates", json);
        Assert.DoesNotContain("FirstRunCompleted", json);
        Assert.DoesNotContain("page:home", json); // palette usage is per PC
        Assert.Contains("02:30", json);
        Assert.Contains("mygame.exe", json);
        Assert.Contains("Foo.Bar", json);
        Assert.Contains("Git.Git", json);
        Assert.Contains("cars-secrettopic12345", json); // only because Alerts was asked for
    }

    [Fact]
    public void Alerts_are_left_out_unless_chosen()
    {
        var json = SetupBundles.Serialize(SetupBundles.Build(Settings(), Ignores, [], SetupParts.Default, "PC", "1"));
        Assert.DoesNotContain("cars-secrettopic12345", json);
    }

    [Fact]
    public void A_bundle_round_trips_and_applies_to_another_pcs_settings()
    {
        var parsed = SetupBundles.Parse(SetupBundles.Serialize(SetupBundles.Build(Settings(), Ignores, [], SetupParts.Default, "PC-A", "0.8.0")))!;
        Assert.Equal("PC-A", parsed.Device);

        var other = new CarsSettings();
        other.Updates.Repository = "mine/mine";
        other.Profile.HoldDriverUpdates = false;
        other.Alerts.ProtectedToken = "mine";
        var ignored = new List<IgnoreEntry>();
        var report = SetupBundles.Apply(other, parsed, SetupParts.Default, ignored.Add);

        Assert.Equal(ThemeChoice.Light, other.Appearance.Theme);
        Assert.Equal("02:30", other.Maintenance.Time);
        Assert.Equal(["mygame.exe"], other.Gaming.ExtraGames);
        Assert.Equal(["Tailscale", "Windhawk"], other.ProtectedApps);
        Assert.Equal("Foo.Bar", Assert.Single(ignored).Key);
        Assert.Equal("mine/mine", other.Updates.Repository);
        Assert.False(other.Profile.HoldDriverUpdates);
        Assert.Equal("mine", other.Alerts.ProtectedToken);
        Assert.True(report.AnythingChanged);
        Assert.Contains("Appearance", report.Changed);
    }

    [Fact]
    public void A_hostile_file_cannot_push_odd_values_in()
    {
        var json = """
            { "version": 1, "device": "x", "savedUtc": "2026-10-14T00:00:00Z", "appVersion": "9", "content": {
                "preferences": { "CheckIntervalHours": 9999, "HelperIdleMinutes": -5,
                    "Maintenance": { "Time": "not a time", "IdleMinutes": 100000, "Enabled": true },
                    "Health": { "CpuAlertC": 5, "GpuAlertC": 900 },
                    "Updates": { "Repository": "attacker/evil" } },
                "alerts": { "Enabled": true, "Server": "http://evil.example", "Topic": "ok-topic-12345" },
                "apps": [ { "id": "Git.Git; calc", "name": "x" } ] } }
            """;
        var b = SetupBundles.Parse(json)!;
        var s = new CarsSettings();
        var report = SetupBundles.Apply(s, b, SetupParts.Default | SetupParts.Alerts);
        Assert.Equal(48, s.CheckIntervalHours);
        Assert.Equal(1, s.HelperIdleMinutes);
        Assert.Equal("03:00", s.Maintenance.Time);
        Assert.Equal(240, s.Maintenance.IdleMinutes);
        Assert.Equal(60, s.Health.CpuAlertC);
        Assert.Equal(110, s.Health.GpuAlertC);
        Assert.Equal("CarCar08/cars-control", s.Updates.Repository);
        Assert.False(s.Alerts.Enabled);
        Assert.Contains(report.Skipped, x => x.Contains("https"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "version": 99, "content": {} }""")]
    [InlineData("""{ "version": 0, "content": {} }""")]
    public void Anything_that_isnt_a_setup_file_is_refused(string json) => Assert.Null(SetupBundles.Parse(json));

    [Fact]
    public async Task Files_round_trip_atomically_and_oversized_ones_are_ignored()
    {
        using var t = new TempTree();
        var path = Path.Combine(t.Root, "sync", "setup.json");
        var b = SetupBundles.Build(Settings(), Ignores, [], SetupParts.Default, "PC", "1");
        await SetupBundles.WriteAsync(path, b);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(b.Content.Preferences!.ToJsonString(), (await SetupBundles.ReadAsync(path))!.Content.Preferences!.ToJsonString());
        File.WriteAllText(path, new string('x', (int)SetupBundles.MaxBytes + 10));
        Assert.Null(await SetupBundles.ReadAsync(path));
        Assert.Null(await SetupBundles.ReadAsync(Path.Combine(t.Root, "missing.json")));
    }

    [Fact]
    public void The_hash_follows_content_not_who_saved_it_or_when()
    {
        var a = SetupBundles.Build(Settings(), Ignores, [], SetupParts.Default, "PC-A", "1");
        var b = SetupBundles.Build(Settings(), Ignores, [], SetupParts.Default, "PC-B", "2");
        Assert.Equal(SetupBundles.ContentHash(a.Content), SetupBundles.ContentHash(b.Content));
        var changed = Settings();
        changed.Maintenance.Time = "05:00";
        Assert.NotEqual(SetupBundles.ContentHash(a.Content), SetupBundles.ContentHash(SetupBundles.Build(changed, Ignores, [], SetupParts.Default, "PC-A", "1").Content));
    }

    [Fact]
    public void Which_parts_to_sync_follow_the_toggles()
    {
        Assert.Equal(SetupParts.Default, SetupSync.PartsOf(new SyncSettings()));
        Assert.Equal(SetupParts.Alerts, SetupSync.PartsOf(new SyncSettings { Preferences = false, Ignored = false, Protected = false, Apps = false, Alerts = true }));
    }

    [Theory]
    [InlineData("a", "a", null, SyncAction.Push)]
    [InlineData("a", "a", "a", SyncAction.InSync)]
    [InlineData("b", "a", "a", SyncAction.Push)]
    [InlineData("a", "a", "c", SyncAction.Pull)]
    [InlineData("b", "a", "c", SyncAction.Conflict)]
    [InlineData("b", null, "c", SyncAction.Conflict)]
    [InlineData("b", "b", "b", SyncAction.InSync)]
    public void Sync_decisions_compare_both_sides_with_the_last_agreed_state(string local, string? last, string? remote, SyncAction expected) =>
        Assert.Equal(expected, SetupSync.Decide(local, last, remote));
}

public class FleetTests
{
    private static FleetStatus Status(string device, DateTime at, int updates = 0, int warnings = 0, int bsod = 0) =>
        new(device, "0.8.0", at, "Windows 11 Pro", updates, 100_000_000_000, 2_000_000_000_000, warnings, bsod, null, null, null, 12);

    [Theory]
    [InlineData("DESK-PC", "status-DESK-PC.json")]
    [InlineData("Sam's Laptop!", "status-Sam-s-Laptop.json")]
    [InlineData("..\\evil", "status-evil.json")]
    [InlineData("", "status-pc.json")]
    public void File_names_are_safe(string device, string expected) => Assert.Equal(expected, FleetFiles.FileNameFor(device));

    [Fact]
    public async Task Every_pc_in_the_folder_is_listed_this_one_first_and_old_ones_marked_stale()
    {
        using var t = new TempTree();
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        await FleetFiles.WriteAsync(t.Root, Status("Zed-Laptop", now.AddMinutes(-20), updates: 3));
        await FleetFiles.WriteAsync(t.Root, Status("DESK-PC", now.AddMinutes(-5), warnings: 1, bsod: 2));
        await FleetFiles.WriteAsync(t.Root, Status("Old-Mac", now.AddDays(-5)));
        File.WriteAllText(Path.Combine(t.Root, "status-broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(t.Root, "notes.json"), "{}");
        var list = await FleetFiles.ReadAllAsync(t.Root, "desk-pc", now);
        Assert.Equal(["DESK-PC", "Old-Mac", "Zed-Laptop"], list.Select(e => e.Status.Device));
        Assert.True(list[0].IsThisPc);
        Assert.False(list[0].Stale);
        Assert.True(list.Single(e => e.Status.Device == "Old-Mac").Stale);
        Assert.Equal("1 health warning, 2 blue screens this week", list[0].Summary);
        Assert.Equal("3 updates waiting", list.Single(e => e.Status.Device == "Zed-Laptop").Summary);
        Assert.Equal("All good", list.Single(e => e.Status.Device == "Old-Mac").Summary);
    }

    [Fact]
    public async Task A_missing_folder_is_an_empty_list()
    {
        Assert.Empty(await FleetFiles.ReadAllAsync(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()), "x", DateTime.UtcNow));
    }

    [Fact]
    public void The_default_folder_is_inside_proton_drive_only()
    {
        var roots = new[] { new CarsControl.Core.Storage.SyncRoot(@"A:\iCloud", "iCloud Drive"), new CarsControl.Core.Storage.SyncRoot(@"A:\Windows (Backup)\Proton Drive\My files", "Proton Drive") };
        Assert.Equal(@"A:\Windows (Backup)\Proton Drive\My files\C.A.R.S.", SetupSync.DefaultFolder(roots));
        Assert.Null(SetupSync.DefaultFolder(roots.Take(1)));
    }
}
