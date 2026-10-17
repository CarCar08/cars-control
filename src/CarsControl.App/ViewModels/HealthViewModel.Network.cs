using System.Collections.ObjectModel;
using CarsControl.App.Services;
using CarsControl.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CarsControl.App.ViewModels;

public sealed partial class HealthViewModel
{
    public ObservableCollection<AdapterRow> AdapterRows { get; } = [];
    public ObservableCollection<PeerRow> PeerRows { get; } = [];

    [ObservableProperty] private string _vpnLevel = "Info";
    [ObservableProperty] private string _vpnText = "";
    [ObservableProperty] private string _tailscaleLevel = "Info";
    [ObservableProperty] private string _tailscaleText = "";
    [ObservableProperty] private string _exitNodeText = "";
    [ObservableProperty] private string _exitNodeLevel = "Info";
    [ObservableProperty] private string _netCheckText = "";
    [ObservableProperty] private string _netCheckLevel = "Info";
    [ObservableProperty] private bool _netBusy;
    [ObservableProperty] private bool _tailscaleInstalled;
    [ObservableProperty] private bool _protonInstalled;
    [ObservableProperty] private string _netIntro = "Which connection the internet goes through, and how fast each adapter is. Read-only.";
    [ObservableProperty] private int _probePort = 22;

    public IReadOnlyList<int> ProbePorts { get; } = [22, 445, 3389, 80, 443];

    private async Task LoadNetworkAsync()
    {
        if (NetBusy) return;
        NetBusy = true;
        try
        {
            var adapters = await Task.Run(NetworkStatus.Adapters);
            ProtonInstalled = Extras.ProtonVpn(adapters);
            _vpn = ProtonInstalled ? NetworkStatus.Vpn(adapters) : null;
            VpnLevel = _vpn?.Level switch { Core.Health.VpnLevel.Carrying => "Good", Core.Health.VpnLevel.ConnectedNotCarrying => "Watch", _ => "Info" };
            VpnText = _vpn?.Summary ?? "";
            AdapterRows.Clear();
            foreach (var a in adapters.Where(a => a.Up || !a.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)))
                AdapterRows.Add(new AdapterRow(a.Name, a.Description, a.Up ? "Up" : "Down", a.SpeedBps > 0 ? (a.SpeedBps >= 1_000_000_000 ? $"{a.SpeedBps / 1_000_000_000.0:0.#} Gbps" : $"{a.SpeedBps / 1_000_000} Mbps") : "", a.IsDefaultRoute, a.IsProton));

            var ts = await TailscaleStatus.ReadAsync();
            _tailscale = ts;
            TailscaleInstalled = ts.Installed;
            NetIntro = (ProtonInstalled, ts.Installed) switch
            {
                (true, true) => "Whether Proton VPN really carries your traffic, how your other devices reach this PC over Tailscale, and which connection the internet goes through. Read-only.",
                (true, false) => "Whether Proton VPN really carries your traffic, and which connection the internet goes through. Read-only.",
                (false, true) => "How your other devices reach this PC over Tailscale, and which connection the internet goes through. Read-only.",
                _ => "Which connection the internet goes through, and how fast each adapter is. Read-only.",
            };
            PeerRows.Clear();
            foreach (var p in ts.Peers) PeerRows.Add(new PeerRow(p, ProbeAsync));
            if (!ts.Installed)
            {
                TailscaleLevel = "Info";
                TailscaleText = "Tailscale isn't installed on this PC.";
                ExitNodeText = "";
            }
            else if (!ts.Running)
            {
                TailscaleLevel = "Watch";
                TailscaleText = ts.Error ?? $"Tailscale isn't running (its state is {ts.BackendState ?? "unknown"}).";
                ExitNodeText = "";
            }
            else
            {
                var online = ts.Peers.Count(p => p.Online);
                var relayed = ts.Peers.Count(p => p.Path == PeerPath.Relayed);
                TailscaleLevel = "Good";
                TailscaleText = $"Tailscale is running on {ts.Tailnet}: {online} of {ts.Peers.Count} other devices are online" + (relayed > 0 ? $", {relayed} reached through a relay rather than directly." : ".");
                // Only with Proton VPN does "where an exit node's traffic leaves" have a wrong answer worth flagging.
                var vpnCarrying = _vpn?.Level == Core.Health.VpnLevel.Carrying;
                ExitNodeLevel = ts.OfferingExitNode && _vpn is not null && !vpnCarrying ? "Bad" : "Info";
                ExitNodeText = (ts.OfferingExitNode, _vpn is null, vpnCarrying) switch
                    {
                        (true, true, _) => "This PC is offering itself as an exit node: devices that use it go out through this PC's internet connection.",
                        (true, false, true) => "This PC is offering itself as an exit node, and Proton VPN is carrying its traffic: devices using it go out through Proton.",
                        (true, false, false) => "This PC is offering itself as an exit node but Proton VPN isn't carrying traffic: a device using it would leave through your normal connection. Connect Proton VPN or stop offering the exit node in Tailscale.",
                        _ => "This PC isn't offering itself as an exit node right now.",
                    }
                    + (ts.SubnetRoutes.Count > 0 ? $" It still shares {string.Join(", ", ts.SubnetRoutes)} with the tailnet." : "");
            }
            BuildOverview();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("network", ex);
            SetBanner("Couldn't read the network state: " + ex.Message, NoticeKind.Warning);
        }
        finally
        {
            NetBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshNetworkAsync() => LoadNetworkAsync();

    [RelayCommand]
    private async Task RunNetCheckAsync()
    {
        NetBusy = true;
        NetCheckText = "Testing how this PC reaches the Tailscale relays… (about 5 seconds)";
        NetCheckLevel = "Info";
        try
        {
            var n = await TailscaleStatus.NetCheckAsync();
            if (n is null) { NetCheckText = "Tailscale didn't answer."; return; }
            if (!n.Udp) { NetCheckLevel = "Bad"; NetCheckText = "UDP is blocked, so Tailscale can only use relays (slow, but it works)."; return; }
            NetCheckLevel = n.StrictNat ? "Watch" : "Good";
            NetCheckText = n.StrictNat
                ? "Strict NAT: the network changes this PC's address for every destination, so devices can't connect directly and fall back to relays. With Proton VPN, set the NAT type to Moderate in its settings, then restart Tailscale."
                : $"Direct connections are possible (NAT type is fine). Nearest relay: {n.NearestDerp ?? "unknown"}" + (n.NearestDerpMs is { } ms ? $", {ms} ms" : "") + (n.PublicIpv4 is { } ip ? $". Public address seen by Tailscale: {ip}." : ".");
        }
        finally
        {
            NetBusy = false;
        }
    }

    private async Task ProbeAsync(TailscalePeer peer, int port)
    {
        var row = PeerRows.FirstOrDefault(r => r.Peer == peer);
        if (row is null || peer.Address is not { } address) return;
        row.ProbeResult = $"Trying {address}:{port}…";
        var (open, took, error) = await NetworkStatus.TcpProbeAsync(address, port, TimeSpan.FromSeconds(4));
        row.ProbeResult = open ? $"Port {port} answered in {took.TotalMilliseconds:0} ms" : $"Port {port}: {error}";
    }

    [RelayCommand]
    private Task ProbePeerAsync(PeerRow? row) => row is null ? Task.CompletedTask : row.ProbeAsync(ProbePort);
}
