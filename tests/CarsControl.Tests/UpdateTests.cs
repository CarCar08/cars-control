using CarsControl.Contracts;
using CarsControl.Core.Data;
using CarsControl.Core.Elevation;
using CarsControl.Core.Util;
using CarsControl.Core.Winget;

namespace CarsControl.Tests;

public class WingetErrorMapTests
{
    // Exit codes as logged by UniGetUI on this PC (signed int32).
    [Theory]
    [InlineData(-1978335212, WingetFailure.NotFoundForAccount)] // Oh My Posh / LocalSend run elevated
    [InlineData(-1978335189, WingetFailure.NotApplicable)]      // phantom update
    [InlineData(-1978335215, WingetFailure.HashMismatch)]       // Ubisoft Connect
    [InlineData(-1978335217, WingetFailure.SourceBroken)]       // admin source missing (Aug 11)
    [InlineData(-2147009255, WingetFailure.NotFoundForAccount)] // WSL 0x80073D19 "a user was logged off" (elevated as admin)
    [InlineData(unchecked((int)0x80073CF9), WingetFailure.MsixFailed)]
    [InlineData(1073807364, WingetFailure.Terminated)]          // PowerToys 0x40010004
    [InlineData(0, WingetFailure.None)]
    public void Maps_codes_seen_on_this_pc(int code, WingetFailure expected) =>
        Assert.Equal(expected, WingetErrorMap.Interpret(code, []).Failure);

    [Fact]
    public void Reads_installer_exit_code_from_output() // GameInput: 0x8A150049 with "Installer failed with exit code: 1603"
    {
        var o = WingetErrorMap.Interpret(-1978335159, ["Starting package install...", "Installer failed with exit code: 1603"]);
        Assert.Equal(WingetFailure.InstallerFailed, o.Failure);
        Assert.Equal(1603, o.InstallerExitCode);
    }

    [Fact]
    public void Reboot_codes_count_as_success() => Assert.True(WingetErrorMap.Interpret(-1, ["Installer failed with exit code: 3010"]).Ok);
}

public class ValidationTests
{
    [Theory]
    [InlineData("Google.Chrome", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("9NBLGGH4NNS1", true)]
    [InlineData("Foo.Bar & calc.exe", false)]
    [InlineData("--override", false)]
    [InlineData("a\"b", false)]
    [InlineData("", false)]
    public void Winget_ids(string id, bool ok) => Assert.Equal(ok, WingetCli.IsValidId(id));

    [Theory]
    [InlineData("requests", true)]
    [InlineData("@angular/cli", true)]
    [InlineData("--index-url=http://evil", false)]
    [InlineData("x;rm", false)]
    [InlineData("a'b", false)]
    public void Package_names(string name, bool ok) => Assert.Equal(ok, Tools.IsSafePackageName(name));

    [Theory]
    [InlineData("CarsControl.0123456789abcdef0123456789abcdef", true)]
    [InlineData("CarsControl.xyz", false)]
    [InlineData("Other.0123456789abcdef0123456789abcdef", false)]
    public void Pipe_names(string name, bool ok) => Assert.Equal(ok, HelperJson.IsValidPipeName(name));

    [Fact]
    public void Helper_args_require_valid_pipe_and_parent()
    {
        Assert.Null(HelperHost.ParseArgs(["--pipe", "evil", "--parent", "5"]));
        Assert.Null(HelperHost.ParseArgs(["--pipe", "CarsControl.0123456789abcdef0123456789abcdef"]));
        var a = HelperHost.ParseArgs(["--pipe", "CarsControl.0123456789abcdef0123456789abcdef", "--parent", "42", "--idle-minutes", "9999"])!;
        Assert.Equal(42, a.ParentPid);
        Assert.Equal(240, a.IdleMinutes); // clamped
    }
}

public class HelperAllowlistTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    [Fact]
    public async Task Rejects_injection_in_every_identifier()
    {
        var ops = Ops();
        HelperOp[] bad =
        [
            new WingetUpgradeOp("Foo & calc"),
            new WingetUpgradeOp("Foo.Bar", "1 --override x"),
            new WingetUninstallOp("a|b"),
            new PipUpgradeOp("--index-url=x"),
            new CreateRestorePointOp("x'; Remove-Item C:\\"),
            new WindowsUpdateInstallOp(["nope"]),
        ];
        foreach (var op in bad)
        {
            var r = await ops.ExecuteAsync(op, dryRun: true, _ => { }, default);
            Assert.True(r.Status == OpStatus.Rejected, $"{op} should be rejected but was {r.Status}");
        }
    }

