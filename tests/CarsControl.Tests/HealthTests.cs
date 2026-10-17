using CarsControl.Core.Health;

namespace CarsControl.Tests;

internal static class EventXml
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/win/2004/08/events/event'";

    public static string Event(string provider, int id, string timeUtc, params (string Name, string Value)[] data) =>
        $"<Event {Ns}><System><Provider Name='{provider}' Guid='{{00000000-0000-0000-0000-000000000000}}'/><EventID>{id}</EventID><TimeCreated SystemTime='{timeUtc}'/></System>" +
        $"<EventData>{string.Concat(data.Select(d => $"<Data Name='{d.Name}'>{d.Value}</Data>"))}</EventData></Event>";
}

public class WinEventsTests
{
    [Fact]
    public void Parses_back_to_back_events_without_a_root_oldest_first()
    {
        var xml = EventXml.Event("A", 2, "2026-09-07T02:19:36.9974696Z", ("X", "2")) + EventXml.Event("B", 1, "2026-09-07T01:00:00.0000000Z", ("Y", "1"));
        var events = WinEvents.Parse(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("B", events[0].Provider);
        Assert.Equal(1, events[0].GetLong("Y"));
        Assert.Equal("2", events[1].Get("X"));
        Assert.Equal(DateTimeKind.Utc, events[1].TimeUtc.Kind);
    }

    [Fact]
    public void Empty_or_odd_output_is_no_events()
    {
        Assert.Empty(WinEvents.Parse(""));
        Assert.Empty(WinEvents.Parse("   \r\n"));
        Assert.Empty(WinEvents.Parse(EventXml.Event("A", 1, "not a time")));
    }

    [Fact]
    public void The_query_names_each_provider_and_id_and_the_start_time()
    {
        var q = WinEvents.Query(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), ("Microsoft-Windows-Kernel-Power", 41), ("Microsoft-Windows-Kernel-General", 1));
        Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", q);
        Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=1", q);
        Assert.Contains("2026-10-01T12:00:00.000Z", q);
    }
}

public class SleepHealthTests
{
    // Shapes copied from this PC's own log (2026-09-06/07): a hibernate whose resume spent 26 s in firmware,
    // the power-button sleep event that started it, a driver that blocked it, and a 45 s clock stall.
    private static readonly string Xml =
        EventXml.Event("Microsoft-Windows-Kernel-Power", 42, "2026-09-07T02:18:43.6350910Z", ("TargetState", "5"), ("EffectiveState", "5"), ("Reason", "0")) +
        EventXml.Event("Microsoft-Windows-Kernel-Power", 40, "2026-09-07T02:18:43.5245021Z", ("DriverName", @"\Driver\vpcivsp"), ("InstanceName", @"ROOT\VPCIVSP\0000")) +
        EventXml.Event("Microsoft-Windows-Power-Troubleshooter", 1, "2026-09-07T02:19:36.9974696Z",
            ("SleepTime", "2026-09-07T02:18:42.3192472Z"), ("WakeTime", "2026-09-07T02:19:36.2571684Z"), ("BiosInitDuration", "26496"),
            ("DriverInitDuration", "1539"), ("HiberReadDuration", "4880"), ("TargetState", "5"), ("EffectiveState", "5"), ("WakeSourceType", "0"), ("WakeSourceText", "")) +
        EventXml.Event("Microsoft-Windows-Kernel-General", 1, "2026-09-06T21:19:34.0000000Z", ("TimeDeltaInMs", "45222"), ("Reason", "2")) +
        EventXml.Event("Microsoft-Windows-Kernel-General", 1, "2026-10-03T22:30:03.0000000Z", ("TimeDeltaInMs", "-1326"), ("Reason", "1")) +
        EventXml.Event("Microsoft-Windows-Kernel-General", 1, "2026-10-03T22:31:03.0000000Z", ("TimeDeltaInMs", "800"), ("Reason", "2"));

    [Fact]
    public void Builds_a_resume_with_its_reason_and_what_took_long()
    {
        var report = SleepHealth.Build(WinEvents.Parse(Xml));
        var c = Assert.Single(report.Cycles);
        Assert.Equal("Hibernate", c.State);
        Assert.Equal("the power button or lid", c.Why);
        Assert.Equal(26.496, c.FirmwareInit.TotalSeconds, 3);
        Assert.Equal(26.496 + 1.539 + 4.88, c.ResumeTime.TotalSeconds, 3);
        Assert.True(c.IsSlow);
        Assert.Null(c.WokenBy);
        Assert.Equal(1, report.SlowResumes);
        Assert.Contains("firmware start-up (26 s)", report.Headline);
    }

    [Fact]
    public void Only_real_freezes_count_as_stalls()
    {
        var report = SleepHealth.Build(WinEvents.Parse(Xml));
        var stall = Assert.Single(report.Stalls); // not the -1.3 s correction, not the 0.8 s sync
        Assert.Equal(45.222, stall.Length.TotalSeconds, 3);
    }

    [Fact]
    public void A_driver_that_blocked_sleep_is_named()
    {
        var block = Assert.Single(SleepHealth.Build(WinEvents.Parse(Xml)).Blocks);
        Assert.Equal("vpcivsp", block.Driver);
        Assert.Equal(@"ROOT\VPCIVSP\0000", block.Device);
    }

    [Theory]
    [InlineData(4, "Sleep (S3)")]
    [InlineData(5, "Hibernate")]
    [InlineData(null, "Sleep")]
    public void State_names(int? state, string name) => Assert.Equal(name, SleepHealth.StateName(state));

