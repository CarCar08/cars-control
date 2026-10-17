using CarsControl.Contracts;
using CarsControl.Core.Elevation;
using CarsControl.Core.Health;
using CarsControl.Core.Settings;

namespace CarsControl.Tests;

public class TailscaleStatusTests
{
    // Shaped like `tailscale status --json` output (names and addresses replaced).
    private const string Json = """
        {
          "Version": "1.104.1", "BackendState": "Running", "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"],
          "Health": [],
          "CurrentTailnet": { "Name": "someone@example.com", "MagicDNSSuffix": "tail0000aa.ts.net" },
          "Self": { "HostName": "Desk-PC", "OS": "windows", "Online": true, "ExitNodeOption": false, "PrimaryRoutes": ["192.168.1.0/24"], "TailscaleIPs": ["100.64.0.1"] },
          "Peer": {
            "nodekey:aaa": { "HostName": "Living Room TV", "OS": "android", "Online": false, "Active": false, "CurAddr": "", "Relay": "dfw", "LastSeen": "2026-09-01T11:33:18Z", "ExitNode": false, "ExitNodeOption": false, "TailscaleIPs": ["100.64.0.2", "fd7a::1"] },
            "nodekey:bbb": { "HostName": "MacBook-Air", "OS": "macOS", "Online": true, "Active": true, "CurAddr": "203.0.113.26:12816", "Relay": "dfw", "LastSeen": "2026-10-13T18:29:22Z", "ExitNode": false, "ExitNodeOption": true, "TailscaleIPs": ["100.64.0.5"] },
            "nodekey:ccc": { "HostName": "localhost", "OS": "iOS", "Online": true, "Active": true, "CurAddr": "", "Relay": "dfw", "LastSeen": "0001-01-01T00:00:00Z", "ExitNode": true, "ExitNodeOption": true, "TailscaleIPs": ["100.64.0.3"] },
            "nodekey:ddd": { "HostName": "Laptop-1", "OS": "windows", "Online": true, "Active": false, "CurAddr": "", "Relay": "dfw", "TailscaleIPs": ["100.64.0.4"] }
          }
        }
        """;

    [Fact]
    public void Reads_the_tailnet_and_this_pc()
    {
        var t = TailscaleStatus.Parse(Json);
        Assert.True(t.Installed);
        Assert.True(t.Running);
        Assert.Equal("someone@example.com", t.Tailnet);
        Assert.Equal("Desk-PC", t.SelfName);
        Assert.Equal(["192.168.1.0/24"], t.SubnetRoutes);
        Assert.False(t.OfferingExitNode);
        Assert.Empty(t.Warnings);
        Assert.Equal(4, t.Peers.Count);
    }

    [Fact]
    public void Peers_are_direct_relayed_idle_or_offline_and_online_ones_come_first()
    {
        var t = TailscaleStatus.Parse(Json);
        var byName = t.Peers.ToDictionary(p => p.Name);
        Assert.Equal(PeerPath.Direct, byName["MacBook-Air"].Path);
        Assert.Equal(PeerPath.Relayed, byName["localhost"].Path);
        Assert.Equal(PeerPath.Idle, byName["Laptop-1"].Path);
        Assert.Equal(PeerPath.Offline, byName["Living Room TV"].Path);
        Assert.All(t.Peers.Take(3), p => Assert.True(p.Online));
        Assert.False(t.Peers[3].Online);
        Assert.Equal("relayed via dfw", byName["localhost"].PathText);
        Assert.Equal("direct", byName["MacBook-Air"].PathText);
    }

