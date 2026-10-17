using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CarsControl.Setup;

namespace CarsControl.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.8.0", "0.8.1")]
    [InlineData("0.8.0-rc.1", "0.8.0")]
    [InlineData("0.8.0-rc.2", "0.8.0-rc.10")]
    [InlineData("0.8.0-alpha", "0.8.0-beta")]
    [InlineData("0.9.9", "0.10.0")]
    [InlineData("v0.8.0", "1.0.0")]
    public void Orders_versions(string older, string newer)
    {
        Assert.True(SemVer.Parse(older) < SemVer.Parse(newer));
        Assert.True(SemVer.Parse(newer) > SemVer.Parse(older));
    }

    [Fact]
    public void Parses_tags_and_rejects_nonsense()
    {
        Assert.True(SemVer.TryParse("v1.2.3-rc.1+build5", out var v));
        Assert.Equal("1.2.3-rc.1", v.ToString());
        Assert.True(v.IsPreRelease);
        Assert.True(SemVer.TryParse("10.0", out v));
        Assert.Equal("10.0.0", v.ToString());
        Assert.True(SemVer.TryParse("10.0.5.0", out v)); // assembly-style four part
        Assert.False(SemVer.TryParse("latest", out _));
        Assert.False(SemVer.TryParse("", out _));
        Assert.False(SemVer.TryParse(null, out _));
        Assert.False(SemVer.TryParse("1.x.3", out _));
    }
}

public class SetupTestBase : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "cars-setup-" + Guid.NewGuid().ToString("N"));

    protected SetupTestBase() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        if (Directory.Exists(Root)) FolderSwap.DeleteTree(Root);
    }

    /// <summary>A small fake app: the main exe, the helper and a runtime-style subfolder.</summary>
    protected string MakeApp(string name, string version, string exeText = "main")
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "CarsControl.exe"), exeText + version);
        File.WriteAllText(Path.Combine(dir, "CarsControl.Helper.exe"), "helper" + version);
        File.WriteAllText(Path.Combine(dir, "sub", "data.json"), "{\"v\":\"" + version + "\"}");
        return dir;
    }

    protected static byte[] Zip(string folder)
    {
        var path = Path.GetTempFileName();
        SafeZip.Create(folder, path);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    protected string SelfExe()
    {
        var path = Path.Combine(Root, "setup-self.exe");
        File.WriteAllText(path, "setup");
        return path;
    }
}

public class PayloadTests : SetupTestBase
{
    [Fact]
    public void Manifest_matches_its_folder_and_notices_every_kind_of_damage()
    {
        var app = MakeApp("app", "1.0");
        var m = PayloadManifest.Build(app, "1.0.0");
        Assert.Equal(["CarsControl.exe", "CarsControl.Helper.exe", "sub/data.json"], m.Files.Select(f => f.Path));
        Assert.Empty(m.Verify(app));

        File.WriteAllText(Path.Combine(app, "CarsControl.exe"), "tampered");
        File.Delete(Path.Combine(app, "sub", "data.json"));
        File.WriteAllText(Path.Combine(app, "extra.dll"), "x");
        var problems = m.Verify(app);
        Assert.Contains("Damaged: CarsControl.exe", problems);
        Assert.Contains("Missing: sub/data.json", problems);
        Assert.Contains("Unexpected: extra.dll", problems);
    }

    [Fact]
    public void Manifest_round_trips_through_json()
    {
        var m = PayloadManifest.Build(MakeApp("app", "1.0"), "1.2.3", "10.0");
        var back = PayloadManifest.FromJson(m.ToJson());
        Assert.Equal("1.2.3", back.Version);
        Assert.Equal("10.0", back.RequiredRuntime);
        Assert.Equal(m.Files.Select(f => f.Sha256), back.Files.Select(f => f.Sha256));
        Assert.Equal(m.TotalSize, back.TotalSize);
    }

