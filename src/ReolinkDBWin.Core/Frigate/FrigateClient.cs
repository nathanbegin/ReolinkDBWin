using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Net;

namespace ReolinkDBWin.Core.Frigate;

public sealed record FrigateConnectionInfo
{
    public required string BaseUrl { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public bool AcceptInvalidCertificate { get; init; } = true;
}

public sealed record FrigateEventQuery
{
    public IReadOnlyList<string> Cameras { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public DateTimeOffset? After { get; init; }
    public DateTimeOffset? Before { get; init; }
    public int Limit { get; init; } = 50;
    public bool? HasClip { get; init; }
}

/// <summary>
/// Client de l'API REST de Frigate (0.12 à 0.16+). Gère l'authentification par cookie
/// (frigate_token) de Frigate 0.14+ sur le port 8971, ou l'accès direct sans authentification (port 5000).
/// </summary>
public sealed class FrigateClient : IDisposable
{
    private readonly FrigateConnectionInfo _info;
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public FrigateClient(FrigateConnectionInfo info, HttpMessageHandler? handler = null)
    {
        _info = info;
        var url = info.BaseUrl.Trim();
        if (!url.Contains("://")) url = "http://" + url;
        if (!url.EndsWith('/')) url += "/";
        _base = new Uri(url);
        _http = new HttpClient(handler ?? HttpHandlerFactory.Create(info.AcceptInvalidCertificate, new CookieContainer()), disposeHandler: true)
        {
            BaseAddress = _base,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public Uri BaseUri => _base;
    public bool RequiresAuthentication => !string.IsNullOrEmpty(_info.Username);

    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (!RequiresAuthentication) return;
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/login", new { user = _info.Username, password = _info.Password }, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                throw new FrigateException("Frigate : nom d'utilisateur ou mot de passe invalide.");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new FrigateException("Frigate : trop de tentatives de connexion, réessayez plus tard.");
            resp.EnsureSuccessStatusCode();
            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/version"), ct).ConfigureAwait(false);
        return (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"');
    }

    public async Task<IReadOnlyList<string>> GetCamerasAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/config"), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json?["cameras"] is JsonObject cams ? cams.Select(c => c.Key).OrderBy(c => c).ToList() : [];
    }

    public async Task<IReadOnlyList<string>> GetLabelsAsync(string? camera = null, CancellationToken ct = default)
    {
        var path = "api/labels" + (string.IsNullOrEmpty(camera) ? "" : "?camera=" + Uri.EscapeDataString(camera));
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json is JsonArray a ? a.Select(x => x?.ToString()).OfType<string>().OrderBy(x => x).ToList() : [];
    }

    public async Task<IReadOnlyList<FrigateEvent>> GetEventsAsync(FrigateEventQuery query, CancellationToken ct = default)
    {
        var path = "api/events?" + BuildEventQuery(query);
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return FrigateEvent.ParseList(text);
    }

    public async Task<FrigateEvent?> GetEventAsync(string eventId, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/events/{Uri.EscapeDataString(eventId)}"), ct).ConfigureAwait(false);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return node is JsonObject o ? FrigateEvent.Parse(o) : null;
    }

    public Task<byte[]> GetThumbnailAsync(string eventId, CancellationToken ct = default) =>
        GetBytesAsync($"api/events/{Uri.EscapeDataString(eventId)}/thumbnail.jpg", ct);

    public Task<byte[]> GetSnapshotAsync(string eventId, CancellationToken ct = default) =>
        GetBytesAsync($"api/events/{Uri.EscapeDataString(eventId)}/snapshot.jpg?bbox=1", ct);

    public Task<byte[]> GetLatestCameraImageAsync(string camera, CancellationToken ct = default) =>
        GetBytesAsync($"api/{Uri.EscapeDataString(camera)}/latest.jpg?h=720", ct);

    public Uri GetClipUri(string eventId) => new(_base, $"api/events/{Uri.EscapeDataString(eventId)}/clip.mp4");

    /// <summary>Télécharge le clip MP4 d'un événement.</summary>
    public async Task DownloadClipAsync(string eventId, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = $"api/events/{Uri.EscapeDataString(eventId)}/clip.mp4";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress?.Report((double)read / total.Value);
        }
        if (read == 0) throw new FrigateException("Le clip est vide (l'événement est peut-être encore en cours ou les enregistrements sont désactivés).");
        progress?.Report(1);
    }

    public void Dispose()
    {
        _http.Dispose();
        _loginLock.Dispose();
    }

    internal static string BuildEventQuery(FrigateEventQuery q)
    {
        var parts = new List<string>();
        if (q.Cameras.Count > 0) parts.Add("cameras=" + Uri.EscapeDataString(string.Join(',', q.Cameras)));
        if (q.Labels.Count > 0) parts.Add("labels=" + Uri.EscapeDataString(string.Join(',', q.Labels)));
        if (q.After is { } after) parts.Add("after=" + (after.ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture));
        if (q.Before is { } before) parts.Add("before=" + (before.ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture));
        if (q.HasClip is { } hasClip) parts.Add("has_clip=" + (hasClip ? "1" : "0"));
        parts.Add("limit=" + Math.Clamp(q.Limit, 1, 500).ToString(CultureInfo.InvariantCulture));
        parts.Add("include_thumbnails=0");
        return string.Join('&', parts);
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Envoie une requête ; en cas de 401 avec identifiants, se reconnecte et réessaie une fois.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (RequiresAuthentication && !_loggedIn) await LoginAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var req = factory();
            var resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                if (!RequiresAuthentication)
                    throw new FrigateException("Frigate exige une authentification : indiquez un utilisateur et un mot de passe (port 8971), ou utilisez le port interne 5000.");
                _loggedIn = false;
                await LoginAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                throw new FrigateException("Ressource introuvable sur Frigate (404).");
            }
            if (!resp.IsSuccessStatusCode)
            {
                var status = resp.StatusCode;
                resp.Dispose();
                throw new FrigateException($"Frigate a répondu HTTP {(int)status} {status}.");
            }
            return resp;
        }
    }
}

public sealed class FrigateException(string message) : Exception(message);
