using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarsControl.Core.Util;

namespace CarsControl.Core.Health;

public enum PeerPath { Offline, Direct, Relayed, Idle }

public sealed record TailscalePeer(string Name, string Os, bool Online, PeerPath Path, string? Relay, DateTime? LastSeen, bool OffersExitNode, bool UsingExitNode, IReadOnlyList<string> Addresses)
{
    public string PathText => Path switch
    {
        PeerPath.Direct => "direct",
        PeerPath.Relayed => $"relayed via {Relay ?? "DERP"}",
        PeerPath.Idle => "online, idle",
        _ => "offline",
    };

    /// <summary>The first IPv4 tailnet address (what you'd connect to).</summary>
    public string? Address => Addresses.FirstOrDefault(a => !a.Contains(':'));
}

public sealed record TailscaleState(
    bool Installed, string? BackendState, string? Tailnet, string? SelfName, IReadOnlyList<string> SelfAddresses,
    bool OfferingExitNode, IReadOnlyList<string> SubnetRoutes, IReadOnlyList<string> Warnings, IReadOnlyList<TailscalePeer> Peers, string? Error = null)
{
    public bool Running => BackendState == "Running";
    public static TailscaleState NotInstalled { get; } = new(false, null, null, null, [], false, [], [], []);
}

public sealed record NetCheck(bool Udp, bool? MappingVariesByDestIp, string? NearestDerp, int? NearestDerpMs, string? PublicIpv4)
{
    /// <summary>Strict NAT (the destination changes the mapping) forces relaying; Proton's NAT type "Moderate" fixes it.</summary>
    public bool StrictNat => MappingVariesByDestIp == true;
}