    [Fact]
    public void Exit_node_use_offer_and_addresses_are_read()
    {
        var peers = TailscaleStatus.Parse(Json).Peers.ToDictionary(p => p.Name);
        Assert.True(peers["localhost"].UsingExitNode);
        Assert.True(peers["MacBook-Air"].OffersExitNode);
        Assert.False(peers["Laptop-1"].OffersExitNode);
        Assert.Equal("100.64.0.2", peers["Living Room TV"].Address);
        Assert.NotNull(peers["Living Room TV"].LastSeen);
        Assert.Null(peers["localhost"].LastSeen); // 0001-01-01 means "never"
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ broken")]
    public void Garbage_never_throws(string text)
    {
        var t = TailscaleStatus.Parse(text);
        Assert.False(t.Running);
        Assert.Empty(t.Peers);
    }

    [Fact]
    public void Stopped_and_logged_out_states_are_not_running()
    {
        Assert.False(TailscaleStatus.Parse("""{ "BackendState": "Stopped" }""").Running);
        Assert.Equal("NeedsLogin", TailscaleStatus.Parse("""{ "BackendState": "NeedsLogin", "Health": ["You are logged out."] }""").BackendState);
        Assert.Equal(["You are logged out."], TailscaleStatus.Parse("""{ "BackendState": "NeedsLogin", "Health": ["You are logged out."] }""").Warnings);
    }

    [Fact]
    public void Netcheck_reads_nat_type_nearest_relay_and_public_address()
    {
        string[] lines =
        [
            "Report:",
            "\t* Time: 2026-10-13 08:48:19.5559199-05:00",
            "\t* UDP: true",
            "\t* IPv4: yes, 203.0.113.26:57096",
            "\t* IPv6: no, but OS has support",
            "\t* MappingVariesByDestIP: false",
            "\t* PortMapping: ",
            "\t* Nearest DERP: Dallas",
            "\t* DERP latency:",
            "\t\t- dfw: 15.5ms  (Dallas)",
            "\t\t- ord: 25.6ms  (Chicago)",
        ];
        var n = TailscaleStatus.ParseNetCheck(lines);
        Assert.True(n.Udp);
        Assert.False(n.StrictNat);
        Assert.Equal("Dallas", n.NearestDerp);
        Assert.Equal(16, n.NearestDerpMs);
        Assert.Equal("203.0.113.26", n.PublicIpv4);
    }

    [Fact]
    public void Strict_nat_is_the_proton_default_that_forces_relaying()
    {
        var n = TailscaleStatus.ParseNetCheck(["* UDP: true", "* MappingVariesByDestIP: true"]);
        Assert.True(n.StrictNat);
        Assert.False(TailscaleStatus.ParseNetCheck(["* UDP: true"]).StrictNat); // unknown is not an alarm
    }
}

public class VpnStatusTests
{
    private static NetAdapter A(string name, string desc, bool up, bool route, int index = 1) => new(name, desc, up, 1_000_000_000, index, route);

    [Fact]
    public void No_proton_adapter_means_not_connected()
    {
        var v = NetworkStatus.Vpn([A("Ethernet", "Realtek PCIe 2.5GbE", true, true), A("Tailscale", "Tailscale Tunnel", true, false)]);
        Assert.Equal(VpnLevel.NotConnected, v.Level);
        Assert.Null(v.Adapter);
    }

    [Fact]
    public void A_down_proton_adapter_is_not_connected()
    {
        Assert.Equal(VpnLevel.NotConnected, NetworkStatus.Vpn([A("ProtonVPN", "WireGuard Tunnel", false, false), A("Ethernet", "Realtek", true, true)]).Level);
    }

    [Fact]
    public void Proton_carrying_the_default_route_is_the_good_state()
    {
        var v = NetworkStatus.Vpn([A("ProtonVPN", "WireGuard Tunnel", true, true), A("Ethernet", "Realtek", true, false)]);
        Assert.Equal(VpnLevel.Carrying, v.Level);
        Assert.Equal("ProtonVPN", v.Adapter);
    }

    [Fact]
    public void Proton_up_while_traffic_leaves_elsewhere_is_called_out_with_the_adapter()
    {
        var v = NetworkStatus.Vpn([A("ProtonVPN", "WireGuard Tunnel", true, false), A("Ethernet", "Realtek", true, true)]);
        Assert.Equal(VpnLevel.ConnectedNotCarrying, v.Level);
        Assert.Contains("Ethernet", v.Summary);
    }

    [Theory]
    [InlineData("Proton VPN", "TAP Adapter", true)]
    [InlineData("Local Area Connection 4", "ProtonVPN TUN Adapter", true)]
    [InlineData("Ethernet", "Realtek PCIe", false)]
    public void Proton_is_recognised_by_adapter_name_or_description(string name, string desc, bool proton) =>
        Assert.Equal(proton, A(name, desc, true, false).IsProton);

