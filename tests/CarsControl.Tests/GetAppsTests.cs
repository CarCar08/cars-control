using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public class AppListTests
{
    [Fact]
    public void A_list_round_trips()
    {
        var list = new[] { new AppListEntry("Mozilla.Firefox", "Mozilla Firefox"), new AppListEntry("9NZVDKPMR9RD", "Firefox (Store)", "msstore") };
        var back = AppList.Parse(AppList.Serialize(list), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(list, back);
    }

    [Fact]
    public void A_winget_export_file_is_read_too()
    {
        const string json = """
            { "$schema": "https://aka.ms/winget-packages.schema.2.0.json", "CreationDate": "2026-10-13",
              "Sources": [
                { "Packages": [ { "PackageIdentifier": "Git.Git" }, { "PackageIdentifier": "Microsoft.PowerToys" } ], "SourceDetails": { "Name": "winget", "Type": "Microsoft.PreIndexed.Package" } },
                { "Packages": [ { "PackageIdentifier": "9NBLGGH4NNS1" } ], "SourceDetails": { "Name": "msstore" } }
              ] }
            """;
        var apps = AppList.Parse(json, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(["Git.Git", "Microsoft.PowerToys", "9NBLGGH4NNS1"], apps.Select(a => a.Id));
        Assert.Equal("msstore", apps[2].Source);
    }

    [Fact]
    public void Ids_that_could_smuggle_a_command_line_are_dropped_and_counted()
    {
        const string json = """
            { "format": "cars-control-app-list", "version": 1, "apps": [
                { "id": "Good.App", "name": "Good" },
                { "id": "bad id; calc", "name": "x" },
                { "id": "--source=evil", "name": "y" },
                { "id": "Another.App", "name": "z", "source": "evilsource" },
                { "id": "", "name": "none" },
                { "id": "Good.App", "name": "dup" }
            ] }
            """;
        var apps = AppList.Parse(json, out var skipped);
        Assert.Equal(["Good.App"], apps.Select(a => a.Id));
        Assert.Equal(5, skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "apps": 5 }""")]
    public void Anything_else_is_an_empty_list(string json)
    {
        Assert.Empty(AppList.Parse(json, out _));
    }

    [Fact]
    public void Names_are_cleaned_and_a_missing_name_falls_back_to_the_id()
    {
        var apps = AppList.Parse("""{ "apps": [ { "id": "A.B", "name": "" }, { "id": "C.D", "name": "Evil\u0007\nName" } ] }""", out _);
        Assert.Equal("A.B", apps[0].Name);
        Assert.Equal("EvilName", apps[1].Name);
    }

    [Fact]
    public void A_list_is_capped()
    {
        var many = Enumerable.Range(0, AppList.MaxApps + 50).Select(i => new AppListEntry($"Pub.App{i}", "x"));
        Assert.Equal(AppList.MaxApps, AppList.Parse(AppList.Serialize(many), out _).Count);
    }
}

public class WingetSearchRankingTests
{
    private static WingetResult R(string id, string name, string source = "winget") => new(id, name, "x", "1.0", source, null);

    [Fact]
    public void The_main_entry_beats_translations_and_the_exact_name_beats_the_rest()
    {
        var ranked = WingetSearch.Rank(
        [
            R("Mozilla.Firefox.de", "Mozilla Firefox (de)"), R("Mozilla.Firefox.ach", "Mozilla Firefox (ach)"), R("Mozilla.Firefox.ca-valencia", "Mozilla Firefox (ca-valencia)"),
            R("Mozilla.Firefox", "Mozilla Firefox (en-US)"), R("Vendor.FirefoxTool", "Tools for Firefox"), R("Mozilla.Firefox.MSIX", "Mozilla Firefox (MSIX)"),
        ], "firefox").ToList();
        Assert.True(ranked.FindIndex(r => r.Id == "Mozilla.Firefox") < ranked.FindIndex(r => r.Id == "Mozilla.Firefox.de"));
        Assert.True(ranked.FindIndex(r => r.Id == "Mozilla.Firefox.MSIX") < ranked.FindIndex(r => r.Id == "Mozilla.Firefox.ach"));
        Assert.True(ranked.FindIndex(r => r.Id == "Vendor.FirefoxTool") < ranked.FindIndex(r => r.Id == "Mozilla.Firefox.ca-valencia"));
    }

    [Fact]
    public void An_exact_id_comes_first_and_the_community_repository_comes_before_the_store()
    {
        var ranked = WingetSearch.Rank([R("XP9KHM4BK9FZ7Q", "Visual Studio Code", "msstore"), R("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code"), R("Git.Git", "Git")], "git").ToList();
        Assert.Equal("Git.Git", ranked[0].Id);
        var vs = WingetSearch.Rank([R("XP9KHM4BK9FZ7Q", "Visual Studio Code", "msstore"), R("Microsoft.VisualStudioCode", "Visual Studio Code")], "visual studio code").ToList();
        Assert.Equal("winget", vs[0].Source);
    }

    [Fact]
    public void Duplicates_collapse()
    {
        Assert.Single(WingetSearch.Rank([R("A.B", "One"), R("A.B", "One again")], "one"));
    }

    [Theory]
    [InlineData("Mozilla Firefox (de)", "Mozilla.Firefox.de", true)]
    [InlineData("Mozilla Firefox (en-US)", "Mozilla.Firefox", false)]
    [InlineData("Mozilla Firefox (MSIX)", "Mozilla.Firefox.MSIX", false)]
    [InlineData("Some Tool (beta)", "Vendor.Tool.beta", false)]
    [InlineData("Mozilla Firefox (pt-BR)", "Mozilla.Firefox.pt-BR", true)]
    public void Translations_are_recognised_by_name_and_id(string name, string id, bool expected) => Assert.Equal(expected, WingetSearch.IsTranslation(R(id, name)));
}

public class WingetInstallTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    [Fact]
    public void Install_arguments_are_fixed_and_carry_scope_and_source()
    {
        var args = WingetCli.InstallArgs("Git.Git", "winget", "user", null, null);
        Assert.Equal(["install", "--id", "Git.Git", "--exact", "--source", "winget", "--scope", "user", "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"], args);
        var machine = WingetCli.InstallArgs("Git.Git", "msstore", "machine", "2.46.0", @"C:\logs\x.log");
        Assert.Contains("machine", machine);
        Assert.Equal("2.46.0", machine[machine.ToList().IndexOf("--version") + 1]);
        Assert.Equal(@"C:\logs\x.log", machine[^1]);
    }

    [Theory]
    [InlineData("winget", true)]
    [InlineData("msstore", true)]
    [InlineData("Winget", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("", false)]
    public void Only_the_two_known_sources_are_valid(string source, bool valid) => Assert.Equal(valid, WingetCli.IsValidSource(source));

    [Theory]
    [InlineData("bad id")]
    [InlineData("a;b")]
    [InlineData("--exact")]
    [InlineData("")]
    public async Task The_helper_rejects_odd_ids(string id)
    {
        var r = await Ops().ExecuteAsync(new WingetInstallOp(id), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task The_helper_rejects_odd_sources_and_versions()
    {
        Assert.Equal(OpStatus.Rejected, (await Ops().ExecuteAsync(new WingetInstallOp("Git.Git", "evil"), true, _ => { }, CancellationToken.None)).Status);
        Assert.Equal(OpStatus.Rejected, (await Ops().ExecuteAsync(new WingetInstallOp("Git.Git", "winget", "1.0 --force"), true, _ => { }, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_valid_install_dry_runs_as_machine_scope_and_dev_mode_allows_nothing_else()
    {
        var dry = await Ops().ExecuteAsync(new WingetInstallOp("Git.Git"), dryRun: true, _ => { }, CancellationToken.None);
        // winget.exe may not exist for the admin account in CI; either way it never ran.
        Assert.True(dry.Status is OpStatus.DryRun or OpStatus.Failed);
        if (dry.Status == OpStatus.DryRun) Assert.Contains("--scope machine", dry.Message);
        var real = await Ops(dev: true).ExecuteAsync(new WingetInstallOp("Git.Git"), dryRun: false, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, real.Status);
    }

    [Fact]
    public void The_op_round_trips_through_the_pipe_format()
    {
        var op = new WingetInstallOp("Git.Git", "winget", "2.46.0");
        Assert.Equal(op, HelperJson.Deserialize<HelperRequest>(HelperJson.Serialize(new HelperRequest(1, op)))!.Op);
    }

    [Fact]
    public void No_applicable_installer_gets_its_own_plain_message()
    {
        var o = WingetErrorMap.Interpret(unchecked((int)0x8A150010), []);
        Assert.Equal(WingetFailure.NoApplicableInstaller, o.Failure);
        Assert.False(o.Ok);
        Assert.Equal(WingetFailure.NoApplicableInstaller, WingetErrorMap.Interpret(1, ["No applicable installer found; see logs for more details."]).Failure);
    }
}
