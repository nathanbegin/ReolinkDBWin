using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ReolinkDBWin.Core.Net;

namespace ReolinkDBWin.Core.Cameras;

/// <summary>Un profil vidéo ONVIF et son adresse RTSP.</summary>
public sealed record OnvifProfile(string Token, string Name, int Width, int Height, string Encoding, string StreamUri)
{
    public long Pixels => (long)Width * Height;
    public override string ToString() => $"{Name} — {Width}×{Height} {Encoding}";
}

/// <summary>
/// Client ONVIF minimal (profil S) : demande à la caméra ses profils vidéo et leurs adresses RTSP.
/// Authentification WS-Security « UsernameToken » avec mot de passe haché (digest).
/// </summary>
public sealed class OnvifClient : IDisposable
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private readonly HttpClient _http;
    private readonly Uri _device;
    private readonly string _user;
    private readonly string _password;

    public OnvifClient(string host, int port, string? user, string? password, HttpMessageHandler? handler = null)
        : this(new UriBuilder("http", host.Trim(), port > 0 ? port : 80, "/onvif/device_service").Uri, user, password, handler)
    {
    }

    public OnvifClient(Uri deviceService, string? user, string? password, HttpMessageHandler? handler = null)
    {
        _device = deviceService;
        _user = user?.Trim() ?? "";
        _password = password ?? "";
        _http = new HttpClient(handler ?? HttpHandlerFactory.Create(acceptInvalidCertificate: true), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
    }

    /// <summary>Profils vidéo de la caméra, du plus grand au plus petit, avec leur adresse RTSP (sans identifiants).</summary>
    public async Task<IReadOnlyList<OnvifProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        var media = await GetMediaServiceAsync(ct).ConfigureAwait(false);
        var profilesXml = await SendAsync(media, "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
            "<trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"/>", ct).ConfigureAwait(false);

        var result = new List<OnvifProfile>();
        foreach (var p in profilesXml.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = p.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token)) continue;
            var encoder = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration");
            if (encoder is null) continue;
            var resolution = encoder.Descendants().FirstOrDefault(e => e.Name.LocalName == "Resolution");
            int.TryParse(resolution?.Elements().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value, out var w);
            int.TryParse(resolution?.Elements().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value, out var h);
            var encoding = encoder.Elements().FirstOrDefault(e => e.Name.LocalName == "Encoding")?.Value ?? "";
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? token;

            var uriXml = await SendAsync(media, "http://www.onvif.org/ver10/media/wsdl/GetStreamUri",
                "<trt:GetStreamUri xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">" +
                "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
                $"<trt:ProfileToken>{System.Security.SecurityElement.Escape(token)}</trt:ProfileToken></trt:GetStreamUri>", ct).ConfigureAwait(false);
            var uri = uriXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value?.Trim();
            if (string.IsNullOrEmpty(uri)) continue;
            result.Add(new OnvifProfile(token, name, w, h, encoding, uri));
        }
        return result.OrderByDescending(p => p.Pixels).ToList();
    }

    /// <summary>Ajoute les identifiants à une adresse RTSP renvoyée par la caméra.</summary>
    public static string WithCredentials(string rtspUri, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user) || !Uri.TryCreate(rtspUri, UriKind.Absolute, out var uri)) return rtspUri;
        var cred = $"{Uri.EscapeDataString(user.Trim())}:{Uri.EscapeDataString(password ?? "")}@";
        var schemeEnd = rtspUri.IndexOf("://", StringComparison.Ordinal) + 3;
        var rest = rtspUri[schemeEnd..];
        if (!string.IsNullOrEmpty(uri.UserInfo)) rest = rest[(rest.IndexOf('@') + 1)..];
        return rtspUri[..schemeEnd] + cred + rest;
    }

    private async Task<Uri> GetMediaServiceAsync(CancellationToken ct)
    {
        var xml = await SendAsync(_device, "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
            "<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Category>Media</tds:Category></tds:GetCapabilities>", ct).ConfigureAwait(false);
        var xaddr = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media")?
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value?.Trim();
        if (!Uri.TryCreate(xaddr, UriKind.Absolute, out var media))
            return new UriBuilder(_device) { Path = "/onvif/media_service" }.Uri;
        // Certaines caméras annoncent une adresse interne (autre IP / port) : on garde l'hôte qu'on a su joindre.
        return new UriBuilder(media) { Host = _device.Host, Port = _device.Port }.Uri;
    }

    private async Task<XDocument> SendAsync(Uri service, string action, string body, CancellationToken ct)
    {
        var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                       "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
                       (_user.Length > 0 ? "<s:Header>" + BuildSecurityHeader(_user, _password, DateTime.UtcNow, RandomNumberGenerator.GetBytes(16)) + "</s:Header>" : "") +
                       $"<s:Body>{body}</s:Body></s:Envelope>";
        using var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{action}\"");
        using var resp = await _http.PostAsync(service, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        XDocument? doc = null;
        try { doc = XDocument.Parse(text); } catch { /* pas du XML */ }
        var fault = doc?.Descendants(Soap + "Fault").FirstOrDefault();
        if (fault is not null || !resp.IsSuccessStatusCode)
        {
            var reason = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value;
            var unauthorized = (int)resp.StatusCode == 401 || (fault?.Value.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ?? false)
                               || (reason?.Contains("auth", StringComparison.OrdinalIgnoreCase) ?? false);
            throw new OnvifException(unauthorized
                ? "ONVIF : identifiants refusés (vérifiez l'utilisateur, le mot de passe et que l'heure de la caméra est juste)."
                : $"ONVIF : {reason ?? $"HTTP {(int)resp.StatusCode}"}");
        }
        return doc ?? throw new OnvifException("ONVIF : réponse illisible (ONVIF est-il activé sur cette caméra, sur ce port ?).");
    }

    /// <summary>En-tête WS-Security : digest = Base64(SHA1(nonce + date + mot de passe)).</summary>
    internal static string BuildSecurityHeader(string user, string password, DateTime utcNow, byte[] nonce)
    {
        var created = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var digest = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created), .. Encoding.UTF8.GetBytes(password)]));
        return "<wsse:Security s:mustUnderstand=\"1\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" " +
               "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
               $"<wsse:UsernameToken><wsse:Username>{System.Security.SecurityElement.Escape(user)}</wsse:Username>" +
               $"<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</wsse:Password>" +
               $"<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{Convert.ToBase64String(nonce)}</wsse:Nonce>" +
               $"<wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>";
    }

    public void Dispose() => _http.Dispose();
}

public sealed class OnvifException(string message) : Exception(message);
