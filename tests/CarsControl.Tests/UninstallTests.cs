using CarsControl.Core.Models;
using CarsControl.Core.Uninstall;

namespace CarsControl.Tests;

public class UninstallCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\App\\uninst.exe /x", "C:\\Program Files\\App\\uninst.exe", "/x")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\Users\\alex\\AppData\\Local\\Discord\\Update.exe\" --uninstall", "C:\\Users\\alex\\AppData\\Local\\Discord\\Update.exe", "--uninstall")]
    public void Splits_exe_and_args(string raw, string exe, string args)
    {
        var c = UninstallCommand.Parse(raw)!;
        Assert.Equal(exe, c.Exe);
        Assert.Equal(args, c.Args);
    }

    [Fact]
    public void Detects_families_by_name()
    {
        Assert.Equal(InstallerKind.Inno, UninstallCommand.Detect(new CommandLine(@"C:\X\unins001.exe", "")));
        Assert.Equal(InstallerKind.Squirrel, UninstallCommand.Detect(new CommandLine(@"C:\X\Update.exe", "--uninstall")));
        Assert.Equal(InstallerKind.Msi, UninstallCommand.Detect(new CommandLine("msiexec.exe", "/x")));
    }

    [Fact]
    public void Adds_silent_switches_once()
    {
        var inno = UninstallCommand.MakeSilent(new CommandLine(@"C:\X\unins000.exe", ""), InstallerKind.Inno)!;
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", inno.Args);
        var nsis = UninstallCommand.MakeSilent(new CommandLine(@"C:\X\uninst.exe", "/S"), InstallerKind.Nsis)!;
        Assert.Equal("/S", nsis.Args);
        Assert.Null(UninstallCommand.MakeSilent(new CommandLine("x.exe", ""), InstallerKind.Unknown));
    }
}

public class UninstallPlannerTests
{
    private static AppEntry Arp(string key, ArpHive hive = ArpHive.LocalMachine64, string? uninstall = null, string? quiet = null, string? msi = null, string? winget = null) => new()
    {
        Key = key, Name = key, Source = AppSource.Arp, Arp = new ArpLocation(hive, key),
        Scope = hive == ArpHive.CurrentUser ? InstallScope.User : InstallScope.Machine,
        UninstallString = uninstall, QuietUninstallString = quiet, MsiProductCode = msi, WingetId = winget,
    };

    [Fact]
    public void Msi_beats_everything_and_runs_elevated_for_machine_apps()
    {
        var p = UninstallPlanner.Plan(Arp("{A}", msi: "{11111111-2222-3333-4444-555555555555}", quiet: "x.exe /q", winget: "A.B"));
        Assert.Equal(UninstallMethod.Msi, p.Method);
        Assert.True(p.Elevated);
        Assert.Contains("/qn", p.Command!.Args);
    }

    [Fact]
    public void Per_user_apps_never_elevate() =>
        Assert.False(UninstallPlanner.Plan(Arp("Discord", ArpHive.CurrentUser, quiet: "\"C:\\U\\Update.exe\" --uninstall -s")).Elevated);

    [Fact]
    public void Inno_quiet_string_gets_message_boxes_suppressed()
    {
        var p = UninstallPlanner.Plan(Arp("T", ArpHive.CurrentUser, quiet: "\"C:\\T\\unins000.exe\" /SILENT"));
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART", p.Command!.Args);
    }

    [Fact]
    public void Steam_games_are_handed_to_steam()
    {
        var p = UninstallPlanner.Plan(Arp("Steam App 284160", uninstall: "\"C:\\Steam\\steam.exe\" steam://uninstall/284160"));
        Assert.Equal(UninstallMethod.SteamHandoff, p.Method);
        Assert.Equal("284160", p.SteamAppId);
    }

    [Fact]
    public void Unknown_uninstaller_is_interactive() =>
        Assert.Equal(UninstallMethod.Interactive, UninstallPlanner.Plan(Arp("Odd", uninstall: "\"C:\\nope\\setup.exe\" -remove")).Method);
}

public class LeftoverSafetyTests
{
    private static AppEntry App(string name, string? location = null, string? icon = null, string key = "k") => new()
    {
        Key = key, Name = name, InstallLocation = location, DisplayIcon = icon, Source = AppSource.Arp,
        Arp = new ArpLocation(ArpHive.LocalMachine64, key),
    };

    [Theory]
    [InlineData("LocalSend version 1.17.0", "localsend")]
    [InlineData("Notepad++ (64-bit x64)", "notepad++")]
    [InlineData("WizTree v4.33", "wiztree")]
    [InlineData("7-Zip 24.08 (x64)", "7zip")]
    public void Normalizes_names(string raw, string expected) => Assert.Equal(expected, LeftoverScanner.NormalizeName(raw));

    [Fact]
    public void Never_offers_system_or_shared_roots()
    {
        var s = new LeftoverScanner([]);
        Assert.False(s.IsSafeFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(s.IsSafeFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(s.IsSafeFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Teams")));
        Assert.False(s.IsSafeFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    }

    [Fact]
    public void Protects_folders_holding_another_apps_files_even_without_install_location()
    {
        // Not under %TEMP%: "\Temp\" paths are refused outright, which would make this test vacuous.
        var dir = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"CarsControlTest-{Guid.NewGuid():N}")).FullName;
        try
        {
            var other = App("Real Program", icon: Path.Combine(dir, "app.exe")); // like Notepad++: no InstallLocation, only an icon
            Assert.False(new LeftoverScanner([other]).IsSafeFolder(dir));
            Assert.True(new LeftoverScanner([]).IsSafeFolder(dir));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Same_named_app_disables_name_matching()
    {
        // The MSIX shell extension "Notepad++" must not claim the Notepad++ program's folders.
        var removed = new AppEntry { Key = "msix:npp", Name = "Notepad++", Source = AppSource.Msix, Scope = InstallScope.User };
        var program = App("Notepad++ (64-bit x64)", key: "Notepad++");
        var found = new LeftoverScanner([program]).Scan(removed, LeftoverScope.User);
        Assert.DoesNotContain(found, f => f.Confidence != Confidence.High);
    }
}