/// <summary>Reads Tailscale through its own CLI (as you: the local API belongs to your account). No admin.</summary>
public static class TailscaleStatus
{
    public static string? CliPath()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(p) ? p : null;
    }

    public static async Task<TailscaleState> ReadAsync(CancellationToken ct = default)
    {
        var cli = CliPath();
        if (cli is null) return TailscaleState.NotInstalled;
        var r = await ProcessRunner.RunAsync(cli, ["status", "--json"], null, TimeSpan.FromSeconds(15), ct);
        if (r.TimedOut || string.IsNullOrWhiteSpace(r.Stdout))
            return TailscaleState.NotInstalled with { Installed = true, Error = r.TimedOut ? "Tailscale didn't answer." : r.Lines.LastOrDefault() ?? "Tailscale didn't answer." };
        return Parse(r.Stdout);
    }

    public static async Task<NetCheck?> NetCheckAsync(CancellationToken ct = default)
    {
        var cli = CliPath();
        if (cli is null) return null;
        var r = await ProcessRunner.RunAsync(cli, ["netcheck"], null, TimeSpan.FromSeconds(40), ct);
        return r.TimedOut ? null : ParseNetCheck(r.Lines);
    }

    public static TailscaleState Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var self = root.TryGetProperty("Self", out var s) ? s : default;
            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
                foreach (var p in peerMap.EnumerateObject()) peers.Add(ParsePeer(p.Value));

            return new TailscaleState(
                true,
                Str(root, "BackendState"),
                root.TryGetProperty("CurrentTailnet", out var t) ? Str(t, "Name") : null,
                self.ValueKind == JsonValueKind.Object ? Str(self, "HostName") : null,
                Strings(root, "TailscaleIPs"),
                self.ValueKind == JsonValueKind.Object && Bool(self, "ExitNodeOption"),
                self.ValueKind == JsonValueKind.Object ? Strings(self, "PrimaryRoutes") : [],
                Strings(root, "Health"),
                peers.OrderByDescending(x => x.Online).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (JsonException ex)
        {
            return TailscaleState.NotInstalled with { Installed = true, Error = "Couldn't read Tailscale's answer: " + ex.Message };
        }
    }

    private static TailscalePeer ParsePeer(JsonElement p)
    {
        var online = Bool(p, "Online");
        var cur = Str(p, "CurAddr");
        var relay = Str(p, "Relay");
        // Tailscale reports CurAddr only while it has a direct path; Active says traffic flowed recently.
        var path = !online ? PeerPath.Offline
            : !string.IsNullOrEmpty(cur) ? PeerPath.Direct
            : Bool(p, "Active") ? PeerPath.Relayed
            : PeerPath.Idle;
        DateTime? seen = DateTime.TryParse(Str(p, "LastSeen"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year > 2000
            ? d.ToLocalTime() : null;
        return new TailscalePeer(PeerName(Str(p, "HostName"), Str(p, "DNSName")), Str(p, "OS") ?? "", online, path, relay, seen,
            Bool(p, "ExitNodeOption"), Bool(p, "ExitNode"), Strings(p, "TailscaleIPs"));
    }

    /// <summary>iPhones and iPads report their host name as "localhost"; their MagicDNS name ("iphone-15.tail0000aa.ts.net.") is the useful one.</summary>
    public static string PeerName(string? hostName, string? dnsName)
    {
        var dns = dnsName?.Split('.')[0];
        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return hostName;
        return !string.IsNullOrWhiteSpace(dns) ? dns : hostName ?? "unknown";
    }

    public static NetCheck ParseNetCheck(IEnumerable<string> input)
    {
        var lines = input.ToList();
        bool udp = false;
        bool? varies = null;
        string? nearest = null, ipv4 = null;
        int? ms = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('*', ' ', '-', '\t');
            if (line.StartsWith("UDP:", StringComparison.Ordinal)) udp = line.Contains("true");
            else if (line.StartsWith("MappingVariesByDestIP:", StringComparison.Ordinal)) varies = line.Contains("true") ? true : line.Contains("false") ? false : null;
            else if (line.StartsWith("IPv4:", StringComparison.Ordinal) && Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3}):\d+") is { Success: true } m) ipv4 = m.Groups[1].Value;
            else if (line.StartsWith("Nearest DERP:", StringComparison.Ordinal)) nearest = line["Nearest DERP:".Length..].Trim();
        }
        // "dfw: 15.5ms  (Dallas)" lines: take the latency of the nearest region's key.
        if (nearest is not null)
            foreach (var raw in lines)
                if (Regex.Match(raw, @"^\s*-\s*(?<k>[a-z0-9]+):\s*(?<ms>[\d.]+)ms\s*\((?<city>[^)]+)\)") is { Success: true } m && m.Groups["city"].Value.Equals(nearest, StringComparison.OrdinalIgnoreCase))
                {
                    ms = (int)Math.Round(double.Parse(m.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
        return new NetCheck(udp, varies, nearest, ms, ipv4);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
}

public enum VpnLevel { NotConnected, ConnectedNotCarrying, Carrying }

public sealed record NetAdapter(string Name, string Description, bool Up, long SpeedBps, int Index, bool IsDefaultRoute)
{
    public bool IsProton => NetworkStatus.LooksLikeProton(this);
}

public sealed record VpnStatus(VpnLevel Level, string? Adapter, string Summary);

/// <summary>This PC's connections: which adapter carries internet traffic, and whether that's Proton VPN.</summary>
public static class NetworkStatus
{
    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    public static bool LooksLikeProton(NetAdapter a) =>
        a.Name.Contains("proton", StringComparison.OrdinalIgnoreCase) || a.Description.Contains("proton", StringComparison.OrdinalIgnoreCase);

    /// <summary>The adapter Windows would use to reach the internet (1.1.1.1 is only used to pick a route; nothing is sent).</summary>
    public static int? DefaultRouteIndex()
    {
        try
        {
            var address = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            return GetBestInterface(address, out var index) == 0 ? (int)index : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static readonly string[] NoiseDescriptions = ["WAN Miniport", "Microsoft 6to4", "Kernel Debug", "Virtual Switch Extension", "Wi-Fi Direct", "Teredo", "ISATAP", "IP-HTTPS", "Bluetooth Device"];

    /// <summary>Windows lists a pseudo-adapter for every driver layered on a real one (Npcap, QoS, WFP) plus placeholders; none are connections.</summary>
    public static bool IsNoise(string name, string description) =>
        name.EndsWith("-0000", StringComparison.Ordinal) || NoiseDescriptions.Any(n => description.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<NetAdapter> Adapters()
    {
        var best = DefaultRouteIndex();
        var list = new List<NetAdapter>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsNoise(n.Name, n.Description)) continue;
            int index;
            try { index = n.GetIPProperties().GetIPv4Properties()?.Index ?? n.GetIPProperties().GetIPv6Properties()?.Index ?? -1; }
            catch (NetworkInformationException) { index = -1; }
            list.Add(new NetAdapter(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up, n.Speed, index, index >= 0 && index == best));
        }
        return list.OrderByDescending(a => a.IsDefaultRoute).ThenByDescending(a => a.Up).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static VpnStatus Vpn(IReadOnlyList<NetAdapter> adapters)
    {
        var proton = adapters.Where(a => a.IsProton).OrderByDescending(a => a.Up).FirstOrDefault();
        if (proton is null || !proton.Up)
            return new VpnStatus(VpnLevel.NotConnected, proton?.Name, "Proton VPN isn't connected, so your traffic leaves through your normal connection.");
        if (!proton.IsDefaultRoute)
        {
            var via = adapters.FirstOrDefault(a => a.IsDefaultRoute)?.Name ?? "another adapter";
            return new VpnStatus(VpnLevel.ConnectedNotCarrying, proton.Name, $"Proton VPN is up, but internet traffic is leaving through {via}.");
        }
        return new VpnStatus(VpnLevel.Carrying, proton.Name, "Proton VPN is connected and carrying your internet traffic.");
    }

    /// <summary>Opens a TCP connection and says how long it took. ICMP is useless here: macOS and some firewalls drop pings that work fine for SSH.</summary>
    public static async Task<(bool Open, TimeSpan Took, string? Error)> TcpProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return (true, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, sw.Elapsed, "no answer");
        }
        catch (SocketException ex)
        {
            return (false, sw.Elapsed, ex.SocketErrorCode == SocketError.ConnectionRefused ? "refused (the machine is up, nothing listens on that port)" : ex.SocketErrorCode.ToString());
        }
    }
}
