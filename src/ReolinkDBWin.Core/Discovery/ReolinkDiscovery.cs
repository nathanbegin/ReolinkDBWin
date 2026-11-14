using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ReolinkDBWin.Core.Net;
using ReolinkDBWin.Core.Reolink;

namespace ReolinkDBWin.Core.Discovery;

public sealed record DiscoveredDevice
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public bool UseHttps { get; init; }
    public string? Name { get; init; }
    public string? Model { get; init; }
    public bool IsReolink { get; init; }
    /// <summary>Comment l'appareil a été trouvé (ONVIF, HTTP).</summary>
    public string Method { get; init; } = "";
    public string? Details { get; init; }
    /// <summary>Adresse du service ONVIF annoncée par l'appareil (WS-Discovery), si connue.</summary>
    public string? OnvifServiceUrl { get; init; }

    public override string ToString() => $"{Host} — {Name ?? Model ?? "Appareil"}";
}

/// <summary>
/// Recherche les sonnettes / caméras Reolink sur le réseau local :
/// 1) WS-Discovery ONVIF (multidiffusion 239.255.255.250:3702) ;
/// 2) balayage des sous-réseaux locaux (/24 max) en sondant l'API Reolink sur les ports 80 et 443.
/// Si des identifiants sont fournis, chaque appareil est interrogé pour obtenir son nom et son modèle.
/// </summary>
public sealed class ReolinkDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int WsDiscoveryPort = 3702;

    public event EventHandler<DiscoveredDevice>? DeviceFound;
    public event EventHandler<string>? Progress;

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(string? username, string? password, bool scanSubnet = true, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, DiscoveredDevice>();

        void Add(DiscoveredDevice d)
        {
            var merged = found.AddOrUpdate(d.Host, d, (_, existing) => Merge(existing, d));
            DeviceFound?.Invoke(this, merged);
        }

        Progress?.Invoke(this, "Recherche ONVIF (WS-Discovery)…");
        var onvif = WsDiscoveryAsync(Add, TimeSpan.FromSeconds(3), ct);
        var scan = scanSubnet ? SubnetScanAsync(Add, ct) : Task.CompletedTask;
        await Task.WhenAll(onvif, scan).ConfigureAwait(false);

        // Enrichissement avec les identifiants (nom, modèle).
        if (!string.IsNullOrEmpty(username))
        {
            Progress?.Invoke(this, "Identification des appareils…");
            var tasks = found.Values.Where(d => d.IsReolink || d.Method.Contains("ONVIF")).Select(async d =>
            {
                var enriched = await IdentifyAsync(d, username, password ?? "", ct).ConfigureAwait(false);
                if (enriched is not null) Add(enriched);
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        Progress?.Invoke(this, $"Terminé : {found.Count} appareil(s) trouvé(s).");
        return found.Values.OrderByDescending(d => d.IsReolink).ThenBy(d => d.Host, HostComparer.Instance).ToList();
    }

    /// <summary>Caméras ONVIF de toutes marques présentes sur le réseau local (WS-Discovery seulement).</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverOnvifAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, DiscoveredDevice>();
        await WsDiscoveryAsync(d => found.AddOrUpdate(d.Host, d, (_, existing) => Merge(existing, d)), duration, ct).ConfigureAwait(false);
        return found.Values.OrderBy(d => d.Host, HostComparer.Instance).ToList();
    }

    /// <summary>Tente une connexion avec les identifiants pour lire le nom et le modèle.</summary>
    public static async Task<DiscoveredDevice?> IdentifyAsync(DiscoveredDevice d, string username, string password, CancellationToken ct)
    {
        var candidates = d.Port > 0 ? [(d.Port, d.UseHttps)] : new[] { (80, false), (443, true) };
        foreach (var (port, https) in candidates)
        {
            using var client = new ReolinkClient(new ReolinkConnectionInfo
            {
                Host = d.Host, Port = port, UseHttps = https, Username = username, Password = password,
            });
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                await client.LoginAsync(cts.Token).ConfigureAwait(false);
                var info = await client.GetDeviceInfoAsync(cts.Token).ConfigureAwait(false);
                await client.LogoutAsync(cts.Token).ConfigureAwait(false);
                return d with
                {
                    Port = port, UseHttps = https, Name = info.Name ?? d.Name, Model = info.Model ?? d.Model,
                    IsReolink = true, Details = info.FirmwareVersion is null ? d.Details : $"Firmware {info.FirmwareVersion}",
                };
            }
            catch (ReolinkApiException ex)
            {
                return d with { Port = port, UseHttps = https, IsReolink = true, Details = ex.Message };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Port suivant
            }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // WS-Discovery (ONVIF)
    // ------------------------------------------------------------------------------------------

    private async Task WsDiscoveryAsync(Action<DiscoveredDevice> add, TimeSpan duration, CancellationToken ct)
    {
        var tasks = new List<Task>();
        foreach (var local in GetLocalIPv4Networks().Select(n => n.Address).DefaultIfEmpty(IPAddress.Any).Distinct())
            tasks.Add(ProbeFromAsync(local, add, duration, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProbeFromAsync(IPAddress local, Action<DiscoveredDevice> add, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            var probe = Encoding.UTF8.GetBytes(BuildProbe());
            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), WsDiscoveryPort);
            await udp.SendAsync(probe, target, ct).ConfigureAwait(false);
            await Task.Delay(150, ct).ConfigureAwait(false);
            await udp.SendAsync(probe, target, ct).ConfigureAwait(false); // UDP : on double l'envoi

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                var device = ParseProbeMatch(Encoding.UTF8.GetString(r.Buffer), r.RemoteEndPoint.Address);
                if (device is not null) add(device);
            }
        }
        catch (SocketException)
        {
            // Interface inutilisable (VPN, etc.) : on ignore.
        }
    }

    internal static string BuildProbe() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
        <e:Header><w:MessageID>uuid:{Guid.NewGuid()}</w:MessageID><w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To><w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>
        <e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>
        </e:Envelope>
        """;

    /// <summary>Analyse une réponse ProbeMatch ONVIF.</summary>
    internal static DiscoveredDevice? ParseProbeMatch(string xml, IPAddress from)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return null; }
        var xaddrs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? "";
        var scopes = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value ?? "";
        if (xaddrs.Length == 0 && scopes.Length == 0) return null;

        string? Scope(string kind)
        {
            var m = Regex.Match(scopes, @"onvif://www\.onvif\.org/" + kind + @"/(\S+)", RegexOptions.IgnoreCase);
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value).Replace('_', ' ') : null;
        }

        var host = from.ToString();
        string? service = null;
        foreach (var addr in xaddrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(addr, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4)
            {
                host = u.Host;
                service = addr;
                break;
            }
        }
        var name = Scope("name");
        var hardware = Scope("hardware");
        var isReolink = scopes.Contains("reolink", StringComparison.OrdinalIgnoreCase)
                        || (hardware?.Contains("reolink", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (name?.Contains("reolink", StringComparison.OrdinalIgnoreCase) ?? false);
        return new DiscoveredDevice
        {
            Host = host,
            Name = name,
            Model = hardware,
            IsReolink = isReolink,
            Method = "ONVIF",
            OnvifServiceUrl = service,
        };
    }

    // ------------------------------------------------------------------------------------------
    // Balayage de sous-réseau
    // ------------------------------------------------------------------------------------------

    private async Task SubnetScanAsync(Action<DiscoveredDevice> add, CancellationToken ct)
    {
        var hosts = GetLocalIPv4Networks().SelectMany(n => EnumerateHosts(n.Address, n.Mask)).Distinct().ToList();
        if (hosts.Count == 0) return;
        Progress?.Invoke(this, $"Balayage de {hosts.Count} adresses…");

        using var handler = HttpHandlerFactory.Create(acceptInvalidCertificate: true);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var done = 0;
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, token) =>
        {
            foreach (var (port, https) in new[] { (80, false), (443, true) })
            {
                if (!await IsPortOpenAsync(ip, port, TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false)) continue;
                if (await LooksLikeReolinkAsync(http, ip, port, https, token).ConfigureAwait(false))
                {
                    add(new DiscoveredDevice { Host = ip.ToString(), Port = port, UseHttps = https, IsReolink = true, Method = "HTTP" });
                    break;
                }
            }
            var n = Interlocked.Increment(ref done);
            if (n % 32 == 0) Progress?.Invoke(this, $"Balayage… {n}/{hosts.Count}");
        }).ConfigureAwait(false);
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Une requête non authentifiée à /cgi-bin/api.cgi?cmd=GetDevInfo renvoie, sur un appareil Reolink,
    /// un JSON du type [{"cmd":"GetDevInfo","code":1,"error":{"detail":"please login first","rspCode":-6}}].
    /// </summary>
    private static async Task<bool> LooksLikeReolinkAsync(HttpClient http, IPAddress ip, int port, bool https, CancellationToken ct)
    {
        try
        {
            var uri = new UriBuilder(https ? "https" : "http", ip.ToString(), port, "/cgi-bin/api.cgi") { Query = "cmd=GetDevInfo" }.Uri;
            using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return IsReolinkApiResponse(body);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsReolinkApiResponse(string body) =>
        body.Contains("\"cmd\"", StringComparison.Ordinal)
        && body.Contains("GetDevInfo", StringComparison.Ordinal)
        && (body.Contains("rspCode", StringComparison.Ordinal) || body.Contains("DevInfo", StringComparison.Ordinal));

    public static IEnumerable<(IPAddress Address, IPAddress Mask)> GetLocalIPv4Networks()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }
        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA
                yield return (ua.Address, ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0"));
            }
        }
    }

    /// <summary>Énumère les hôtes d'un réseau ; limité au /24 contenant l'adresse si le masque est plus large.</summary>
    internal static IEnumerable<IPAddress> EnumerateHosts(IPAddress address, IPAddress mask)
    {
        var a = ToUInt(address);
        var m = ToUInt(mask);
        if (m < 0xFFFFFF00) m = 0xFFFFFF00; // max 254 hôtes
        if (m >= 0xFFFFFFFE) yield break;
        var network = a & m;
        var broadcast = network | ~m;
        for (var ip = network + 1; ip < broadcast; ip++)
        {
            if (ip == a) continue;
            yield return FromUInt(ip);
        }
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    private static DiscoveredDevice Merge(DiscoveredDevice a, DiscoveredDevice b) => a with
    {
        Port = b.Port > 0 ? b.Port : a.Port,
        UseHttps = b.Port > 0 ? b.UseHttps : a.UseHttps,
        Name = b.Name ?? a.Name,
        Model = b.Model ?? a.Model,
        IsReolink = a.IsReolink || b.IsReolink,
        Method = a.Method == b.Method || b.Method.Length == 0 ? a.Method
            : a.Method.Contains(b.Method) ? a.Method : $"{a.Method}+{b.Method}",
        Details = b.Details ?? a.Details,
        OnvifServiceUrl = b.OnvifServiceUrl ?? a.OnvifServiceUrl,
    };

    private sealed class HostComparer : IComparer<string>
    {
        public static readonly HostComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            if (IPAddress.TryParse(x, out var a) && IPAddress.TryParse(y, out var b))
                return ToUInt(a).CompareTo(ToUInt(b));
            return string.CompareOrdinal(x, y);
        }
    }
}