    [Theory]
    [InlineData("Ethernet-Npcap Packet Driver (NPCAP)-0000", "Realtek PCIe-Npcap Packet Driver (NPCAP)-0000", true)]
    [InlineData("vEthernet (WSL)-QoS Packet Scheduler-0000", "Hyper-V Virtual Ethernet Adapter #2-QoS Packet Scheduler-0000", true)]
    [InlineData("Local Area Connection* 3", "WAN Miniport (L2TP)", true)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", true)]
    [InlineData("vSwitch (Default Switch)", "Hyper-V Virtual Switch Extension Adapter", true)]
    [InlineData("Ethernet", "Realtek PCIe 2.5GbE Family Controller", false)]
    [InlineData("Tailscale", "Tailscale Tunnel", false)]
    [InlineData("ProtonVPN", "WireGuard Tunnel", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    public void Driver_layers_and_placeholders_are_not_connections(string name, string description, bool noise) =>
        Assert.Equal(noise, NetworkStatus.IsNoise(name, description));

    [Fact]
    public async Task A_tcp_probe_reports_open_and_refused_ports_without_ping()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var open = await NetworkStatus.TcpProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3));
        Assert.True(open.Open);
        listener.Stop();
        var closed = await NetworkStatus.TcpProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3));
        Assert.False(closed.Open);
        Assert.False(string.IsNullOrEmpty(closed.Error));
    }
}

public class ActivityTests
{
    [Theory]
    [InlineData("chrome#12", "chrome")]
    [InlineData("svchost#3", "svchost")]
    [InlineData("code", "code")]
    [InlineData("a#b", "a#b")]
    public void Counter_names_lose_their_duplicate_number(string raw, string expected) => Assert.Equal(expected, ProcessRules.CleanName(raw));

    [Fact]
    public void Gpu_engine_instances_name_the_process_and_engine()
    {
        Assert.Equal((1234, "3D"), ProcessRules.ParseGpuEngine("pid_1234_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D"));
        Assert.Equal((88, "VideoDecode"), ProcessRules.ParseGpuEngine("pid_88_luid_0x0_0x1_phys_0_eng_3_engtype_VideoDecode"));
        Assert.Null(ProcessRules.ParseGpuEngine("luid_0x0_phys_0"));
    }

