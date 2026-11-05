using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Net;

namespace ReolinkDBWin.Core.Reolink;

/// <summary>
/// Client minimal de l'API HTTP Reolink (/cgi-bin/api.cgi) : authentification par jeton,
/// informations de l'appareil, états de détection IA / mouvement / sonnette et instantanés.
/// </summary>
public sealed class ReolinkClient : IDisposable
{
    private readonly ReolinkConnectionInfo _info;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly Func<DateTimeOffset> _clock;

    public ReolinkClient(ReolinkConnectionInfo info, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _info = info;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? HttpHandlerFactory.Create(info.AcceptInvalidCertificate), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public ReolinkConnectionInfo ConnectionInfo => _info;

    public bool HasValidToken => _token is not null && _clock() < _tokenExpiry;

    /// <summary>Ouvre une session et mémorise le jeton.</summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasValidToken) return;
            var body = new JsonArray
            {
                new JsonObject
                {
                    ["cmd"] = "Login",
                    ["action"] = 0,
                    ["param"] = new JsonObject
                    {
                        ["User"] = new JsonObject
                        {
                            ["Version"] = "0",
                            ["userName"] = _info.Username,
                            ["password"] = _info.Password,
                        },
                    },
                },
            };
            var response = await PostRawAsync("Login", body, token: null, ct).ConfigureAwait(false);
            var item = FindCommand(response, "Login");
            var value = item["value"]?["Token"] ?? throw new ReolinkApiException("Login", -1, "réponse inattendue");
            _token = value["name"]?.GetValue<string>() ?? throw new ReolinkApiException("Login", -1, "jeton absent");
            var lease = value["leaseTime"]?.GetValue<int>() ?? 3600;
            // On renouvelle un peu avant l'expiration réelle.
            _tokenExpiry = _clock().AddSeconds(Math.Max(30, lease - 60));
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Ferme la session (libère un des rares slots de session de l'appareil).</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (_token is null) return;
        try
        {
            var body = new JsonArray { new JsonObject { ["cmd"] = "Logout", ["action"] = 0, ["param"] = new JsonObject() } };
            await PostRawAsync("Logout", body, _token, ct).ConfigureAwait(false);
        }
        catch
        {
            // Sans importance : la session expirera d'elle-même.
        }
        finally
        {
            _token = null;
            _tokenExpiry = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Exécute une ou plusieurs commandes dans une seule requête. Se reconnecte automatiquement
    /// une fois si la session a expiré. Retourne les éléments de réponse (un par commande).
    /// </summary>
    public async Task<JsonArray> ExecuteAsync(IReadOnlyList<JsonObject> commands, CancellationToken ct = default)
    {
        if (commands.Count == 0) throw new ArgumentException("Aucune commande", nameof(commands));
        for (var attempt = 0; ; attempt++)
        {
            if (!HasValidToken) await LoginAsync(ct).ConfigureAwait(false);
            var body = new JsonArray(commands.Select(c => (JsonNode)c.DeepClone()).ToArray());
            var response = await PostRawAsync(commands[0]["cmd"]!.GetValue<string>(), body, _token, ct).ConfigureAwait(false);
            if (attempt == 0 && response.OfType<JsonObject>().Any(IsLoginRequired))
            {
                InvalidateToken();
                continue;
            }
            return response;
        }
    }

    /// <summary>Exécute une commande unique et retourne sa valeur, ou lève une <see cref="ReolinkApiException"/>.</summary>
    public async Task<JsonNode> ExecuteSingleAsync(string cmd, JsonObject? param = null, CancellationToken ct = default)
    {
        var command = Command(cmd, param);
        var response = await ExecuteAsync([command], ct).ConfigureAwait(false);
        var item = FindCommand(response, cmd);
        return item["value"] ?? new JsonObject();
    }

    public async Task<ReolinkDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var value = await ExecuteSingleAsync("GetDevInfo", null, ct).ConfigureAwait(false);
        return ParseDeviceInfo(value);
    }

    /// <summary>
    /// Lit l'état courant des détections (sonnette, personne, véhicule, animal, colis, mouvement…).
    /// Utilise GetEvents si le firmware le supporte, sinon GetMdState + GetAiState.
    /// La clé est un libellé normalisé (visitor, person, vehicle, animal, package, face, motion…).
    /// </summary>
    public async Task<Dictionary<string, bool>> GetDetectionStatesAsync(int channel, bool tryGetEvents, CancellationToken ct = default)
    {
        var param = new JsonObject { ["channel"] = channel };
        if (tryGetEvents)
        {
            var response = await ExecuteAsync([Command("GetEvents", param)], ct).ConfigureAwait(false);
            var item = response.OfType<JsonObject>().FirstOrDefault(i => (string?)i["cmd"] == "GetEvents");
            if (item is not null && (item["code"]?.GetValue<int>() ?? -1) == 0 && item["value"] is JsonNode v)
            {
                var states = ParseDetectionStates(v);
                if (states.Count > 0) return states;
            }
            throw new ReolinkApiException("GetEvents", ReadRspCode(item) ?? -9, "GetEvents non supporté");
        }

        var batch = await ExecuteAsync([Command("GetMdState", param), Command("GetAiState", param)], ct).ConfigureAwait(false);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch.OfType<JsonObject>())
        {
            if ((item["code"]?.GetValue<int>() ?? -1) != 0 || item["value"] is not JsonNode value) continue;
            foreach (var (k, on) in ParseDetectionStates(value)) result[k] = on;
        }
        if (result.Count == 0)
        {
            var err = batch.OfType<JsonObject>().FirstOrDefault();
            throw new ReolinkApiException("GetAiState", ReadRspCode(err) ?? -1, ReadDetail(err));
        }
        return result;
    }

    /// <summary>Capture une image JPEG du flux principal.</summary>
    public async Task<byte[]> GetSnapshotAsync(int channel, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!HasValidToken) await LoginAsync(ct).ConfigureAwait(false);
            var uri = new UriBuilder(_info.ApiUri)
            {
                Query = $"cmd=Snap&channel={channel}&rs={Guid.NewGuid():N}&token={Uri.EscapeDataString(_token!)}",
            }.Uri;
            using var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return bytes;
            // Pas un JPEG : probablement une erreur JSON (session expirée).
            InvalidateToken();
        }
        throw new ReolinkApiException("Snap", -1, "impossible d'obtenir un instantané");
    }

    public void InvalidateToken()
    {
        _token = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        _http.Dispose();
        _loginLock.Dispose();
    }

    // ----------------------------------------------------------------------------------------
    // Utilitaires (internes pour les tests)
    // ----------------------------------------------------------------------------------------

    internal static JsonObject Command(string cmd, JsonObject? param = null) => new()
    {
        ["cmd"] = cmd,
        ["action"] = 0,
        ["param"] = param?.DeepClone() ?? new JsonObject(),
    };

    private async Task<JsonArray> PostRawAsync(string cmd, JsonArray body, string? token, CancellationToken ct)
    {
        var query = $"cmd={Uri.EscapeDataString(cmd)}";
        if (token is not null) query += $"&token={Uri.EscapeDataString(token)}";
        var uri = new UriBuilder(_info.ApiUri) { Query = query }.Uri;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync(uri, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Reolink {cmd} : HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
        return ParseResponse(cmd, text);
    }

    internal static JsonArray ParseResponse(string cmd, string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ReolinkApiException(cmd, -1, "réponse non JSON : " + ex.Message);
        }
        return node switch
        {
            JsonArray a => a,
            JsonObject o => new JsonArray(o.DeepClone()),
            _ => throw new ReolinkApiException(cmd, -1, "réponse vide"),
        };
    }

    /// <summary>Trouve la réponse d'une commande et lève une exception si elle est en erreur.</summary>
    internal static JsonObject FindCommand(JsonArray response, string cmd)
    {
        var item = response.OfType<JsonObject>().FirstOrDefault(i => string.Equals((string?)i["cmd"], cmd, StringComparison.OrdinalIgnoreCase))
                   ?? response.OfType<JsonObject>().FirstOrDefault()
                   ?? throw new ReolinkApiException(cmd, -1, "réponse vide");
        var code = item["code"]?.GetValue<int>() ?? -1;
        if (code != 0) throw new ReolinkApiException(cmd, ReadRspCode(item) ?? code, ReadDetail(item));
        return item;
    }

    private static bool IsLoginRequired(JsonObject item) => ReadRspCode(item) == -6;

    private static int? ReadRspCode(JsonObject? item)
    {
        var n = item?["error"]?["rspCode"];
        if (n is null) return null;
        try { return n.GetValue<int>(); } catch { return int.TryParse(n.ToString(), out var v) ? v : null; }
    }

    private static string? ReadDetail(JsonObject? item) => item?["error"]?["detail"]?.ToString();

    internal static ReolinkDeviceInfo ParseDeviceInfo(JsonNode value)
    {
        var d = value["DevInfo"] ?? value;
        return new ReolinkDeviceInfo
        {
            Name = d["name"]?.ToString(),
            Model = d["model"]?.ToString(),
            Type = d["type"]?.ToString(),
            FirmwareVersion = d["firmVer"]?.ToString(),
            HardwareVersion = d["hardVer"]?.ToString(),
            Serial = d["serial"]?.ToString() ?? d["uid"]?.ToString(),
            ChannelCount = int.TryParse(d["channelNum"]?.ToString(), out var c) && c > 0 ? c : 1,
        };
    }

    /// <summary>
    /// Analyse les réponses GetMdState ({"state":1}), GetAiState ({"people":{"alarm_state":1,"support":1},…})
    /// et GetEvents ({"ai":{…},"md":{…},"visitor":{…}}) en un dictionnaire libellé normalisé → actif.
    /// </summary>
    internal static Dictionary<string, bool> ParseDetectionStates(JsonNode value)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (value is not JsonObject obj) return result;

        // GetMdState : {"state": 0|1}
        if (obj["state"] is JsonValue state && TryInt(state, out var mdState) && !obj.ContainsKey("alarm_state"))
            result["motion"] = mdState == 1;

        Walk(obj, result, depth: 0);
        return result;

        static void Walk(JsonObject o, Dictionary<string, bool> acc, int depth)
        {
            if (depth > 3) return;
            foreach (var (key, child) in o)
            {
                if (child is not JsonObject c) continue;
                if (c["alarm_state"] is JsonValue alarm && TryInt(alarm, out var on))
                {
                    // « support » = 0 : la fonction n'existe pas sur l'appareil, on l'ignore.
                    if (c["support"] is JsonValue sup && TryInt(sup, out var s) && s == 0) continue;
                    acc[NormalizeKey(key)] = on == 1;
                }
                else
                {
                    Walk(c, acc, depth + 1);
                }
            }
        }
    }

    internal static string NormalizeKey(string key) => key.ToLowerInvariant() switch
    {
        "md" or "motion" => "motion",
        "people" or "person" => "person",
        "dog_cat" or "pet" => "animal",
        "vehicle" or "car" => "vehicle",
        "visitor" => "visitor",
        "package" => "package",
        "face" => "face",
        var k => k,
    };

    private static bool TryInt(JsonValue v, out int result)
    {
        if (v.TryGetValue(out int i)) { result = i; return true; }
        if (v.TryGetValue(out bool b)) { result = b ? 1 : 0; return true; }
        if (v.TryGetValue(out string? s) && int.TryParse(s, out i)) { result = i; return true; }
        result = 0;
        return false;
    }
}
