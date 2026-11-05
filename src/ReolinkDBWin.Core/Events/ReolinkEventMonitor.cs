using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Reolink;

namespace ReolinkDBWin.Core.Events;

/// <summary>
/// Interroge la sonnette Reolink à intervalle régulier et émet un événement à chaque front montant
/// (passage inactif → actif) d'une détection : sonnette, personne, véhicule, animal, colis, mouvement.
/// </summary>
public sealed class ReolinkEventMonitor : EventMonitor
{
    private readonly ReolinkClient _client;
    private readonly int _channel;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, bool> _previous = new(StringComparer.OrdinalIgnoreCase);
    private bool? _getEventsSupported;
    private string? _deviceName;

    public ReolinkEventMonitor(ReolinkClient client, int channel, TimeSpan interval)
    {
        _client = client;
        _channel = channel;
        _interval = interval < TimeSpan.FromMilliseconds(300) ? TimeSpan.FromMilliseconds(300) : interval;
    }

    public override EventSource Source => EventSource.Reolink;

    /// <summary>Libellés actuellement actifs (pour l'affichage).</summary>
    public IReadOnlyDictionary<string, bool> CurrentStates => _previous;

    protected override async Task RunAsync(CancellationToken ct)
    {
        await _client.LoginAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await _client.GetDeviceInfoAsync(ct).ConfigureAwait(false);
            _deviceName = info.Name;
            SetStatus(MonitorState.Connected, $"Connecté à {info}");
        }
        catch (ReolinkApiException)
        {
            SetStatus(MonitorState.Connected, "Connecté");
        }

        // Premier relevé : on initialise l'état sans notifier ce qui est déjà actif.
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var states = await ReadStatesAsync(ct).ConfigureAwait(false);
            foreach (var e in ProcessStates(states, DateTimeOffset.Now, suppress: first)) Raise(e);
            first = false;
            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, bool>> ReadStatesAsync(CancellationToken ct)
    {
        if (_getEventsSupported != false)
        {
            try
            {
                var s = await _client.GetDetectionStatesAsync(_channel, tryGetEvents: true, ct).ConfigureAwait(false);
                _getEventsSupported = true;
                return s;
            }
            catch (ReolinkApiException) when (_getEventsSupported is null)
            {
                // Firmware plus ancien : on bascule définitivement sur GetMdState + GetAiState.
                _getEventsSupported = false;
            }
        }
        return await _client.GetDetectionStatesAsync(_channel, tryGetEvents: false, ct).ConfigureAwait(false);
    }

    /// <summary>Détecte les fronts montants par rapport au relevé précédent.</summary>
    internal IReadOnlyList<DetectionEvent> ProcessStates(IReadOnlyDictionary<string, bool> states, DateTimeOffset now, bool suppress = false)
    {
        var events = new List<DetectionEvent>();
        foreach (var (key, active) in states)
        {
            var wasActive = _previous.TryGetValue(key, out var prev) && prev;
            _previous[key] = active;
            if (suppress || !active || wasActive) continue;
            events.Add(new DetectionEvent
            {
                Source = EventSource.Reolink,
                Type = DetectionTypeInfo.FromLabel(key),
                RawLabel = key,
                Camera = _deviceName,
                Timestamp = now,
            });
        }
        // Le mouvement seul est peu informatif quand une détection IA se déclenche en même temps.
        if (events.Count > 1) events.RemoveAll(e => e.Type == DetectionType.Motion);
        return events;
    }

    protected override async Task OnStoppedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _client.LogoutAsync(cts.Token).ConfigureAwait(false);
    }
}