    [Fact]
    public void A_process_gpu_use_is_its_busiest_engine_not_the_sum()
    {
        var gpu = ProcessRules.GpuPerProcess(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_10_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 25.0),   // same type adds: 55
            ("pid_10_luid_0x0_0x1_phys_0_eng_2_engtype_Copy", 40.0), // different engine: max wins
            ("pid_20_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 70.0),
            ("pid_20_luid_0x0_0x1_phys_0_eng_5_engtype_3D", 60.0),   // 130 caps at 100
            ("garbage", 5.0),
        ]);
        Assert.Equal(55.0, gpu[10]);
        Assert.Equal(100.0, gpu[20]);
        Assert.Equal(2, gpu.Count);
    }

    [Theory]
    [InlineData("Salad.exe", null, false, ProcessRole.Salad)]
    [InlineData("xmrig", null, false, ProcessRole.Salad)]
    [InlineData("t-rex", null, false, ProcessRole.Salad)]
    [InlineData("SaladBowl.Service", null, false, ProcessRole.Salad)]
    [InlineData("anything", @"C:\Program Files\Salad\app\worker.exe", false, ProcessRole.Salad)]
    [InlineData("vmmemWSL", null, true, ProcessRole.Salad)]
    [InlineData("vmmemWSL", null, false, ProcessRole.Protected)]
    [InlineData("svchost", @"C:\Windows\System32\svchost.exe", false, ProcessRole.Windows)]
    [InlineData("MsMpEng", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", false, ProcessRole.Windows)]
    [InlineData("CarsControl", @"C:\Program Files\CarsControl\CarsControl.exe", false, ProcessRole.Self)]
    [InlineData("CarsControl.Helper", null, false, ProcessRole.Self)]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false, ProcessRole.Normal)]
    [InlineData("GTA5_Enhanced", @"D:\SteamLibrary\steamapps\common\GTA V Enhanced\GTA5_Enhanced.exe", true, ProcessRole.Normal)]
    [InlineData("mystery", null, false, ProcessRole.Protected)]
    [InlineData("hl2", @"D:\SteamLibrary\steamapps\common\Half-Life 2\hl2.exe", false, ProcessRole.Normal)]
    public void Processes_are_classified_for_what_may_be_ended(string name, string? path, bool saladRunning, ProcessRole expected) =>
        Assert.Equal(expected, ProcessRules.Classify(name, path, saladRunning));

    [Fact]
    public void Another_accounts_process_is_protected_even_with_a_path()
    {
        Assert.Equal(ProcessRole.Protected, ProcessRules.Classify("tool", @"C:\Tools\tool.exe", false, null, ownedByYou: false));
        Assert.Equal(ProcessRole.Salad, ProcessRules.Classify("xmrig", @"C:\x\xmrig.exe", false, null, ownedByYou: false)); // Salad's miner stays labelled
    }

    [Fact]
    public void A_programs_processes_are_added_together()
    {
        var snap = new ActivitySnapshot(
        [
            new ProcessUse(10, "chrome", 5, 300, 10, 1, @"C:\c\chrome.exe", ProcessRole.Normal),
            new ProcessUse(11, "Chrome", 7, 200, 20, 2, null, ProcessRole.Normal),
            new ProcessUse(12, "chrome", 1, 100, 5, 0, @"C:\c\chrome.exe", ProcessRole.Normal),
            new ProcessUse(20, "svchost", 3, 50, 1, 0, @"C:\Windows\System32\svchost.exe", ProcessRole.Windows),
        ], 20, 650, 16000, null, DateTime.Now);
        var apps = snap.Apps();
        Assert.Equal(2, apps.Count);
        var chrome = apps.Single(a => a.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, chrome.Instances);
        Assert.Equal(13, chrome.CpuPercent);
        Assert.Equal(600, chrome.RamBytes);
        Assert.Equal(35, chrome.DiskBytesPerSec);
        Assert.Equal(@"C:\c\chrome.exe", chrome.Path);
        Assert.True(chrome.CanEnd);
        Assert.False(apps.Single(a => a.Name == "svchost").CanEnd);
        Assert.Equal("chrome", snap.TopApps(ActivitySort.Ram, 1).Single().Name, ignoreCase: true);
    }

    [Fact]
    public void Only_your_own_apps_can_be_ended()
    {
        Assert.True(new ProcessUse(1, "chrome", 1, 1, 1, 1, @"C:\x\chrome.exe", ProcessRole.Normal).CanEnd);
        foreach (var role in new[] { ProcessRole.Salad, ProcessRole.Windows, ProcessRole.Self, ProcessRole.Protected })
            Assert.False(new ProcessUse(1, "x", 1, 1, 1, 1, null, role).CanEnd);
    }

    [Fact]
    public void Top_lists_sort_by_the_chosen_resource()
    {
        var snap = new ActivitySnapshot(
        [
            new ProcessUse(1, "a", 50, 100, 5, 0, null, ProcessRole.Normal),
            new ProcessUse(2, "b", 10, 900, 1, 80, null, ProcessRole.Normal),
            new ProcessUse(3, "c", 30, 500, 99, 10, null, ProcessRole.Normal),
        ], 90, 1500, 16000, 90, DateTime.Now);
        Assert.Equal("a", snap.TopBy(ActivitySort.Cpu, 1).Single().Name);
        Assert.Equal("b", snap.TopBy(ActivitySort.Gpu, 1).Single().Name);
        Assert.Equal("b", snap.TopBy(ActivitySort.Ram, 1).Single().Name);
        Assert.Equal("c", snap.TopBy(ActivitySort.Disk, 1).Single().Name);
        Assert.Equal(["a", "c"], snap.TopBy(ActivitySort.Cpu, 2).Select(p => p.Name));
    }
}

public class RestartPlannerTests
{
    private static RestartSettings On(string time = "04:00", int window = 3, int idle = 30) => new() { Enabled = true, Time = time, WindowHours = window, IdleMinutes = idle };
    private static readonly TimeSpan Away = TimeSpan.FromHours(2);