    [Fact]
    public async Task Dev_mode_only_allows_dry_runs()
    {
        var r = await Ops(dev: true).ExecuteAsync(new WslUpdateOp(), dryRun: false, _ => { }, default);
        Assert.Equal(OpStatus.Rejected, r.Status);
        var dry = await Ops(dev: true).ExecuteAsync(new WslUpdateOp(), dryRun: true, _ => { }, default);
        Assert.Equal(OpStatus.DryRun, dry.Status);
        Assert.Contains("wsl.exe", dry.Message);
    }
}

public class VersionCompareTests
{
    [Theory]
    [InlineData("1.0.6.0", "1.0.6", false)] // the PowerShell false positive
    [InlineData("1.29.380", "1.29.280", true)]
    [InlineData("10.0", "9.9", true)]
    [InlineData("v2.1", "2.0.9", true)]
    [InlineData(null, "1", false)]
    public void Compares(string? candidate, string current, bool newer) => Assert.Equal(newer, VersionCompare.IsNewer(candidate, current));
}

public class DbTests
{
    private static CarsDb TempDb() => new(Path.Combine(Path.GetTempPath(), $"cars-test-{Guid.NewGuid():N}.db"));

    [Fact]
    public void Skip_version_vs_permanent_ignore()
    {
        var db = TempDb();
        db.SetIgnore("winget:A", "2.0");
        db.SetIgnore("winget:B", null);
        var rules = db.IgnoreRules();
        Assert.True(CarsDb.IsIgnored(rules, "winget:A", "2.0"));
        Assert.False(CarsDb.IsIgnored(rules, "winget:A", "2.1")); // a newer release shows up again
        Assert.True(CarsDb.IsIgnored(rules, "winget:B", "99"));
        db.RemoveIgnore("winget:B");
        Assert.False(CarsDb.IsIgnored(db.IgnoreRules(), "winget:B", "99"));
    }

    [Fact]
    public void Imports_unigetui_format()
    {
        var dir = Directory.CreateTempSubdirectory("unigetui").FullName;
        File.WriteAllText(Path.Combine(dir, "IgnoredPackageUpdates.json"), """
            { "winget\\Ubisoft.Connect": "174.0.0.13368", "powershell7\\Microsoft.WinGet.Client": "1.29.380", "winget\\Foo.Bar": "*" }
            """);
        File.WriteAllText(Path.Combine(dir, "OperationHistory.json"), """
            [ { "Kind": "update-package", "PackageId": "Telegram.TelegramDesktop", "PackageName": "Telegram Desktop", "ManagerName": "winget",
                "VersionBefore": "7.3", "VersionAfter": "7.3.1", "Status": "succeeded", "TimestampUtc": "2026-10-12T02:29:08Z",
                "ExitCode": 0, "RanElevated": false, "FailureSummary": "", "Output": [ { "Text": "Successfully installed", "Type": "Information" } ] } ]
            """);
        var db = TempDb();
        var (rules, history) = UniGetUiImporter.Import(db, dir);

        Assert.Equal(3, rules);
        Assert.Equal(1, history);
        var r = db.IgnoreRules();
        Assert.Equal("174.0.0.13368", r["winget:Ubisoft.Connect"].Version);
        Assert.True(r["winget:Foo.Bar"].IsPermanent);
        Assert.True(r.ContainsKey("psmodule:Microsoft.WinGet.Client"));
        var h = Assert.Single(db.ListHistory());
        Assert.Equal("Succeeded", h.Status);
        Assert.True(h.Imported);
    }
}

public class ProcessRunnerTests
{
    [Theory]
    [InlineData("  ██████▒▒▒▒  ", "")]
    [InlineData("███▒▒ 12.3 MB / 40.1 MB", "12.3 MB / 40.1 MB")]
    [InlineData("   -\\|/ ", "")]
    [InlineData("Successfully installed", "Successfully installed")]
    public void Cleans_progress_noise(string raw, string expected) => Assert.Equal(expected, ProcessRunner.Clean(raw));
}
