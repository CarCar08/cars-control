using CarsControl.Core.Health;

namespace CarsControl.Tests;

public class DriveHealthRulesTests
{
    private static DriveReading Drive(DriveKind kind, string health = "Healthy", int? temp = null, int? wear = null, long? readErrs = null, long? writeErrs = null) =>
        new("Test drive", kind, 1_000_000_000_000, health, ["C"], temp, wear, 1000, readErrs, writeErrs);

    [Fact]
    public void A_healthy_drive_with_no_counters_is_good()
    {
        var v = DriveHealthRules.Evaluate(Drive(DriveKind.Nvme));
        Assert.Equal(DriveLevel.Good, v.Level);
        Assert.Empty(v.Notes);
        Assert.Equal("Healthy", v.Summary);
    }

    [Theory]
    [InlineData("Warning", DriveLevel.Watch)]
    [InlineData("Unhealthy", DriveLevel.Bad)]
    [InlineData("Unknown", DriveLevel.Watch)]
    public void Windows_health_status_maps_to_a_level(string status, DriveLevel expected) =>
        Assert.Equal(expected, DriveHealthRules.Evaluate(Drive(DriveKind.Ssd, status)).Level);

    [Theory]
    [InlineData(DriveKind.Nvme, 69, DriveLevel.Good)]
    [InlineData(DriveKind.Nvme, 70, DriveLevel.Watch)]
    [InlineData(DriveKind.Nvme, 80, DriveLevel.Bad)]
    [InlineData(DriveKind.HardDisk, 49, DriveLevel.Good)]
    [InlineData(DriveKind.HardDisk, 50, DriveLevel.Watch)]
    [InlineData(DriveKind.HardDisk, 60, DriveLevel.Bad)]
    public void Temperature_limits_depend_on_the_kind_of_drive(DriveKind kind, int temp, DriveLevel expected) =>
        Assert.Equal(expected, DriveHealthRules.Evaluate(Drive(kind, temp: temp)).Level);

    [Theory]
    [InlineData(79, DriveLevel.Good)]
    [InlineData(80, DriveLevel.Watch)]
    [InlineData(95, DriveLevel.Bad)]
    public void Wear_matters_for_flash_drives(int wear, DriveLevel expected) =>
        Assert.Equal(expected, DriveHealthRules.Evaluate(Drive(DriveKind.Ssd, wear: wear)).Level);

    [Fact]
    public void Wear_is_ignored_on_spinning_disks()
    {
        // Spinning disks don't report endurance; a stray value must not raise an alarm.
        Assert.Equal(DriveLevel.Good, DriveHealthRules.Evaluate(Drive(DriveKind.HardDisk, wear: 99)).Level);
    }

    [Fact]
    public void Uncorrected_errors_are_always_bad_and_counted_together()
    {
        var v = DriveHealthRules.Evaluate(Drive(DriveKind.HardDisk, readErrs: 2, writeErrs: 3));
        Assert.Equal(DriveLevel.Bad, v.Level);
        Assert.Contains("5 read/write errors", Assert.Single(v.Notes));
    }

    [Fact]
    public void The_worst_finding_wins()
    {
        var v = DriveHealthRules.Evaluate(Drive(DriveKind.Nvme, "Warning", temp: 85));
        Assert.Equal(DriveLevel.Bad, v.Level);
        Assert.Equal(2, v.Notes.Count);
    }

    [Theory]
    [InlineData(17, 4, DriveKind.Nvme)]
    [InlineData(7, 4, DriveKind.Usb)]
    [InlineData(11, 3, DriveKind.HardDisk)]
    [InlineData(11, 4, DriveKind.Ssd)]
    [InlineData(11, 0, DriveKind.Unknown)]
    public void Bus_and_media_type_pick_the_kind(int bus, int media, DriveKind expected) =>
        Assert.Equal(expected, DriveHealthReader.KindOf(bus, media));

    [Theory]
    [InlineData(0, "Healthy")]
    [InlineData(1, "Warning")]
    [InlineData(2, "Unhealthy")]
    [InlineData(5, "Unknown")]
    public void Health_status_numbers_have_names(int code, string name) => Assert.Equal(name, DriveHealthReader.HealthName(code));
}

public class BootPerformanceTests
{
    private const string P = "Microsoft-Windows-Diagnostics-Performance";

    [Fact]
    public void Boots_shutdowns_and_slow_items_are_summarised()
    {
        var xml =
            EventXml.Event(P, 100, "2026-10-10T08:00:00.0000000Z", ("BootTime", "84000"), ("MainPathBootTime", "30000"), ("BootPostBootTime", "54000"), ("BootKernelInitTime", "4000"), ("BootDriverInitTime", "9000"), ("BootExplorerInitTime", "2000")) +
            EventXml.Event(P, 100, "2026-10-11T08:00:00.0000000Z", ("BootTime", "41000"), ("MainPathBootTime", "20000"), ("BootPostBootTime", "21000")) +
            EventXml.Event(P, 200, "2026-10-10T23:00:00.0000000Z", ("ShutdownTime", "12500")) +
            EventXml.Event(P, 101, "2026-10-10T08:01:00.0000000Z", ("Name", "salad.exe"), ("FriendlyName", "Salad"), ("TotalTime", "20000")) +
            EventXml.Event(P, 101, "2026-10-11T08:01:00.0000000Z", ("Name", "salad.exe"), ("FriendlyName", "Salad"), ("TotalTime", "35000")) +
            EventXml.Event(P, 102, "2026-10-11T08:01:00.0000000Z", ("FileName", "nvlddmkm.sys"), ("TotalTime", "1500"));
        var report = BootPerformance.Build(WinEvents.Parse(xml));

        Assert.Equal(2, report.Boots.Count);
        // Newest first.
        Assert.False(report.Boots[0].IsSlow);
        Assert.True(report.Boots[1].IsSlow);
        Assert.Equal(TimeSpan.FromSeconds(9), report.Boots[1].Drivers);
        Assert.Equal(TimeSpan.FromSeconds(12.5), Assert.Single(report.Shutdowns).Time);

        // The same app on two boots shows once, at its worst.
        Assert.Equal(2, report.SlowItems.Count);
        Assert.Equal(new SlowItem("App", "Salad", TimeSpan.FromSeconds(35)), report.SlowItems[0]);
        Assert.Equal("Driver", report.SlowItems[1].Kind);
        Assert.Equal("nvlddmkm.sys", report.SlowItems[1].Name);
    }

