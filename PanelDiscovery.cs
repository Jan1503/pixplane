using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PixPlane;

/// <summary>
///     One W6300 / RP2350 LED panel found on the LAN (UDP 7778 announce or HTTP /status).
/// </summary>
public sealed class PanelAnnouncement
{
    public const string KindValue = "verpixeld-panel";

    public string Kind { get; init; } = KindValue;
    public string Id { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Host { get; init; } = "";
    public int UdpPort { get; init; } = 7777;
    public int WebPort { get; init; } = 5000;
    public int Width { get; init; }
    public int Height { get; init; }
    public int ColorBits { get; init; }
    public string Name { get; init; } = "";
    public string MdnsHost { get; init; } = "";
    public string Version { get; init; } = "";
    public string Via { get; init; } = "";
    public DateTime SeenUtc { get; init; } = DateTime.UtcNow;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(MdnsHost) ? Host : MdnsHost) : Name;
}

/// <summary>
///     Discovers RP2350 + W6300 panels: UDP probe/announce on port 7778, optional HTTP /status fallback.
/// </summary>
public static class PanelDiscovery
{
    public const int DiscoveryPort = 7778;
    public const int DefaultWebPort = 5000;
    public static ReadOnlySpan<byte> Magic => "VPXD"u8;
    public const byte TypeProbe = 0x01;
    public const byte TypeAnnounce = 0x02;

    public static async Task IdentifyAsync(string host, int webPort = DefaultWebPort,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var basePath = $"http://{host}:{webPort}/cmd?c=";
        await http.GetAsync(basePath + Uri.EscapeDataString("t f 8191"), ct).ConfigureAwait(false);
        try
        {
            await Task.Delay(800, ct).ConfigureAwait(false);
            await http.GetAsync(basePath + Uri.EscapeDataString("t n"), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* panel may drop the second request while leaving test mode */ }
    }

    /// <summary>GET <c>/status</c> from a known panel. Returns null if it does not answer.</summary>
    public static async Task<PanelAnnouncement?> QueryStatusAsync(string host, int webPort = DefaultWebPort,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var resp = await http.GetAsync($"http://{host}:{webPort}/status", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseStatusJson(body, host, "http", out var p) ? p : null;
        }
        catch { return null; }
    }

    public static async Task<IReadOnlyList<PanelAnnouncement>> ScanAsync(
        TimeSpan? timeout = null, bool httpFallback = true, CancellationToken ct = default)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(2);
        var found = new ConcurrentDictionary<string, PanelAnnouncement>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var listen = ListenUdpAsync(udp, found, wait, ct);
        SendProbes(udp);
        await listen.ConfigureAwait(false);

        if (httpFallback && found.IsEmpty)
            await HttpScanAsync(found, wait, ct).ConfigureAwait(false);

        return found.Values.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Find the panel with this chip-id: last known IP /status, then mDNS
    ///     <c>panel-xxxxxx.local</c>, then a UDP scan.
    /// </summary>
    public static async Task<PanelAnnouncement?> ResolveAsync(
        string panelId, string? lastHost = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = NormalizeId(panelId);
        if (string.IsNullOrEmpty(id)) return null;
        var wait = timeout ?? TimeSpan.FromSeconds(2);

        if (!string.IsNullOrWhiteSpace(lastHost))
        {
            var viaHttp = await TryHttpStatusAsync(lastHost.Trim(), id, ct).ConfigureAwait(false);
            if (viaHttp != null) return viaHttp;
        }

        var mdns = MdnsHostFromId(id);
        if (mdns != null)
        {
            var ip = await TryMdnsAsync(mdns, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ip))
            {
                var viaMdns = await TryHttpStatusAsync(ip, id, ct).ConfigureAwait(false);
                if (viaMdns != null) return viaMdns;
            }
        }

        var scanned = await ScanAsync(wait, httpFallback: true, ct).ConfigureAwait(false);
        return scanned.FirstOrDefault(p => NormalizeId(p.Id) == id);
    }

    /// <summary>mDNS label <c>panel-aabbcc</c> from the last 3 MAC bytes (= unique-id bytes 3..5).</summary>
    public static string? MdnsHostFromId(string panelId)
    {
        var hex = NormalizeId(panelId);
        return hex.Length >= 12 ? "panel-" + hex.Substring(6, 6) : null;
    }

    public static string NormalizeId(string? panelId)
    {
        if (string.IsNullOrWhiteSpace(panelId)) return "";
        Span<char> buf = stackalloc char[panelId.Length];
        var n = 0;
        foreach (var c in panelId)
        {
            if (c is ':' or '-' or ' ') continue;
            buf[n++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..n]);
    }