    [Fact]
    public void Zip_slip_entries_are_refused()
    {
        foreach (var evil in new[] { "../outside.txt", "..\\outside.txt", "sub/../../outside.txt", "C:/Windows/evil.dll", "/rooted.txt", "file.txt:stream" })
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(z.CreateEntry(evil).Open())) w.Write("x");
            ms.Position = 0;
            var target = Path.Combine(Root, "extract-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<InvalidDataException>(() => SafeZip.Extract(ms, target));
            Assert.False(File.Exists(Path.Combine(Root, "outside.txt")), evil);
        }
    }

    [Fact]
    public void Extracts_folders_and_reports_progress()
    {
        var app = MakeApp("app", "1.0");
        var target = Path.Combine(Root, "out");
        var seen = new List<int>();
        SafeZip.Extract(new MemoryStream(Zip(app)), target, (p, _) => seen.Add(p));
        Assert.Equal("{\"v\":\"1.0\"}", File.ReadAllText(Path.Combine(target, "sub", "data.json")));
        Assert.Equal(100, seen.Last());
    }
}

public class DotNetRuntimeTests : SetupTestBase
{
    private string FakeDotnet(params string[] versions)
    {
        var root = Path.Combine(Root, "dotnet");
        foreach (var v in versions) Directory.CreateDirectory(Path.Combine(root, "shared", DotNetRuntime.Product, v));
        return root;
    }

    [Theory]
    [InlineData("10.0", new[] { "10.0.1" }, true)]
    [InlineData("10.0", new[] { "10.2.0" }, true)]      // newer minor of the same major is fine
    [InlineData("10.0", new[] { "9.0.8", "8.0.20" }, false)]
    [InlineData("10.0", new[] { "11.0.0" }, false)]      // a different major isn't
    [InlineData("10.0", new[] { "10.0.0-preview.7.25380.108" }, false)] // previews don't count
    [InlineData("10.0", new string[0], false)]
    public void Detects_the_desktop_runtime(string required, string[] installed, bool expected) =>
        Assert.Equal(expected, DotNetRuntime.IsSatisfied(required, installed.Length == 0 ? FakeDotnet() : FakeDotnet(installed)));

    private const string Releases = """
        { "releases": [
          { "release-version": "10.0.2", "windowsdesktop": { "version": "10.0.2", "files": [
              { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.2/windowsdesktop-runtime-10.0.2-win-x64.exe", "hash": "AAAA" },
              { "name": "windowsdesktop-runtime-win-arm64.exe", "rid": "win-arm64", "url": "https://builds.dotnet.microsoft.com/arm.exe", "hash": "BBBB" } ] } },
          { "release-version": "10.0.1", "windowsdesktop": { "version": "10.0.1", "files": [
              { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/old.exe", "hash": "CCCC" } ] } },
          { "release-version": "11.0.0-preview.1", "windowsdesktop": { "version": "11.0.0-preview.1", "files": [
              { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/preview.exe", "hash": "DDDD" } ] } }
        ] }
        """;

    [Fact]
    public void Picks_the_newest_stable_x64_installer_with_its_hash()
    {
        var d = DotNetRuntime.FindInstaller(Releases)!;
        Assert.Equal("10.0.2", d.Version);
        Assert.EndsWith("windowsdesktop-runtime-10.0.2-win-x64.exe", d.Url);
        Assert.Equal("AAAA", d.Sha512);
    }

