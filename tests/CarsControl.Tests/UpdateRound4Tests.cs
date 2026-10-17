using CarsControl.Core.Updates;
using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public class PipRoutingTests
{
    // Real `pip list --outdated --format=json -v` output from this PC (2026-10-12), plus one shared package.
    private const string Outdated = """
        [{"name": "pillow_heif", "version": "1.7.0", "location": "C:\\Users\\alex\\AppData\\Roaming\\Python\\Python314\\site-packages", "installer": "pip", "latest_version": "1.8.1", "latest_filetype": "wheel"},
         {"name": "requests", "version": "2.31.0", "location": "C:\\Program Files\\Python314\\Lib\\site-packages", "installer": "pip", "latest_version": "2.32.3", "latest_filetype": "wheel"}]
        """;

    [Fact]
    public void Your_own_packages_update_as_you_and_shared_ones_need_admin() // the "2 updates that never go away" bug
    {
        var items = UpdateSources.ParsePipOutdated(Outdated, @"C:\Users\alex");
        var mine = items.Single(i => i.Title == "pillow_heif");
        var shared = items.Single(i => i.Title == "requests");
        Assert.False(mine.NeedsAdmin);
        Assert.True(shared.NeedsAdmin);
        Assert.Equal(@"C:\Users\alex\AppData\Roaming\Python\Python314\site-packages", mine.Location);
        Assert.All(items, i => Assert.False(i.SelectedByDefault));
    }

    [Fact]
    public void Output_without_locations_counts_as_shared() =>
        Assert.True(UpdateSources.ParsePipOutdated("""[{"name":"x","version":"1","latest_version":"2"}]""", @"C:\Users\alex").Single().NeedsAdmin);

    [Fact]
    public void Upgrades_your_copy_with_user_flag()
    {
        Assert.Contains("--user", PipInstall.UpgradeArgs("pypdf", user: true));
        Assert.DoesNotContain("--user", PipInstall.UpgradeArgs("pypdf", user: false));
        Assert.Equal("pypdf", PipInstall.UpgradeArgs("pypdf", user: true)[^1]);
    }

    [Fact]
    public void Reads_the_version_pip_show_reports() =>
        Assert.Equal("6.20.1", PipInstall.ParseShowVersion(["Name: pypdf", "Version: 6.20.1", "Summary: A pure-python PDF library"]));

    [Theory]
    [InlineData(@"C:\Users\alex\AppData\Roaming\Python\Python314\site-packages", true)]
    [InlineData(@"C:\Users\admin\AppData\Roaming\Python\Python314\site-packages", false)] // another user's profile
    [InlineData(@"C:\Program Files\Python314\Lib\site-packages", false)]
    [InlineData(null, false)]
    public void User_location(string? location, bool mine) => Assert.Equal(mine, PipInstall.IsUserLocation(location, @"C:\Users\alex"));
}

public class UpToDateTests
{
    [Fact]
    public void Wsl_comes_from_its_own_source_not_windows_update() // it ran twice on 2026-10-12 and the second "failed"
    {
        Assert.True(UpdateSources.CoveredByOtherSource("Windows Subsystem for Linux 3.0.3.0 (x64)"));
        Assert.False(UpdateSources.CoveredByOtherSource("2026-10 Cumulative Update for Windows 11"));
    }

    [Fact]
    public void Winget_with_nothing_newer_is_up_to_date_not_a_failure()
    {
        var r = WingetErrorMap.Interpret(-1978335189, ["No available upgrade found.", "No newer package versions are available from the configured sources."]);
        Assert.True(r.Ok);
        Assert.Equal("Already up to date", r.Message);
    }

    [Fact]
    public void A_phantom_update_is_still_reported() =>
        Assert.Equal(WingetFailure.NotApplicable, WingetErrorMap.Interpret(-1978335189, ["No applicable upgrade found."]).Failure);
}

public class WingetShowTests
{
    [Fact]
    public void Picks_the_release_notes_then_the_homepage()
    {
        string[] full =
        [
            "Found Git [Git.Git]", "Version: 2.45.0", "Publisher: The Git Development Community", "Publisher Url: https://gitforwindows.org",
            "Homepage: https://gitforwindows.org/home", "License: GPL-2.0", "Release Notes Url: https://github.com/git-for-windows/git/releases/tag/v2.45.0",
        ];
        Assert.Equal("https://github.com/git-for-windows/git/releases/tag/v2.45.0", CarsControl.Core.Winget.WingetShow.Parse(full).BestUrl);
        Assert.Equal("https://gitforwindows.org/home", CarsControl.Core.Winget.WingetShow.Parse(full.Where(l => !l.StartsWith("Release")).ToArray()).BestUrl);
        Assert.Null(CarsControl.Core.Winget.WingetShow.Parse(["Found X [X.X]", "Version: 1"]).BestUrl);
    }

    [Fact]
    public void Only_web_links_are_ever_opened()
    {
        var info = CarsControl.Core.Winget.WingetShow.Parse(["Release Notes Url: file:///C:/Windows/System32/calc.exe", "Homepage: javascript:alert(1)", "Publisher Url: ftp://example.com"]);
        Assert.Null(info.BestUrl);
    }
}
