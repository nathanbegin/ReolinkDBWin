using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ReolinkDBWin.Core.HomeAssistant;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Events;

/// <summary>
/// Écoute l'API WebSocket de Home Assistant (événements state_changed) et émet une détection quand
/// une des entités surveillées passe à « on » (binary_sensor) ou se déclenche (entité event.*).
/// </summary>
public sealed class HomeAssistantEventMonitor : EventMonitor
{
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly bool _acceptInvalidCertificate;
    private readonly HashSet<string> _entities;

    public HomeAssistantEventMonitor(string baseUrl, string token, IEnumerable<string> entityIds, bool acceptInvalidCertificate)
    {
        _baseUrl = baseUrl;
        _token = token;
        _acceptInvalidCertificate = acceptInvalidCertificate;
        _entities = new HashSet<string>(entityIds.Select(e => e.Trim()).Where(e => e.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    public override EventSource Source => EventSource.HomeAssistant;

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (_entities.Count == 0)
        {
            SetStatus(MonitorState.Error, "Aucune entité Home Assistant sélectionnée");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (_acceptInvalidCertificate)
            ws.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(HomeAssistantClient.WebSocketUri(_baseUrl), connectCts.Token).ConfigureAwait(false);

        // 1. auth_required → auth → auth_ok
        var hello = await ReceiveAsync(ws, ct).ConfigureAwait(false);
        if ((string?)hello?["type"] == "auth_required")
        {
            await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = _token }, ct).ConfigureAwait(false);
            var auth = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if ((string?)auth?["type"] != "auth_ok")
                throw new InvalidOperationException("Home Assistant : authentification refusée (" + (auth?["message"]?.ToString() ?? "jeton invalide") + ")");
        }

        // 2. Abonnement aux changements d'état
        await SendAsync(ws, new JsonObject { ["id"] = 1, ["type"] = "subscribe_events", ["event_type"] = "state_changed" }, ct).ConfigureAwait(false);
        SetStatus(MonitorState.Connected, $"Connecté à Home Assistant ({_entities.Count} entité(s))");

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) break;
            if ((string?)msg["type"] == "result" && msg["success"]?.GetValue<bool>() == false)
                throw new InvalidOperationException("Home Assistant : abonnement refusé — " + msg["error"]?["message"]);
            var e = ParseStateChanged(msg, _entities, DateTimeOffset.Now);
            if (e is not null) Raise(e);
        }
        throw new WebSocketException("Connexion Home Assistant fermée");
    }

    /// <summary>Transforme un message state_changed en détection si pertinent.</summary>
    internal static DetectionEvent? ParseStateChanged(JsonNode msg, ISet<string> entities, DateTimeOffset now)
    {
        if ((string?)msg["type"] != "event") return null;
        var data = msg["event"]?["data"];
        var entityId = data?["entity_id"]?.ToString();
        if (entityId is null || !entities.Contains(entityId)) return null;

        var newState = data?["new_state"]?["state"]?.ToString();
        var oldState = data?["old_state"]?["state"]?.ToString();
        if (newState is null || newState is "unavailable" or "unknown") return null;

        bool triggered;
        if (entityId.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
            triggered = newState != oldState; // l'état d'une entité event est l'horodatage du dernier déclenchement
        else
            triggered = newState == "on" && oldState != "on";
        if (!triggered) return null;

        var attrs = data?["new_state"]?["attributes"];
        var friendly = attrs?["friendly_name"]?.ToString() ?? entityId;
        var type = DetectionTypeInfo.FromEntityId(entityId);
        if (type == DetectionType.Other && attrs?["device_class"]?.ToString() is { } dc)
        {
            type = dc switch
            {
                "doorbell" => DetectionType.Doorbell,
                "occupancy" or "presence" => DetectionType.Person,
                "motion" => DetectionType.Motion,
                _ => DetectionType.Other,
            };
        }
        return new DetectionEvent
        {
            Source = EventSource.HomeAssistant,
            Type = type,
            RawLabel = friendly,
            Camera = friendly,
            Timestamp = now,
        };
    }

    private static async Task SendAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray());
    }
}