    [Theory]
    [InlineData("https://builds.dotnet.microsoft.com/x.exe", true)]
    [InlineData("https://download.visualstudio.microsoft.com/x.exe", true)]
    [InlineData("http://builds.dotnet.microsoft.com/x.exe", false)]
    [InlineData("https://evil.example.com/x.exe", false)]
    [InlineData("https://builds.dotnet.microsoft.com.evil.example/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    public void Only_https_microsoft_hosts_are_downloaded_from(string url, bool ok) => Assert.Equal(ok, DotNetRuntime.IsAllowedUrl(url));

    [Fact]
    public void Entries_without_a_hash_or_on_other_hosts_are_ignored()
    {
        const string json = """
            { "releases": [ { "windowsdesktop": { "version": "10.0.3", "files": [
              { "name": "a.exe", "rid": "win-x64", "url": "https://evil.example.com/a.exe", "hash": "AAAA" },
              { "name": "b.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/b.exe", "hash": "" } ] } } ] }
            """;
        Assert.Null(DotNetRuntime.FindInstaller(json));
    }

    [Fact]
    public void Verifies_a_download_against_sha512()
    {
        var file = Path.Combine(Root, "dl.exe");
        File.WriteAllText(file, "installer bytes");
        var good = Hex.Sha512File(file);
        Assert.True(DotNetRuntime.VerifyDownload(file, good.ToUpperInvariant()));
        Assert.False(DotNetRuntime.VerifyDownload(file, new string('0', 128)));
    }
}

[Collection("FolderSwap statics")]
public class FolderSwapTests : SetupTestBase
{
    [Fact]
    public void Swaps_and_keeps_nothing_behind()
    {
        var layout = new InstallLayout(Root);
        Directory.CreateDirectory(layout.InstallDir);
        File.WriteAllText(Path.Combine(layout.InstallDir, "v.txt"), "old");
        Directory.CreateDirectory(layout.NewDir);
        File.WriteAllText(Path.Combine(layout.NewDir, "v.txt"), "new");
        var r = FolderSwap.Swap(layout.NewDir, layout.InstallDir, layout.OldDir);
        Assert.True(r.PreviousExisted);
        Assert.Equal("new", File.ReadAllText(Path.Combine(layout.InstallDir, "v.txt")));
        Assert.True(Directory.Exists(layout.OldDir)); // removed by the engine once everything succeeded
        Assert.False(Directory.Exists(layout.NewDir));
    }

    [Fact]
    public void Failure_after_the_move_puts_the_old_version_back()
    {
        var layout = new InstallLayout(Root);
        Directory.CreateDirectory(layout.InstallDir);
        File.WriteAllText(Path.Combine(layout.InstallDir, "v.txt"), "old");
        Directory.CreateDirectory(layout.NewDir);
        File.WriteAllText(Path.Combine(layout.NewDir, "v.txt"), "new");
        Assert.Throws<IOException>(() => FolderSwap.Swap(layout.NewDir, layout.InstallDir, layout.OldDir, afterSwap: () => throw new IOException("boom")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(layout.InstallDir, "v.txt")));
        Assert.False(Directory.Exists(layout.OldDir));
    }

    [Fact]
    public void A_first_install_that_fails_leaves_no_folder()
    {
        var layout = new InstallLayout(Root);
        Directory.CreateDirectory(layout.NewDir);
        File.WriteAllText(Path.Combine(layout.NewDir, "v.txt"), "new");
        Assert.Throws<IOException>(() => FolderSwap.Swap(layout.NewDir, layout.InstallDir, layout.OldDir, afterSwap: () => throw new IOException("boom")));
        Assert.False(Directory.Exists(layout.InstallDir));
    }
}

[Collection("FolderSwap statics")]
public class InstallEngineTests : SetupTestBase
{
    private (InstallEngine Engine, InstallLayout Layout, SandboxOps Ops, PayloadManifest Manifest) Engine(string version, string? sandboxName = null, string exe = "main")
    {
        var layout = new InstallLayout(Path.Combine(Root, sandboxName ?? "sandbox"));
        var app = MakeApp("app-" + version, version, exe);
        var manifest = PayloadManifest.Build(app, version);
        var ops = new SandboxOps(layout, SelfExe());
        var zip = Zip(app);
        return (new InstallEngine(layout, manifest, () => new MemoryStream(zip), ops), layout, ops, manifest);
    }

    [Fact]
    public void Fresh_install_lays_everything_down()
    {
        var (engine, layout, ops, manifest) = Engine("1.0.0");
        var steps = new List<int>();
        var result = engine.Install((p, _) => steps.Add(p), _ => { });
        Assert.True(result.Ok, result.Message);
        Assert.False(result.Upgraded);
        Assert.Equal("main1.0.0", File.ReadAllText(layout.MainExe(manifest)));
        Assert.True(File.Exists(layout.SetupExe));
        Assert.True(File.Exists(layout.StartMenuShortcut));
        Assert.Equal("1.0.0", InstallEngine.InstalledVersionFromSandbox(layout));
        Assert.Contains("stop-app", ops.Events);
        Assert.Contains("runtime:10.0", ops.Events);
        Assert.Contains("secure-programdata", ops.Events);
        Assert.False(Directory.Exists(layout.NewDir));
        Assert.False(Directory.Exists(layout.OldDir));
        Assert.Equal(100, steps.Last());
        Assert.Equal(steps.OrderBy(s => s), steps); // progress never goes backwards
    }

    [Fact]
    public void Upgrade_replaces_the_old_version_and_leaves_no_old_files()
    {
        var (first, layout, _, m1) = Engine("1.0.0");
        Assert.True(first.Install((_, _) => { }, _ => { }).Ok);
        File.WriteAllText(Path.Combine(layout.InstallDir, "leftover-from-1.0.txt"), "x");

        var (second, _, _, m2) = Engine("1.1.0");
        var result = second.Install((_, _) => { }, _ => { });
        Assert.True(result.Ok, result.Message);
        Assert.True(result.Upgraded);
        Assert.Equal("main1.1.0", File.ReadAllText(layout.MainExe(m2)));
        Assert.False(File.Exists(Path.Combine(layout.InstallDir, "leftover-from-1.0.txt")));
        Assert.Equal("1.1.0", InstallEngine.InstalledVersionFromSandbox(layout));
    }

    [Fact]
    public void A_failure_midway_rolls_back_to_the_installed_version()
    {
        var (first, layout, _, m1) = Engine("1.0.0");
        Assert.True(first.Install((_, _) => { }, _ => { }).Ok);

        var (second, _, ops, _) = Engine("1.1.0");
        ops.FailAfterSwap = () => true;
        var log = new List<string>();
        var result = second.Install((_, _) => { }, log.Add);
        Assert.False(result.Ok);
        Assert.Contains("simulated failure", result.Message);
        Assert.Equal("main1.0.0", File.ReadAllText(layout.MainExe(m1))); // still the working version
        Assert.Equal("1.0.0", InstallEngine.InstalledVersionFromSandbox(layout));
        Assert.False(Directory.Exists(layout.NewDir));
        Assert.False(Directory.Exists(layout.OldDir));
    }

    [Fact]
    public void A_damaged_package_is_refused_before_anything_is_replaced()
    {
        var (first, layout, _, m1) = Engine("1.0.0");
        Assert.True(first.Install((_, _) => { }, _ => { }).Ok);

        var layout2 = layout;
        var app = MakeApp("app-bad", "1.1.0");
        var manifest = PayloadManifest.Build(app, "1.1.0");
        File.WriteAllText(Path.Combine(app, "CarsControl.exe"), "corrupted after the manifest was made");
        var bad = new InstallEngine(layout2, manifest, () => new MemoryStream(Zip(app)), new SandboxOps(layout2, SelfExe()));
        var result = bad.Install((_, _) => { }, _ => { });
        Assert.False(result.Ok);
        Assert.Contains("damaged", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("main1.0.0", File.ReadAllText(layout.MainExe(m1)));
    }

    [Fact]
    public void Uninstall_removes_the_program_and_registration_but_not_the_users_data()
    {
        var (engine, layout, _, _) = Engine("1.0.0");
        Assert.True(engine.Install((_, _) => { }, _ => { }).Ok);
        var machineData = Path.Combine(layout.ProgramData, "CarsControl");
        Directory.CreateDirectory(machineData);
        File.WriteAllText(Path.Combine(machineData, "helper.log"), "x");

        var result = engine.Uninstall(removeMachineData: false, (_, _) => { }, _ => { });
        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(layout.InstallDir));
        Assert.False(File.Exists(layout.StartMenuShortcut));
        Assert.Null(InstallEngine.InstalledVersionFromSandbox(layout));
        Assert.True(File.Exists(Path.Combine(machineData, "helper.log")));

        Assert.True(engine.Install((_, _) => { }, _ => { }).Ok);
        Assert.True(engine.Uninstall(removeMachineData: true, (_, _) => { }, _ => { }).Ok);
        Assert.False(Directory.Exists(machineData));
    }

    [Fact]
    public void Arp_entry_has_what_windows_needs()
    {
        var layout = new InstallLayout(Path.Combine(Root, "arp"));
        var manifest = new PayloadManifest { Version = "1.2.3" };
        var v = ArpEntry.Values(layout, manifest, 5 * 1024 * 1024);
        Assert.Equal("C.A.R.S. Control", v["DisplayName"]);
        Assert.Equal("1.2.3", v["DisplayVersion"]);
        Assert.Equal(5120, v["EstimatedSize"]);
        Assert.Equal("\"" + layout.SetupExe + "\" --uninstall", v["UninstallString"]);
        Assert.Contains("--silent", (string)v["QuietUninstallString"]);
    }
}

public class SetupCommandLineTests
{
    [Fact]
    public void Parses_modes_and_options()
    {
        var c = SetupCommandLine.Parse(["--update", "--silent", "--sandbox", @"C:\t\sb"]);
        Assert.Equal(SetupMode.Update, c.Mode);
        Assert.True(c.Silent);
        Assert.True(c.Launch);
        Assert.Equal(@"C:\t\sb", c.Sandbox);
        Assert.Empty(c.Problems);
        Assert.Equal(SetupMode.Interactive, SetupCommandLine.Parse([]).Mode);
        Assert.Equal(false, SetupCommandLine.Parse(["--uninstall", "--keep-data"]).RemoveData);
        Assert.Equal(true, SetupCommandLine.Parse(["--uninstall", "--remove-data"]).RemoveData);
    }

    [Fact]
    public void The_admin_half_needs_a_pipe_this_setup_generated()
    {
        var good = SetupCommandLine.NewPipeName();
        Assert.True(SetupCommandLine.IsValidPipeName(good));
        var c = SetupCommandLine.Parse(["--elevated", "--pipe", good, "--action", "install"]);
        Assert.Empty(c.Problems);
        Assert.NotEmpty(SetupCommandLine.Parse(["--elevated", "--pipe", @"\\.\pipe\evil", "--action", "install"]).Problems);
        Assert.NotEmpty(SetupCommandLine.Parse(["--elevated", "--pipe", good, "--action", "format"]).Problems);
        Assert.NotEmpty(SetupCommandLine.Parse(["--elevated"]).Problems);
        Assert.NotEmpty(SetupCommandLine.Parse(["--frobnicate"]).Problems);
        Assert.NotEmpty(SetupCommandLine.Parse(["--sandbox"]).Problems);
    }

    [Fact]
    public void Messages_round_trip_and_cannot_contain_line_breaks()
    {
        Assert.True(SetupMessage.TryParse(SetupMessage.Progress(250, "Copying | files\nnow"), out var kind, out var pct, out var text));
        Assert.Equal(("PROGRESS", 100), (kind, pct));
        Assert.DoesNotContain('\n', text);
        Assert.True(SetupMessage.TryParse(SetupMessage.Done(true, "all good"), out kind, out pct, out text));
        Assert.Equal(("DONE", 1, "all good"), (kind, pct, text));
        Assert.True(SetupMessage.TryParse(SetupMessage.Done(false, "nope"), out _, out pct, out _));
        Assert.Equal(0, pct);
        Assert.False(SetupMessage.TryParse("garbage", out _, out _, out _));
    }
}

public class ReleaseTests
{
    private static string Release(string tag, bool pre = false, bool draft = false, bool withSetup = true, bool withSums = true)
    {
        var assets = new List<object>();
        if (withSetup) assets.Add(new { name = $"CarsControl-Setup-{tag.TrimStart('v')}.exe", size = 20_000_000, url = "https://api.github.com/repos/o/r/releases/assets/1", browser_download_url = "https://github.com/o/r/releases/download/x/setup.exe" });
        if (withSums) assets.Add(new { name = "SHA256SUMS.txt", size = 100, url = "https://api.github.com/repos/o/r/releases/assets/2", browser_download_url = "https://github.com/o/r/releases/download/x/SHA256SUMS.txt" });
        return JsonSerializer.Serialize(new { tag_name = tag, name = tag, body = "Notes for " + tag, prerelease = pre, draft, html_url = "https://github.com/o/r/releases/tag/" + tag, published_at = "2026-10-13T12:00:00Z", assets });
    }

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Picks_the_newest_complete_stable_release_that_is_newer()
    {
        var json = List(Release("v0.9.0-rc.1", pre: true), Release("v0.8.1"), Release("v0.8.0"), Release("v0.9.5", withSums: false), Release("v1.0.0", draft: true));
        var pick = ReleaseInfo.PickUpdate(json, SemVer.Parse("0.8.0"), includePreReleases: false)!;
        Assert.Equal("v0.8.1", pick.Tag);
        Assert.Equal("Notes for v0.8.1", pick.Notes);
        Assert.Equal("CarsControl-Setup-0.8.1.exe", pick.SetupAsset!.Name);
        Assert.NotNull(pick.Checksums);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), pick.Published!.Value.ToUniversalTime());
    }