    [Fact]
    public void A_quick_resume_has_no_headline()
    {
        var xml = EventXml.Event("Microsoft-Windows-Power-Troubleshooter", 1, "2026-09-07T02:19:36Z",
            ("SleepTime", "2026-09-07T02:18:42Z"), ("WakeTime", "2026-09-07T02:19:36Z"), ("BiosInitDuration", "900"), ("DriverInitDuration", "1200"), ("HiberReadDuration", "0"), ("EffectiveState", "4"));
        var report = SleepHealth.Build(WinEvents.Parse(xml));
        Assert.Null(report.Headline);
        Assert.False(report.Cycles[0].IsSlow);
    }
}

public class CrashHistoryTests
{
    [Fact]
    public void Reads_the_bugcheck_report_windows_logs()
    {
        const string msg = @"The computer has rebooted from a bugcheck.  The bugcheck was: 0x0000003b (0x00000000c0000005, 0xfffff8056e16ca26, 0xffffd586cabe07a0, 0x0000000000000000). A dump was saved in: C:\Windows\MEMORY.DMP. Report Id: 58fc4791-d228-4195-a5dd-257562517361.";
        var b = BugChecks.FromReport(new DateTime(2026, 9, 11, 11, 52, 31), msg)!;
        Assert.Equal(0x3Bu, b.Code);
        Assert.Equal("SYSTEM_SERVICE_EXCEPTION", b.Name);
        Assert.Equal(4, b.Parameters.Count);
        Assert.Equal("0x00000000c0000005", b.Parameters[0]);
        Assert.Equal(@"C:\Windows\MEMORY.DMP", b.DumpPath);
        Assert.Contains("graphics or antivirus", b.Summary);
    }

    [Theory]
    [InlineData(0x116u, "VIDEO_TDR_FAILURE")]
    [InlineData(0x113u, "VIDEO_DXGKRNL_FATAL_ERROR")]
    [InlineData(0x124u, "WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(0xDEADu, "0xDEAD")]
    public void Known_codes_have_names_unknown_ones_show_hex(uint code, string name) => Assert.Equal(name, BugChecks.NameOf(code));

    [Fact]
    public void Text_that_isnt_a_bugcheck_report_is_ignored()
    {
        Assert.Null(BugChecks.FromReport(DateTime.Now, "The previous system shutdown at 3:00 AM was unexpected."));
        Assert.Null(BugChecks.FromReport(DateTime.Now, null));
    }

    private const string Analysis = """
        *******************************************************************************
        *                                                                             *
        *                        Bugcheck Analysis                                    *
        *                                                                             *
        *******************************************************************************

        VIDEO_TDR_FAILURE (116)
        Attempt to reset the display driver and recover from timeout failed.
        Arguments:
        Arg1: ffffd586cabe07a0, Optional pointer to internal TDR recovery context (TDR_RECOVERY_CONTEXT).

        BUGCHECK_CODE:  116

        PROCESS_NAME:  System

        STACK_TEXT:
        ffffa80b`3f3f7e68 fffff800`5a123456 : 00000000`00000116 ffffd586`cabe07a0 : nt!KeBugCheckEx
        ffffa80b`3f3f7e70 fffff800`5a123999 : ffffd586`cabe07a0 00000000`00000000 : dxgkrnl!TdrBugcheckOnTimeout+0xec
        ffffa80b`3f3f7eb0 fffff800`5a124000 : 00000000`00000000 00000000`00000000 : nvlddmkm+0x1b2c3d4

        IMAGE_NAME:  nvlddmkm.sys

        MODULE_NAME: nvlddmkm

        FAILURE_BUCKET_ID:  0x116_IMAGE_nvlddmkm.sys
        """;

    [Fact]
    public void Reads_what_the_debugger_blames()
    {
        var a = DumpAnalyzer.Parse("091126-13359-01.dmp", Analysis.Split('\n'));
        Assert.Equal(0x116u, a.Code);
        Assert.Equal("VIDEO_TDR_FAILURE", a.Name);
        Assert.Equal("nvlddmkm.sys", a.Blamed);
        Assert.False(a.BlamesWindowsItself);
        Assert.Equal("0x116_IMAGE_nvlddmkm.sys", a.FailureBucket);
        Assert.Equal("System", a.ProcessName);
        Assert.StartsWith("Attempt to reset the display driver", a.Description);
        Assert.Equal(3, a.StackTop.Count);
        Assert.Contains("dxgkrnl!TdrBugcheckOnTimeout", a.StackTop[1]);
    }

    [Fact]
    public void Windows_itself_is_a_symptom_not_a_culprit()
    {
        var a = DumpAnalyzer.Parse("x.dmp", ["IMAGE_NAME:  ntkrnlmp.exe", "MODULE_NAME: nt"]);
        Assert.True(a.BlamesWindowsItself);
    }

    [Fact]
    public void Lists_minidumps_newest_first_and_survives_a_missing_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cars-dumps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "old.dmp"), "x");
            File.SetLastWriteTime(Path.Combine(dir, "old.dmp"), new DateTime(2026, 8, 1));
            File.WriteAllText(Path.Combine(dir, "new.dmp"), "xx");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a dump");
            var list = DumpFiles.List(dir);
            Assert.Equal(["new.dmp", "old.dmp"], list.Select(d => d.Name));
            Assert.Empty(DumpFiles.List(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
