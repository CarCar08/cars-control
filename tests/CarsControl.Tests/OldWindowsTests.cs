using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Storage;

namespace CarsControl.Tests;

public class OldWindowsRulesTests
{
    private static readonly HashSet<string> Present = new(StringComparer.OrdinalIgnoreCase) { @"D:\Windows\System32\config\SYSTEM", @"D:\Windows", @"D:\Users", @"D:\SteamLibrary" };

    private static string? Check(string folder, string running = @"C:\", HashSet<string>? exists = null, Func<string, bool>? link = null) =>
        OldWindowsRules.Validate(folder, running, p => (exists ?? Present).Contains(p), link ?? (_ => false));

    [Theory]
    [InlineData(@"D:\Windows")]
    [InlineData(@"D:\Users")]
    [InlineData(@"d:\windows\")]
    public void The_top_level_folders_of_an_old_installation_are_allowed(string folder) => Assert.Null(Check(folder));

    [Theory]
    [InlineData(@"D:\SteamLibrary", "isn't one of the folders")]
    [InlineData(@"D:\XboxGames", "isn't one of the folders")]
    [InlineData(@"D:\WindowsApps", "isn't one of the folders")]
    [InlineData(@"D:\Windows\System32", "top-level")]
    [InlineData(@"D:\Users\alex", "top-level")]
    [InlineData(@"D:\Windows\..\SteamLibrary", "isn't one of the folders")]
    [InlineData(@"D:\", "top-level")]
    [InlineData(@"relative\Windows", "full folder path")]
    [InlineData("", "full folder path")]
    public void Anything_else_is_refused_with_a_reason(string folder, string reason) => Assert.Contains(reason, Check(folder));

    [Fact]
    public void The_running_windows_and_boot_drives_are_never_touched()
    {
        Assert.Contains("running", Check(@"D:\Windows", running: @"D:\"));
        var withBoot = new HashSet<string>(Present, StringComparer.OrdinalIgnoreCase) { @"D:\bootmgr" };
        Assert.Contains("boot manager", Check(@"D:\Windows", exists: withBoot));
        var efi = new HashSet<string>(Present, StringComparer.OrdinalIgnoreCase) { @"D:\EFI\Microsoft\Boot\bootmgfw.efi" };
        Assert.Contains("boot manager", Check(@"D:\Windows", exists: efi));
    }

    [Fact]
    public void A_drive_without_an_old_registry_hive_is_not_an_old_windows()
    {
        var noHive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Windows", @"D:\Users" };
        Assert.Contains("old Windows installation", Check(@"D:\Windows", exists: noHive));
    }

    [Fact]
    public void A_link_is_refused()
    {
        Assert.Contains("link", Check(@"D:\Windows", link: p => p.EndsWith("Windows")));
    }

    [Fact]
    public void Programs_registered_inside_the_folder_are_reported()
    {
        (string, string?)[] registered =
        [
            ("Some Tool", @"D:\Program Files\Some Tool\"),
            ("Icon only", "\"D:\\Program Files\\Other\\o.exe\",0"),
            ("Uninstaller", "\"D:\\Program Files\\Third\\unins000.exe\" /SILENT"),
            ("Elsewhere", @"C:\Program Files\Elsewhere"),
            ("Sibling", @"D:\Program Files (x86)\Sibling\s.exe"),
            ("No path", null),
        ];
        var users = OldWindowsRules.StillUsedBy(@"D:\Program Files", registered);
        Assert.Equal(["Some Tool", "Icon only", "Uninstaller"], users);
        Assert.Empty(OldWindowsRules.StillUsedBy(@"D:\Users", registered));
    }

    [Theory]
    [InlineData("\"D:\\x\\y.exe\",0", @"D:\x\y.exe")]
    [InlineData(@"D:\x\y.exe,-1", @"D:\x\y.exe")]
    [InlineData("\"D:\\x\\y.exe\" /S", @"D:\x\y.exe")]
    [InlineData(@"D:\x\", @"D:\x\")]
    public void Registry_values_are_reduced_to_a_path(string value, string expected) => Assert.Equal(expected, OldWindowsRules.Clean(value));
}

public class OldWindowsHelperTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Users")]
    [InlineData(@"D:\SteamLibrary")]
    [InlineData(@"D:\XboxGames")]
    [InlineData(@"D:\Windows\System32")]
    [InlineData(@"C:\Windows\..\Windows")]
    [InlineData("Windows")]
    [InlineData("")]
    public async Task The_helper_refuses_anything_but_an_old_installations_top_level_folders(string folder)
    {
        var r = await Ops().ExecuteAsync(new RemoveOldWindowsOp(folder), dryRun: true, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task In_dev_mode_a_real_run_is_never_allowed()
    {
        var r = await Ops(dev: true).ExecuteAsync(new RemoveOldWindowsOp(@"D:\Windows"), dryRun: false, _ => { }, CancellationToken.None);
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public void The_op_round_trips_through_the_pipe_format()
    {
        var op = new RemoveOldWindowsOp(@"D:\Windows");
        Assert.Equal(op, HelperJson.Deserialize<HelperRequest>(HelperJson.Serialize(new HelperRequest(1, op)))!.Op);
    }
}

public class OldWindowsLinkTests
{
    [Fact]
    public void Links_on_the_running_system_that_lead_into_the_folder_are_reported()
    {
        (string, string?)[] links =
        [
            (@"C:\Users\alex\.claude", @"D:\Users\alex\.claude"),
            (@"C:\Users\alex\Games", @"D:\SteamLibrary"),
            (@"C:\Users\alex\Other", @"d:\users\alex\Stuff\"),
            (@"C:\broken", null),
        ];
        Assert.Equal([@"C:\Users\alex\.claude", @"C:\Users\alex\Other"], OldWindowsRules.LinkedFrom(@"D:\Users", links));
        Assert.Empty(OldWindowsRules.LinkedFrom(@"D:\Windows", links));
        Assert.Empty(OldWindowsRules.LinkedFrom(@"D:\Use", links)); // a folder whose name merely starts the same
    }
}