    [Fact]
    public void Nothing_to_do_when_up_to_date_or_the_release_is_incomplete()
    {
        Assert.Null(ReleaseInfo.PickUpdate(List(Release("v0.8.0")), SemVer.Parse("0.8.0"), false));
        Assert.Null(ReleaseInfo.PickUpdate(List(Release("v0.7.0")), SemVer.Parse("0.8.0"), false));
        Assert.Null(ReleaseInfo.PickUpdate(List(Release("v0.9.0", withSetup: false)), SemVer.Parse("0.8.0"), false));
    }

    [Fact]
    public void Prereleases_are_offered_only_when_asked_for_or_already_on_one()
    {
        var json = List(Release("v0.9.0-rc.1", pre: true));
        Assert.Null(ReleaseInfo.PickUpdate(json, SemVer.Parse("0.8.0"), includePreReleases: false));
        Assert.Equal("v0.9.0-rc.1", ReleaseInfo.PickUpdate(json, SemVer.Parse("0.8.0"), includePreReleases: true)!.Tag);
        Assert.Equal("v0.9.0-rc.1", ReleaseInfo.PickUpdate(json, SemVer.Parse("0.8.0-rc.1"), includePreReleases: false)!.Tag);
    }

    [Fact]
    public void Works_on_a_single_release_object_too() =>
        Assert.Equal("v0.8.1", ReleaseInfo.PickUpdate(Release("v0.8.1"), SemVer.Parse("0.8.0"), false)!.Tag);

