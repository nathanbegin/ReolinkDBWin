using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Events;

/// <summary>Interroge Frigate pour les nouveaux événements (objets détectés) de la caméra de la sonnette.</summary>
public sealed class FrigateEventMonitor : EventMonitor
{
    private readonly FrigateClient _client;
    private readonly IReadOnlyList<string> _cameras;
    private readonly IReadOnlyList<string> _labels;
    private readonly TimeSpan _interval;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();
    private HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    public FrigateEventMonitor(FrigateClient client, string? camera, IReadOnlyList<string> labels, TimeSpan interval)
        : this(client, string.IsNullOrWhiteSpace(camera) ? [] : [camera], labels, interval)
    {
    }

    /// <param name="cameras">Caméras surveillées (vide = toutes).</param>
    public FrigateEventMonitor(FrigateClient client, IReadOnlyList<string> cameras, IReadOnlyList<string> labels, TimeSpan interval)
    {
        _client = client;
        _cameras = cameras;
        _labels = labels;
        _interval = interval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : interval;
    }

    public override EventSource Source => EventSource.Frigate;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
        SetStatus(MonitorState.Connected, $"Connecté à Frigate {version}" + (_cameras.Count == 0 ? "" : $" ({string.Join(", ", _cameras)})"));

        // Premier relevé : on mémorise les événements existants sans notifier.
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            // Fenêtre glissante : tolère un décalage d'horloge entre ce PC et le serveur Frigate.
            var events = await _client.GetEventsAsync(new FrigateEventQuery
            {
                Cameras = _cameras,
                Labels = _labels,
                After = DateTimeOffset.Now.AddMinutes(-15),
                Limit = 25,
            }, ct).ConfigureAwait(false);

            foreach (var e in Process(events, suppress: first))
            {
                byte[]? image = null;
                try { image = await _client.GetThumbnailAsync(e.FrigateEventId!, ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* image facultative */ }
                Raise(e with { Image = image });
            }
            first = false;
            var active = ActiveCameras(events);
            if (!active.SetEquals(_active))
            {
                _active = active;
                RaiseActiveCameras(active.ToList());
            }
            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Caméras ayant au moins un événement en cours (pas encore terminé, récent).</summary>
    internal static HashSet<string> ActiveCameras(IEnumerable<FrigateEvent> events)
    {
        var limit = DateTimeOffset.Now.AddMinutes(-10); // garde-fou contre un événement resté ouvert
        return events.Where(e => e.InProgress && e.StartTime > limit).Select(e => e.Camera)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Retient les événements jamais vus. <paramref name="suppress"/> : mémoriser sans notifier.</summary>
    internal IReadOnlyList<DetectionEvent> Process(IEnumerable<FrigateEvent> events, bool suppress = false)
    {
        var result = new List<DetectionEvent>();
        foreach (var e in events.OrderBy(e => e.StartTime))
        {
            if (!_seen.Add(e.Id)) continue;
            _seenOrder.Enqueue(e.Id);
            while (_seenOrder.Count > 500) _seen.Remove(_seenOrder.Dequeue());
            if (suppress) continue;
            result.Add(new DetectionEvent
            {
                Source = EventSource.Frigate,
                Type = e.Type,
                RawLabel = e.SubLabel is null ? e.Label : $"{e.Label} ({e.SubLabel})",
                Camera = e.Camera,
                Timestamp = e.StartTime,
                Score = e.Score,
                FrigateEventId = e.Id,
            });
        }
        return result;
    }
}
