using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;

namespace CarsControl.Tests;

public class WindowsUpdatePolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resume_removes_all_six_pause_values_and_nothing_else()
    {
        var plan = WindowsUpdatePolicy.Plan("pause-until", "resume", Now);
        Assert.True(plan.Valid);
        Assert.Equal(6, plan.Writes.Count);
        Assert.All(plan.Writes, w =>
        {
            Assert.Null(w.Value);
            Assert.Equal(WindowsUpdatePolicy.UxSettings, w.SubKey);
            Assert.StartsWith("Pause", w.Name);
        });
    }

    [Fact]
    public void Pausing_sets_start_to_now_and_the_end_to_the_chosen_day()
    {
        var plan = WindowsUpdatePolicy.Plan("pause-until", "2026-10-20", Now);
        Assert.True(plan.Valid);
        Assert.Equal(6, plan.Writes.Count);
        Assert.Equal("2026-10-13T12:00:00Z", plan.Writes.Single(w => w.Name == "PauseUpdatesStartTime").Value);
        var end = DateTime.Parse((string)plan.Writes.Single(w => w.Name == "PauseUpdatesExpiryTime").Value!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(new DateTime(2026, 10, 20), end.ToLocalTime().Date);
    }

    [Theory]
    [InlineData("2026-10-01", "already passed")]
    [InlineData("2026-12-25", "at most 35 days")]
    [InlineData("tomorrow", "Pick a date")]
    [InlineData("2026-13-45", "Pick a date")]
    [InlineData("", "Pick a date")]
    public void Bad_pause_dates_are_rejected_with_a_reason(string value, string reason)
    {
        var plan = WindowsUpdatePolicy.Plan("pause-until", value, Now);
        Assert.False(plan.Valid);
        Assert.Contains(reason, plan.Error);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Excluding_drivers_writes_one_policy_value_and_default_removes_it()
    {
        var on = Assert.Single(WindowsUpdatePolicy.Plan("exclude-drivers", "on", Now).Writes);
        Assert.Equal((WindowsUpdatePolicy.PolicyKey, "ExcludeWUDriversInQualityUpdate", 1), (on.SubKey, on.Name, on.Value));
        var off = Assert.Single(WindowsUpdatePolicy.Plan("exclude-drivers", "default", Now).Writes);
        Assert.Null(off.Value);
        Assert.False(WindowsUpdatePolicy.Plan("exclude-drivers", "off", Now).Valid);
    }

    [Theory]
    [InlineData("30", 2)]
    [InlineData("365", 2)]
    [InlineData("0", 2)]
    [InlineData("default", 2)]
    public void Deferring_feature_updates_takes_whole_days_up_to_a_year(string value, int writes)
    {
        var plan = WindowsUpdatePolicy.Plan("defer-feature-days", value, Now);
        Assert.True(plan.Valid);
        Assert.Equal(writes, plan.Writes.Count);
        Assert.All(plan.Writes, w => Assert.Equal(WindowsUpdatePolicy.PolicyKey, w.SubKey));
    }

    [Theory]
    [InlineData("366")]
    [InlineData("-1")]
    [InlineData("3.5")]
    [InlineData("a month")]
    public void Odd_deferral_values_are_rejected(string value) => Assert.False(WindowsUpdatePolicy.Plan("defer-feature-days", value, Now).Valid);

    [Fact]
    public void Deferring_sets_the_flag_and_the_period_and_zero_clears_both()
    {
        var plan = WindowsUpdatePolicy.Plan("defer-feature-days", "90", Now);
        Assert.Equal(1, plan.Writes.Single(w => w.Name == "DeferFeatureUpdates").Value);
        Assert.Equal(90, plan.Writes.Single(w => w.Name == "DeferFeatureUpdatesPeriodInDays").Value);
        Assert.All(WindowsUpdatePolicy.Plan("defer-feature-days", "0", Now).Writes, w => Assert.Null(w.Value));
    }

    [Theory]
    [InlineData("8-23", true)]
    [InlineData("0-18", true)]
    [InlineData("22-4", true)]
    [InlineData("8-8", false)]
    [InlineData("6-1", false)]
    [InlineData("0-19", false)]
    [InlineData("24-3", false)]
    [InlineData("8", false)]
    [InlineData("8-23-1", false)]
    [InlineData("x-y", false)]
    public void Active_hours_must_fit_windows_limits(string value, bool valid) =>
        Assert.Equal(valid, WindowsUpdatePolicy.Plan("active-hours", value, Now).Valid);

    [Fact]
    public void Manual_active_hours_also_turn_smart_hours_off()
    {
        var plan = WindowsUpdatePolicy.Plan("active-hours", "9-22", Now);
        Assert.Equal(9, plan.Writes.Single(w => w.Name == "ActiveHoursStart").Value);
        Assert.Equal(22, plan.Writes.Single(w => w.Name == "ActiveHoursEnd").Value);
        Assert.Equal(2, plan.Writes.Single(w => w.Name == "SmartActiveHoursState").Value);
    }

    [Theory]
    [InlineData("auto-reboot")]
    [InlineData("NoAutoUpdate")]
    [InlineData("")]
    [InlineData("pause-until ")]
    public void Anything_else_is_not_a_setting(string setting) => Assert.False(WindowsUpdatePolicy.Plan(setting, "1", Now).Valid);

    [Fact]
    public void Every_planned_write_stays_under_the_two_allowed_keys()
    {
        var plans = new[]
        {
            WindowsUpdatePolicy.Plan("pause-until", "resume", Now), WindowsUpdatePolicy.Plan("pause-until", "2026-10-20", Now),
            WindowsUpdatePolicy.Plan("exclude-drivers", "on", Now), WindowsUpdatePolicy.Plan("exclude-drivers", "default", Now),
            WindowsUpdatePolicy.Plan("defer-feature-days", "10", Now), WindowsUpdatePolicy.Plan("defer-feature-days", "default", Now),
            WindowsUpdatePolicy.Plan("active-hours", "8-23", Now), WindowsUpdatePolicy.Plan("active-hours", "default", Now),
        };
        foreach (var w in plans.SelectMany(p => p.Writes))
            Assert.Contains(w.SubKey, new[] { WindowsUpdatePolicy.UxSettings, WindowsUpdatePolicy.PolicyKey });
    }
}

public class HealthHelperOpsTests
{
    private static HelperOperations Ops(bool dev = false) => new(new HelperLog(), parentProfile: null, devUnelevated: dev);

    private static Task<OpResult> Run(HelperOp op, bool dryRun = true, bool dev = false) => Ops(dev).ExecuteAsync(op, dryRun, _ => { }, CancellationToken.None);

    [Theory]
    [InlineData("nvdmi.inf")]
    [InlineData("oem12.inf /f")]
    [InlineData(@"..\oem12.inf")]
    [InlineData(@"C:\Windows\INF\oem12.inf")]
    [InlineData("oem12.inf & calc")]
    [InlineData("")]
    public async Task Only_oem_names_can_be_deleted_from_the_driver_store(string name)
    {
        var r = await Run(new DriverDeleteOp(name));
        Assert.Equal(OpStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task A_valid_driver_name_dry_runs_to_the_exact_pnputil_command()
    {
        var r = await Run(new DriverDeleteOp("OEM12.inf"));
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains("pnputil.exe", r.Message);
        Assert.Contains("/delete-driver oem12.inf", r.Message);
        Assert.DoesNotContain("/force", r.Message);
        Assert.DoesNotContain("/uninstall", r.Message);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("12345678-1234-1234-1234-123456789abc; calc")]
    public async Task Hiding_an_update_needs_a_real_update_id(string id)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new HideUpdateOp(id, true))).Status);
    }

    [Fact]
    public async Task A_valid_update_id_can_be_dry_run_hidden()
    {
        var r = await Run(new HideUpdateOp("12345678-1234-1234-1234-123456789abc", true));
        Assert.Equal(OpStatus.DryRun, r.Status);
    }

    [Theory]
    [InlineData("auto-reboot", "1")]
    [InlineData("pause-until", "2020-01-01")]
    [InlineData("active-hours", "0-23")]
    [InlineData("defer-feature-days", "9999")]
    public async Task Windows_update_settings_outside_the_list_are_rejected(string setting, string value)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new WindowsUpdatePolicyOp(setting, value))).Status);
    }

    [Fact]
    public async Task A_valid_setting_dry_runs_and_names_the_registry_value()
    {
        var r = await Run(new WindowsUpdatePolicyOp("exclude-drivers", "on"));
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains("ExcludeWUDriversInQualityUpdate", r.Message);
    }

    [Fact]
    public async Task Pawnio_dry_run_names_the_pinned_release_and_hash()
    {
        var r = await Run(new InstallPawnIoOp());
        Assert.Equal(OpStatus.DryRun, r.Status);
        Assert.Contains(PawnIo.DownloadUrl, r.Message);
        Assert.Contains(PawnIo.Sha256, r.Message);
        Assert.StartsWith("https://github.com/namazso/PawnIO.Setup/releases/download/", PawnIo.DownloadUrl);
        Assert.Matches("^[0-9a-f]{64}$", PawnIo.Sha256);
    }

    [Fact]
    public async Task The_other_health_ops_dry_run_without_reading_or_changing_anything()
    {
        foreach (var op in new HelperOp[] { new DriveHealthOp(), new PowerRequestsOp(), new BootPerformanceOp(), new EnableSystemRestoreOp(), new SensorsReadOp() })
        {
            var r = await Run(op);
            Assert.Equal(OpStatus.DryRun, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
        }
    }

    [Fact]
    public async Task Enabling_system_protection_is_for_c_only_and_takes_no_input()
    {
        var r = await Run(new EnableSystemRestoreOp());
        Assert.Contains("powershell.exe", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Enable-ComputerRestore -Drive 'C:\\'", r.Message);
    }

    [Fact]
    public async Task Health_ops_in_dev_mode_are_dry_runs_only()
    {
        foreach (var op in new HelperOp[] { new DriveHealthOp(), new DriverDeleteOp("oem1.inf"), new WindowsUpdatePolicyOp("exclude-drivers", "on"), new InstallPawnIoOp(), new SensorsReadOp() })
            Assert.Equal(OpStatus.Rejected, (await Run(op, dryRun: false, dev: true)).Status);
    }

    [Fact]
    public void Health_ops_round_trip_through_the_pipe_format()
    {
        HelperOp[] ops =
        [
            new DriveHealthOp(), new PowerRequestsOp(), new BootPerformanceOp(14), new DriverDeleteOp("oem7.inf"), new WindowsUpdatePolicyOp("active-hours", "8-23"),
            new HideUpdateOp("12345678-1234-1234-1234-123456789abc", false), new EnableSystemRestoreOp(), new SensorsReadOp(), new InstallPawnIoOp(),
        ];
        foreach (var op in ops)
        {
            var json = HelperJson.Serialize(new HelperRequest(1, op));
            Assert.Equal(op, HelperJson.Deserialize<HelperRequest>(json)!.Op);
        }
    }

    [Fact]
    public void Boot_reports_and_drive_readings_survive_the_pipe()
    {
        var report = new BootReport(
            [new BootRecord(new DateTime(2026, 10, 12, 8, 0, 0), TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(2))],
            [new ShutdownRecord(new DateTime(2026, 10, 11, 23, 0, 0), TimeSpan.FromSeconds(12.5))],
            [new SlowItem("App", "Salad", TimeSpan.FromSeconds(35))]);
        var back = HelperJson.Deserialize<BootReport>(HelperJson.Serialize(report))!;
        Assert.Equal(report.Boots[0], back.Boots[0]);
        Assert.Equal(report.SlowItems[0], back.SlowItems[0]);

        var drives = new[] { new DriveReading("WD Black", DriveKind.Nvme, 1_000_000_000_000, "Healthy", ["C", "D"], 41, 3, 4120, 0, 0, 70) };
        var driveBack = HelperJson.Deserialize<List<DriveReading>>(HelperJson.Serialize(drives))!;
        Assert.Equal(41, driveBack[0].TempC);
        Assert.Equal(["C", "D"], driveBack[0].Letters);
        Assert.Equal(DriveKind.Nvme, driveBack[0].Kind);

        var sensors = new[] { new SensorReading("AMD Ryzen 9", "Cpu", "Core (Tctl/Tdie)", "Temperature", 52.5, 31, 74.25) };
        Assert.Equal(sensors[0], HelperJson.Deserialize<List<SensorReading>>(HelperJson.Serialize(sensors))![0]);
    }
}

public class CrashDumpCopyTests
{
    private static Task<OpResult> Run(HelperOp op) =>
        new HelperOperations(new HelperLog(), parentProfile: null).ExecuteAsync(op, dryRun: true, _ => { }, CancellationToken.None);

    [Theory]
    [InlineData("091126-13359-01.dmp", true)]
    [InlineData("101526-9-02.dmp", true)]
    [InlineData("MEMORY.DMP", false)]
    [InlineData(@"..\..\System32\config\SAM", false)]
    [InlineData(@"C:\Windows\Minidump\091126-13359-01.dmp", false)]
    [InlineData("091126-13359-01.dmp:stream", false)]
    [InlineData("091126-13359-01.dmp.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_bare_minidump_names_are_accepted(string? name, bool ok) => Assert.Equal(ok, DumpFiles.IsValidName(name));

    [Theory]
    [InlineData(@"..\091126-13359-01.dmp")]
    [InlineData("SAM")]
    [InlineData(@"C:\Users\alex\secret.dmp")]
    public async Task The_helper_refuses_anything_but_a_minidump_name(string name)
    {
        Assert.Equal(OpStatus.Rejected, (await Run(new CopyCrashDumpOp(name))).Status);
    }

    [Fact]
    public async Task A_dump_that_isnt_there_is_not_found_rather_than_copied()
    {
        var r = await Run(new CopyCrashDumpOp("010100-1-01.dmp"));
        Assert.Equal(OpStatus.NotFound, r.Status);
    }

    [Fact]
    public void The_debuggers_reason_is_shown_when_it_cant_open_a_dump()
    {
        string[] denied = ["Microsoft (R) Windows Debugger Version 10.0", @"Could not open dump file [C:\Windows\Minidump\091126-13359-01.dmp], Win32 error 0n5", "    \"Access is denied.\""];
        Assert.Equal("Windows didn't let the debugger open the dump (access denied).", DumpAnalyzer.ProblemIn(denied));
        Assert.StartsWith("ERROR:", DumpAnalyzer.ProblemIn(["ERROR: Symbol file could not be found."]));
        Assert.Null(DumpAnalyzer.ProblemIn(["BUGCHECK_CODE:  3b", "MODULE_NAME: nvlddmkm"]));
    }

    [Fact]
    public void The_copy_operation_round_trips_through_the_pipe_protocol()
    {
        var json = HelperJson.Serialize(new HelperRequest(7, new CopyCrashDumpOp("091126-13359-01.dmp")));
        Assert.Contains("crashdump-copy", json);
        Assert.Equal("091126-13359-01.dmp", Assert.IsType<CopyCrashDumpOp>(HelperJson.Deserialize<HelperRequest>(json)!.Op).FileName);
    }
}