    [Fact]
    public void Checksum_files_parse_in_both_common_layouts()
    {
        var h1 = new string('a', 64);
        var h2 = new string('B', 64);
        var text = $"{h1}  CarsControl-Setup-0.8.1.exe\r\n{h2} *other file.zip\n\nnot a line\n";
        var map = Checksums.Parse(text);
        Assert.Equal(h1, map["CarsControl-Setup-0.8.1.exe"]);
        Assert.Equal(h2.ToLowerInvariant(), map["other file.zip"]);
        Assert.Equal(2, map.Count);
        Assert.Null(Checksums.HashFor(text, "missing.exe"));
        // What the release build writes, the update check reads.
        Assert.Equal(h1, Checksums.HashFor(Checksums.Build(new Dictionary<string, string> { ["z.exe"] = h1 }), "z.exe"));
    }
}

public class SemVerEqualityTests
{
    [Fact]
    public void Equal_versions_are_equal_by_value()
    {
        var a = SemVer.Parse("0.8.0");
        var b = SemVer.Parse("v0.8.0");
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.False(a == SemVer.Parse("0.8.1"));
        Assert.False(SemVer.Parse("0.8.0-rc.1") == a);
        Assert.True(a != null);
    }
}

public class ReleaseNotesTextTests
{
    [Fact]
    public void Markdown_markers_are_removed_and_list_dashes_become_bullets()
    {
        var md = "## 0.8.0 notes\n\n**Install and update**\n- **`Setup.exe`**: one file, see [the guide](docs/install.md) and *Apps*.\n  - nested item\n\n\n\nLast line";
        var plain = ReleaseNotesText.Plain(md);
        Assert.Equal("0.8.0 notes\n\nInstall and update\n• Setup.exe: one file, see the guide and Apps.\n  • nested item\n\nLast line", plain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Nothing_in_gives_nothing_out(string? md) => Assert.Equal("", ReleaseNotesText.Plain(md));

    [Fact]
    public void Plain_text_and_asterisks_in_math_are_left_alone()
    {
        Assert.Equal("2 * 3 = 6 and a_b_c", ReleaseNotesText.Plain("2 * 3 = 6 and a_b_c"));
    }
}