    private static RestartCheck Check(RestartSettings s, string now, bool pending = true, TimeSpan? idle = null, bool gaming = false, bool full = false, DateTime? last = null) =>
        RestartPlanner.Decide(s, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture), pending, idle ?? Away, gaming, full, last);

    [Fact]
    public void Starts_in_the_window_when_everything_is_quiet()
    {
        var r = Check(On(), "2026-10-14 04:10");
        Assert.Equal(RestartDecision.Start, r.Decision);
    }

    [Theory]
    [InlineData("2026-10-14 03:59")]
    [InlineData("2026-10-14 07:00")]
    [InlineData("2026-10-14 12:00")]
    public void Never_outside_the_window(string now) => Assert.Equal(RestartDecision.Wait, Check(On(), now).Decision);

    [Fact]
    public void The_window_may_cross_midnight()
    {
        var late = On("23:00", 4);
        Assert.Equal(RestartDecision.Start, Check(late, "2026-10-14 01:30").Decision);
        Assert.Equal(RestartDecision.Start, Check(late, "2026-10-13 23:30").Decision);
        Assert.Equal(RestartDecision.Wait, Check(late, "2026-10-14 03:30").Decision);
    }

    [Fact]
    public void Each_blocker_is_respected_and_explained()
    {
        Assert.Contains("off", Check(new RestartSettings(), "2026-10-14 04:10").Why);
        Assert.Contains("No restart", Check(On(), "2026-10-14 04:10", pending: false).Why);
        Assert.Contains("game", Check(On(), "2026-10-14 04:10", gaming: true).Why);
        Assert.Contains("full screen", Check(On(), "2026-10-14 04:10", full: true).Why);
        var active = Check(On(), "2026-10-14 04:10", idle: TimeSpan.FromMinutes(5));
        Assert.Equal(RestartDecision.Wait, active.Decision);
        Assert.Contains("5 min", active.Why);
    }

    [Fact]
    public void One_attempt_per_window()
    {
        Assert.Equal(RestartDecision.Wait, Check(On(), "2026-10-14 05:00", last: DateTime.Parse("2026-10-14 04:05")).Decision);
        Assert.Equal(RestartDecision.Start, Check(On(), "2026-10-15 04:05", last: DateTime.Parse("2026-10-14 04:05")).Decision);
    }

    [Fact]
    public void A_bad_time_never_restarts()
    {
        Assert.Equal(RestartDecision.Wait, Check(On("soon"), "2026-10-14 04:10").Decision);
    }

    [Fact]
    public void The_commands_are_fixed_and_the_countdown_can_be_cancelled()
    {
        Assert.Equal(["/r", "/t", "300", "/c", "reason"], RestartPlanner.StartArguments("reason"));
        Assert.Equal(["/a"], RestartPlanner.CancelArguments);
    }

    [Fact]
    public void Defaults_are_off_and_cautious()
    {
        var d = new RestartSettings();
        Assert.False(d.Enabled);
        Assert.Equal("04:00", d.Time);
        Assert.Equal(30, d.IdleMinutes);
        Assert.True(new HealthSettings().RestorePointBeforeRiskyChanges);
    }

    [Fact]
    public void Pending_restart_summary_lists_the_reasons()
    {
        Assert.False(new PendingRebootState([]).Pending);
        var p = new PendingRebootState(["Windows Update finished installing", "files are waiting to be replaced"]);
        Assert.True(p.Pending);
        Assert.Contains("Windows Update finished installing; files are waiting", p.Summary);
    }

    [Fact]
    public void Leftover_file_renames_alone_never_make_a_restart_pending()
    {
        var p = new PendingRebootState([], ["some files will be replaced"]);
        Assert.False(p.Pending);
        Assert.Contains("No update needs a restart", p.Summary);
        Assert.Equal("No restart is waiting.", new PendingRebootState([]).Summary);
    }
}

