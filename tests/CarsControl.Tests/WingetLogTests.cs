using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public class WingetLogTests
{
    // The real line from the 2026-10-15 VS Code update that "succeeded" without changing anything.
    private const string VsCodeLine =
        @"2026-10-15 11:31:51.424   Original Setup EXE: C:\Users\admin\AppData\Local\Temp\WinGet\Microsoft.VisualStudioCode.1.142.0\VSCodeSetup-x64-1.141.2.exe";

    [Fact]
    public void An_old_installer_behind_a_new_catalog_version_is_recognised()
    {
        var m = WingetLog.Check(["2026-10-15 11:31:51.424   Setup version: Inno Setup version 6.4.1", VsCodeLine], "1.142.0");
        Assert.NotNull(m);
        Assert.Equal("1.142.0", m!.Offered);
        Assert.Equal("1.141.2", m.Installer);
        Assert.Contains("not on your PC", m.Explanation);
    }

    [Fact]
    public void The_right_installer_is_not_a_mismatch()
    {
        var line = VsCodeLine.Replace("VSCodeSetup-x64-1.141.2.exe", "VSCodeSetup-x64-1.142.0.exe");
        Assert.Null(WingetLog.Check([line], "1.142.0"));
    }

    [Fact]
    public void Msi_logs_give_the_product_version()
    {
        Assert.Equal("8.0.32", WingetLog.InstallerVersion(["MSI (s) (A4:B8) [11:02:01:123]: Property(S): ProductVersion = 8.0.32"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_an_offered_version_there_is_nothing_to_compare(string? offered) => Assert.Null(WingetLog.Check([VsCodeLine], offered));

    [Fact]
    public void A_log_that_says_nothing_about_versions_gives_nothing() => Assert.Null(WingetLog.Check(["Installation process succeeded."], "2.0"));

    [Fact]
    public void A_missing_log_file_is_not_an_error() => Assert.Null(WingetLog.CheckFile(@"C:\nope\missing.log", "1.0"));

    [Fact]
    public void Self_updating_apps_say_how_to_update_them()
    {
        Assert.Contains("Check for Updates", SelfUpdatingApps.HowToUpdate("Microsoft.VisualStudioCode"));
        Assert.Contains("Check for Updates", SelfUpdatingApps.HowToUpdate("microsoft.visualstudiocode"));
        Assert.Null(SelfUpdatingApps.HowToUpdate("Git.Git"));
        Assert.Null(SelfUpdatingApps.HowToUpdate(null));
    }
}
