using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Net;

namespace ReolinkDBWin.Core.HomeAssistant;

public sealed record HomeAssistantEntity(string EntityId, string FriendlyName, string State, string? DeviceClass);

/// <summary>Accès REST minimal à Home Assistant (liste des entités, test de connexion).</summary>
public sealed class HomeAssistantClient : IDisposable
{
    private readonly HttpClient _http;

    public HomeAssistantClient(string baseUrl, string accessToken, bool acceptInvalidCertificate, HttpMessageHandler? handler = null)
    {
        BaseUri = NormalizeBase(baseUrl);
        _http = new HttpClient(handler ?? HttpHandlerFactory.Create(acceptInvalidCertificate), disposeHandler: true)
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public Uri BaseUri { get; }

    public static Uri NormalizeBase(string baseUrl)
    {
        var url = baseUrl.Trim();
        if (!url.Contains("://")) url = "http://" + url;
        if (!url.EndsWith('/')) url += "/";
        return new Uri(url);
    }

    public static Uri WebSocketUri(string baseUrl)
    {
        var b = new UriBuilder(NormalizeBase(baseUrl));
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        if (b.Port == 443 && b.Scheme == "wss" || b.Port == 80 && b.Scheme == "ws") b.Port = -1;
        b.Path = b.Path.TrimEnd('/') + "/api/websocket";
        return b.Uri;
    }

    /// <summary>Vérifie l'accès et retourne le message de l'API (« API running. »).</summary>
    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/", ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Home Assistant : jeton d'accès invalide.");
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json?["message"]?.ToString() ?? "OK";
    }

    /// <summary>Liste les binary_sensor (détections, sonnette) et event (Reolink récent) de Home Assistant.</summary>
    public async Task<IReadOnlyList<HomeAssistantEntity>> GetDetectionEntitiesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/states", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseEntities(text);
    }

    internal static IReadOnlyList<HomeAssistantEntity> ParseEntities(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray arr) return [];
        var list = new List<HomeAssistantEntity>();
        foreach (var s in arr.OfType<JsonObject>())
        {
            var id = s["entity_id"]?.ToString();
            if (id is null || !(id.StartsWith("binary_sensor.") || id.StartsWith("event."))) continue;
            var attrs = s["attributes"];
            list.Add(new HomeAssistantEntity(id, attrs?["friendly_name"]?.ToString() ?? id, s["state"]?.ToString() ?? "", attrs?["device_class"]?.ToString()));
        }
        return list.OrderBy(e => e.EntityId).ToList();
    }

    public void Dispose() => _http.Dispose();
}