public class SafetyNetTests
{
    [Fact]
    public void Restore_point_names_fit_what_the_helper_accepts()
    {
        var rule = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9 .,:()_-]{1,64}$");
        foreach (var change in Enum.GetValues<RiskyChange>())
        {
            Assert.Matches(rule, SafetyNet.Description(change));
            Assert.Matches(rule, SafetyNet.Description(change, "NVIDIA Graphics Driver 616.56 \"WHQL\" & more \\ stuff that goes on and on and on forever"));
        }
        Assert.Equal("C.A.R.S. before driver update: NVIDIA 616.56", SafetyNet.Description(RiskyChange.DriverUpdate, "NVIDIA 616.56"));
    }

    [Fact]
    public void A_restore_point_can_only_be_made_when_protection_is_on_and_not_blocked()
    {
        Assert.True(new SafetyNetState(true, false).CanCreate);
        Assert.False(new SafetyNetState(false, false).CanCreate);
        Assert.False(new SafetyNetState(true, true).CanCreate);
    }

    [Fact]
    public void The_setting_turns_the_safety_net_off()
    {
        Assert.True(SafetyNet.Wanted(new HealthSettings(), RiskyChange.DriverUpdate));
        Assert.False(SafetyNet.Wanted(new HealthSettings { RestorePointBeforeRiskyChanges = false }, RiskyChange.DriverUpdate));
    }

    [Theory]
    [InlineData("Feature update to Windows 11, version 25H2", true)]
    [InlineData("feature update to Windows 11, version 26H1", true)]
    [InlineData("2026-10 Cumulative Update for Windows 11 Version 25H2 (KB5099999)", false)]
    [InlineData("NVIDIA - Display - 32.0.15.6616", false)]
    [InlineData(null, false)]
    public void Only_the_yearly_upgrade_counts_as_a_feature_update(string? title, bool expected) =>
        Assert.Equal(expected, SafetyNet.IsFeatureUpdate(title));

    private sealed class FakeChannel(Func<HelperOp, OpResult> answer) : IHelperChannel
    {
        public List<HelperOp> Sent { get; } = [];
        public bool IsConnected => true;

        public Task<OpResult> SendAsync(HelperOp op, Action<string>? onLine = null, bool dryRun = false, CancellationToken ct = default)
        {
            Sent.Add(op);
            return Task.FromResult(answer(op));
        }
    }

    private static readonly SafetyNetState On = new(true, false);

    [Fact]
    public async Task A_restore_point_is_made_with_the_changes_name_when_wanted_and_possible()
    {
        var channel = new FakeChannel(_ => OpResult.Success("Restore point created."));
        var r = await SafetyNet.MakeAsync(channel, new HealthSettings(), On, RiskyChange.PrivacyBatch, "12 switches");
        Assert.Equal(RestoreOutcome.Made, r.Outcome);
        Assert.True(r.Made);
        var sent = Assert.IsType<CreateRestorePointOp>(Assert.Single(channel.Sent));
        Assert.Equal("C.A.R.S. before privacy changes: 12 switches", sent.Description);
    }

    [Fact]
    public async Task Nothing_is_sent_when_you_turned_it_off_or_protection_is_off()
    {
        var channel = new FakeChannel(_ => OpResult.Success());
        var off = await SafetyNet.MakeAsync(channel, new HealthSettings { RestorePointBeforeRiskyChanges = false }, On, RiskyChange.DriverUpdate, null);
        var noProtection = await SafetyNet.MakeAsync(channel, new HealthSettings(), new SafetyNetState(false, false), RiskyChange.DriverUpdate, null);
        var blocked = await SafetyNet.MakeAsync(channel, new HealthSettings(), new SafetyNetState(true, true), RiskyChange.DriverUpdate, null);
        Assert.Equal(RestoreOutcome.NotWanted, off.Outcome);
        Assert.Equal(RestoreOutcome.NoProtection, noProtection.Outcome);
        Assert.Equal(RestoreOutcome.NoProtection, blocked.Outcome);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task A_declined_prompt_and_a_failure_are_told_apart()
    {
        var cancelled = await SafetyNet.MakeAsync(new FakeChannel(_ => throw new HelperCancelledException()), new HealthSettings(), On, RiskyChange.FeatureUpdate, null);
        var noSession = await SafetyNet.MakeAsync(new FakeChannel(_ => throw new HelperSessionClosedException()), new HealthSettings(), On, RiskyChange.FeatureUpdate, null);
        var unavailable = await SafetyNet.MakeAsync(new FakeChannel(_ => throw new HelperUnavailableException("The helper is missing.")), new HealthSettings(), On, RiskyChange.FeatureUpdate, null);
        var refused = await SafetyNet.MakeAsync(new FakeChannel(_ => new OpResult(OpStatus.Failed, 1, "Windows said no.")), new HealthSettings(), On, RiskyChange.FeatureUpdate, null);
        var declinedByResult = await SafetyNet.MakeAsync(new FakeChannel(_ => new OpResult(OpStatus.Cancelled, -1, "Admin approval was cancelled.")), new HealthSettings(), On, RiskyChange.FeatureUpdate, null);
        Assert.Equal(RestoreOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(RestoreOutcome.Cancelled, noSession.Outcome);
        Assert.Equal(RestoreOutcome.Failed, unavailable.Outcome);
        Assert.Equal("The helper is missing.", unavailable.Message);
        Assert.Equal(RestoreOutcome.Failed, refused.Outcome);
        Assert.Equal(RestoreOutcome.Cancelled, declinedByResult.Outcome);
    }
}

public class HealthSummaryAndReportTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0);

    private static SystemSpecs Specs() => new("DESK-PC", "AMD Ryzen 9 9950X3D", 16, 32, 5700, [new GpuInfo("NVIDIA GeForce RTX 5090", "32.0.15.6616", 32L << 30, true)],
        [new RamModule(16L << 30, 6000, "G.Skill", "F5-6000", "A1"), new RamModule(16L << 30, 6000, "G.Skill", "F5-6000", "B1")], "PRIME B850-PLUS WIFI", "ASUSTeK", "1644", new DateTime(2026, 6, 1),
        "Windows 11 Pro", "25H2", "26300", Now.AddDays(-2));

    private static BlueScreen Bsod(int daysAgo, string name = "VIDEO_DXGKRNL_FATAL_ERROR") => new(Now.AddDays(-daysAgo), 0x113, name, [], null, name);

    [Fact]
    public void A_quiet_pc_has_nothing_to_report()
    {
        var f = HealthSummary.Build(new HealthSnapshot(Now, Vpn: new VpnStatus(VpnLevel.Carrying, "ProtonVPN", "ok"),
            Drives: [new DriveReading("WD", DriveKind.Nvme, 1L << 40, "Healthy", ["C"])]));
        Assert.Empty(f);
    }

    [Fact]
    public void Findings_are_worst_first_and_worded_for_a_person()
    {
        var snap = new HealthSnapshot(Now,
            Drives: [new DriveReading("Seagate", DriveKind.HardDisk, 1L << 40, "Unhealthy", ["D"])],
            Gpus: [new GpuReading("RTX 5090", "616.56", 91, 10, 5, 2000, 3000, 40, 100, 575, 1, 2)],
            BlueScreens: [Bsod(1), Bsod(3), Bsod(9), Bsod(40)],
            Reboot: new PendingRebootState(["Windows Update finished installing"]),
            Vpn: new VpnStatus(VpnLevel.NotConnected, null, "Proton VPN isn't connected."));
        var f = HealthSummary.Build(snap);
        Assert.Equal(FindingLevel.Bad, f[0].Level);
        Assert.True(f.Zip(f.Skip(1)).All(p => p.First.Level >= p.Second.Level));
        Assert.Contains(f, x => x.Title.Contains("Seagate"));
        Assert.Contains(f, x => x.Title.Contains("91 °C"));
        Assert.Contains(f, x => x.Title.StartsWith("3 blue screens") && x.Detail.Contains("VIDEO_DXGKRNL_FATAL_ERROR ×3"));
        Assert.Contains(f, x => x.Title == "A restart is waiting");
        Assert.Contains(f, x => x.Title == "Proton VPN isn't connected" && x.Level == FindingLevel.Info);
    }

    [Fact]
    public void A_hot_processor_is_reported_only_from_the_package_or_tctl_sensor()
    {
        var hot = new SensorReading("AMD Ryzen", "Cpu", "Core (Tctl/Tdie)", "Temperature", 95, null, null);
        var cool = new SensorReading("AMD Ryzen", "Cpu", "CCD1 (Tdie)", "Temperature", 99, null, null);
        Assert.Contains(HealthSummary.Build(new HealthSnapshot(Now, Sensors: [hot])), f => f.Title.Contains("95 °C"));
        Assert.Empty(HealthSummary.Build(new HealthSnapshot(Now, Sensors: [cool])));
    }

    [Fact]
    public void Freezes_and_slow_wake_get_their_own_lines()
    {
        var sleep = new SleepReport(
            [new SleepCycle(Now.AddDays(-1), Now.AddDays(-1).AddHours(8), "Hibernate", "the power button or lid", TimeSpan.FromHours(8), TimeSpan.FromSeconds(26), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4.9), null)],
            [new Stall(Now.AddDays(-2), TimeSpan.FromSeconds(45))], []);
        var f = HealthSummary.Build(new HealthSnapshot(Now, Sleep: sleep));
        Assert.Contains(f, x => x.Title == "The PC froze 1 time in the last 14 days" && x.Detail.Contains("45 s"));
        Assert.Contains(f, x => x.Title == "Waking from sleep is slow");
    }

    [Fact]
    public void The_report_is_one_html_page_with_the_findings_and_specs()
    {
        var html = HealthReport.Render(new HealthSnapshot(Now, Specs(), BlueScreens: [Bsod(2)], AppVersion: "0.8.0"), redact: false);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("PC health report", html);
        Assert.Contains("AMD Ryzen 9 9950X3D", html);
        Assert.Contains("PRIME B850-PLUS WIFI", html);
        Assert.Contains("1 blue screen in the last 14 days", html);
        Assert.Contains("C.A.R.S. Control 0.8.0", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Redaction_replaces_the_user_the_pc_and_profile_paths()
    {
        Assert.Equal(@"C:\Users\(you)\Documents", HealthReport.Redact(@"C:\Users\alex\Documents", "alex", "DESK-PC"));
        Assert.Equal("(you) on (this PC)", HealthReport.Redact("alex on DESK-PC", "alex", "DESK-PC"));
        Assert.Equal("xalexx", HealthReport.Redact("xalexx", "alex", "DESK-PC")); // whole words only
        Assert.Equal("", HealthReport.Redact("", "alex", "DESK-PC"));
    }

    [Fact]
    public void The_redacted_report_has_neither_name()
    {
        var snap = new HealthSnapshot(Now, Specs(), Tailscale: TailscaleStatus.Parse("""{"BackendState":"Running","Peer":{"k":{"HostName":"DESK-PC","Online":true,"Active":true,"CurAddr":"1.2.3.4:5"}}}"""),
            Analyses: [], BlueScreens: [new BlueScreen(Now.AddDays(-1), 0x113, "VIDEO_DXGKRNL_FATAL_ERROR", [], @"C:\Windows\Minidump\carson.dmp", "x")]);
        var html = HealthReport.Render(snap, redact: true, userName: "alex", machineName: "DESK-PC");
        Assert.DoesNotContain("DESK-PC", html);
        Assert.DoesNotContain("carson", html);
        Assert.Contains("(this PC)", html);
    }

    [Fact]
    public void Everything_inserted_into_the_report_is_html_encoded()
    {
        var snap = new HealthSnapshot(Now, Drives: [new DriveReading("<img src=x onerror=alert(1)>", DriveKind.Ssd, 1L << 40, "Warning", ["C"])]);
        var html = HealthReport.Render(snap, redact: false);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }
}

public class BatteryTests
{
    [Theory]
    [InlineData(50000, 50000, 0)]
    [InlineData(50000, 42500, 15)]
    [InlineData(50000, 20000, 60)]
    [InlineData(50000, 60000, 0)]
    public void Wear_is_how_much_capacity_is_gone(long design, long full, int expected) => Assert.Equal(expected, BatteryRules.Wear(design, full));

    [Theory]
    [InlineData(null, 40000L)]
    [InlineData(50000L, null)]
    [InlineData(0L, 40000L)]
    public void Missing_numbers_give_no_wear(long? design, long? full) => Assert.Null(BatteryRules.Wear(design, full));

    [Fact]
    public void The_summary_is_plain_and_the_verdict_follows_the_wear()
    {
        var b = new BatteryReading(82, false, false, 50000, 41000);
        Assert.Equal("82% (on battery) · holds 82% of its original capacity", b.Summary);
        Assert.Equal("The battery has lost a fair bit of capacity, which is normal with age.", BatteryRules.Verdict(b.WearPercent));
        Assert.Equal("", BatteryRules.Verdict(null));
        Assert.StartsWith("The battery has lost a lot", BatteryRules.Verdict(60));
        Assert.Equal("100% (charging)", new BatteryReading(100, true, true, null, null).Summary);
    }

    [Fact]
    public void A_desktop_has_no_battery() => Assert.Null(BatteryReader.Read());
}