    [Fact]
    public void No_events_is_an_empty_report()
    {
        var report = BootPerformance.Build([]);
        Assert.Empty(report.Boots);
        Assert.Empty(report.Shutdowns);
        Assert.Empty(report.SlowItems);
    }

    [Fact]
    public void The_query_asks_for_boot_shutdown_and_degradation_events()
    {
        var q = BootPerformance.Query(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        foreach (var id in new[] { 100, 200, 101, 102, 103 })
            Assert.Contains($"Provider[@Name='{P}'] and EventID={id}", q);
    }
}

public class PowerRequestsTests
{
    private static readonly string[] Output =
    [
        "DISPLAY:",
        "None.",
        "",
        "SYSTEM:",
        @"[PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe",
        "Video Wake Lock",
        "[DRIVER] Realtek High Definition Audio(SST)",
        "An audio stream is currently in use.",
        "",
        "AWAYMODE:",
        "None.",
    ];

    [Fact]
    public void Parses_the_requests_and_names_the_apps()
    {
        var list = PowerRequests.Parse(Output);
        Assert.Equal(2, list.Count);
        Assert.Equal(new PowerRequest("keeping the PC awake", "PROCESS", "chrome.exe"), list[0]);
        Assert.Equal("DRIVER", list[1].Kind);
        Assert.Equal("Realtek High Definition Audio(SST)", list[1].Who);
    }

    [Fact]
    public void None_means_nothing_is_blocking_sleep()
    {
        Assert.Empty(PowerRequests.Parse(["DISPLAY:", "None.", "", "SYSTEM:", "None."]));
        Assert.Empty(PowerRequests.Parse([]));
    }
}

public class DriverStoreTests
{
    private static readonly string[] Output =
    [
        "Microsoft PnP Utility",
        "",
        "Published Name:     oem12.inf",
        "Original Name:      nvdmi.inf",
        "Provider Name:      NVIDIA",
        "Class Name:         Display",
        "Driver Version:     09/30/2026 32.0.15.6616",
        "Signer Name:        Microsoft Windows Hardware Compatibility Publisher",
        "",
        "Published Name:     oem7.inf",
        "Original Name:      nvdmi.inf",
        "Provider Name:      NVIDIA",
        "Class Name:         Display",
        "Driver Version:     07/15/2026 32.0.15.6094",
        "",
        "Published Name:     oem3.inf",
        "Original Name:      nvdmi.inf",
        "Provider Name:      NVIDIA",
        "Class Name:         Display",
        "Driver Version:     03/01/2026 32.0.15.5000",
        "",
        "Published Name:     oem9.inf",
        "Original Name:      rtkvhd64.inf",
        "Provider Name:      Realtek",
        "Class Name:         MEDIA",
        "Driver Version:     06/01/2025 6.0.9500.1",
    ];

    private static ISet<string> InUse(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parses_packages_with_date_version_and_use()
    {
        var all = DriverStore.Parse(Output, InUse("oem12.inf", "oem9.inf"));
        Assert.Equal(4, all.Count);
        var nv = all[0];
        Assert.Equal("oem12.inf", nv.PublishedName);
        Assert.Equal("nvdmi.inf", nv.OriginalName);
        Assert.Equal("NVIDIA", nv.Provider);
        Assert.Equal("32.0.15.6616", nv.Version);
        Assert.Equal(new DateTime(2026, 9, 30), nv.Date);
        Assert.True(nv.InUse);
        Assert.False(all[1].InUse);
    }

    [Fact]
    public void Superseded_means_older_copies_of_the_same_driver_that_no_device_uses()
    {
        var all = DriverStore.Parse(Output, InUse("oem12.inf", "oem9.inf"));
        var old = DriverStore.Superseded(all);
        Assert.Equal(["oem7.inf", "oem3.inf"], old.OrderByDescending(p => p.Date).Select(p => p.PublishedName));
    }

    [Fact]
    public void A_copy_still_in_use_is_never_offered_even_when_older()
    {
        var all = DriverStore.Parse(Output, InUse("oem12.inf", "oem7.inf"));
        Assert.DoesNotContain(DriverStore.Superseded(all), p => p.PublishedName == "oem7.inf");
    }

    [Fact]
    public void A_single_package_is_never_superseded()
    {
        Assert.Empty(DriverStore.Superseded(DriverStore.Parse(Output.Skip(21), InUse())));
    }

    [Theory]
    [InlineData("oem0.inf", true)]
    [InlineData("OEM123.INF", true)]
    [InlineData("oem99999.inf", true)]
    [InlineData("nvdmi.inf", false)]
    [InlineData("oem12.inf /f", false)]
    [InlineData(@"..\oem12.inf", false)]
    [InlineData("oem.inf", false)]
    [InlineData("", false)]
    public void Only_published_oem_names_are_valid(string name, bool valid) => Assert.Equal(valid, DriverStore.PublishedNamePattern.IsMatch(name));
}