    public static bool TryParseAnnounce(ReadOnlySpan<byte> buf, string via, out PanelAnnouncement panel)
    {
        panel = null!;
        // magic(4)+type(1)+mac(6)+ip(4)+udp(2)+web(2)+w(2)+h(2)+bits(1)+name(16)+ver(8)+uid(8) = 56
        if (buf.Length < 56) return false;
        if (buf[0] != (byte)'V' || buf[1] != (byte)'P' || buf[2] != (byte)'X' || buf[3] != (byte)'D')
            return false;
        if (buf[4] != TypeAnnounce) return false;

        var mac = buf.Slice(5, 6);
        var ip = buf.Slice(11, 4);
        var udp = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(15, 2));
        var web = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(17, 2));
        var w = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(19, 2));
        var h = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(21, 2));
        var bits = buf[23];
        var name = AsciiZ(buf.Slice(24, 16));
        var ver = AsciiZ(buf.Slice(40, 8));
        var id = Convert.ToHexString(buf.Slice(48, 8).ToArray()).ToLowerInvariant();
        var host = $"{ip[0]}.{ip[1]}.{ip[2]}.{ip[3]}";
        var mdns = $"panel-{mac[3]:x2}{mac[4]:x2}{mac[5]:x2}";
        panel = new PanelAnnouncement
        {
            Id = id,
            Mac = $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}",
            Host = host,
            UdpPort = udp,
            WebPort = web,
            Width = w,
            Height = h,
            ColorBits = bits,
            Name = name,
            MdnsHost = mdns,
            Version = ver,
            Via = via
        };
        return true;
    }

    public static bool TryParseStatusJson(string json, string host, string via, out PanelAnnouncement panel)
    {
        panel = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var kind = Str(r, "kind");
            if (!string.IsNullOrEmpty(kind) &&
                !kind.Equals(PanelAnnouncement.KindValue, StringComparison.OrdinalIgnoreCase) &&
                !r.TryGetProperty("gclk", out _))
                return false;
            if (string.IsNullOrEmpty(kind) && !r.TryGetProperty("gclk", out _) && !r.TryGetProperty("ver", out _))
                return false;

            var ip = Str(r, "ip");
            if (string.IsNullOrEmpty(ip)) ip = host;
            panel = new PanelAnnouncement
            {
                Kind = string.IsNullOrEmpty(kind) ? PanelAnnouncement.KindValue : kind,
                Id = Str(r, "id"),
                Mac = Str(r, "mac"),
                Host = ip,
                UdpPort = Int(r, "udp", 7777),
                WebPort = Int(r, "web", DefaultWebPort),
                Width = Int(r, "w", 0),
                Height = Int(r, "h", 0),
                ColorBits = Int(r, "bits", Int(r, "gclk", 0)),
                Name = Str(r, "name"),
                MdnsHost = Str(r, "host"),
                Version = Str(r, "ver"),
                Via = via
            };
            return !string.IsNullOrEmpty(panel.Host);
        }
        catch { return false; }
    }

    private static async Task ListenUdpAsync(UdpClient udp, ConcurrentDictionary<string, PanelAnnouncement> found,
        TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (TryParseAnnounce(r.Buffer, "udp", out var p))
                    found[Key(p)] = p;
            }
        }
        catch (OperationCanceledException) { /* timeout */ }
        catch (SocketException) { /* closed */ }
    }

    private static void SendProbes(UdpClient udp)
    {
        var probe = new byte[] { (byte)'V', (byte)'P', (byte)'X', (byte)'D', TypeProbe };
        var dests = new HashSet<IPEndPoint>();
        dests.Add(new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bcast = DirectedBroadcast(ua.Address, ua.IPv4Mask);
                    if (bcast != null) dests.Add(new IPEndPoint(bcast, DiscoveryPort));
                }
            }
        }
        catch { /* ignore */ }

        foreach (var ep in dests)
        {
            try { udp.Send(probe, probe.Length, ep); }
            catch { /* interface may not allow broadcast */ }
        }
    }

    private static async Task HttpScanAsync(ConcurrentDictionary<string, PanelAnnouncement> found,
        TimeSpan wait, CancellationToken ct)
    {
        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask?.GetAddressBytes();
                    if (mask is not { Length: 4 }) continue;
                    // Only scan typical /24s so we don't hammer a /16.
                    if (mask[0] != 255 || mask[1] != 255 || mask[2] != 255) continue;
                    var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
                    for (var i = 1; i <= 254; i++) ips.Add(prefix + i);
                }
            }
        }
        catch { return; }

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            await Parallel.ForEachAsync(ips, new ParallelOptions
            {
                MaxDegreeOfParallelism = 32,
                CancellationToken = cts.Token
            }, async (ip, token) =>
            {
                try
                {
                    using var resp = await http.GetAsync($"http://{ip}:{DefaultWebPort}/status", token)
                        .ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return;
                    var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (TryParseStatusJson(body, ip, "http", out var p))
                        found.TryAdd(Key(p), p);
                }
                catch { /* timeout / not a panel */ }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* scan window elapsed */ }
    }

    private static async Task<PanelAnnouncement?> TryHttpStatusAsync(string host, string wantId,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            using var resp = await http.GetAsync($"http://{host}:{DefaultWebPort}/status", ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!TryParseStatusJson(body, host, "http", out var p)) return null;
            return NormalizeId(p.Id) == wantId ? p : null;
        }
        catch { return null; }
    }

    private static async Task<string?> TryMdnsAsync(string hostLabel, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var dns = Dns.GetHostAddressesAsync(hostLabel + ".local");
            var done = await Task.WhenAny(dns, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done != dns) return null;
            var addrs = await dns.ConfigureAwait(false);
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { return null; }
    }

    private static string Key(PanelAnnouncement p) =>
        !string.IsNullOrEmpty(p.Id) ? "id:" + p.Id : "ip:" + p.Host;

    private static string AsciiZ(ReadOnlySpan<byte> s)
    {
        var z = s.IndexOf((byte)0);
        if (z < 0) z = s.Length;
        return Encoding.ASCII.GetString(s[..z]);
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static int Int(JsonElement r, string name, int fallback)
    {
        if (!r.TryGetProperty(name, out var e)) return fallback;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out n)) return n;
        return fallback;
    }

    private static IPAddress? DirectedBroadcast(IPAddress addr, IPAddress? mask)
    {
        if (mask == null) return null;
        var a = addr.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)~m[i]);
        return new IPAddress(b);
    }
}
