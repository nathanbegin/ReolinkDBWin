using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Events;

/// <summary>Interroge Frigate pour les nouveaux événements (objets détectés) de la caméra de la sonnette.</summary>
public sealed class FrigateEventMonitor : EventMonitor
{
    private readonly FrigateClient _client;
    private readonly string? _camera;
    private readonly IReadOnlyList<string> _labels;
    private readonly TimeSpan _interval;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();

    public FrigateEventMonitor(FrigateClient client, string? camera, IReadOnlyList<string> labels, TimeSpan interval)
    {
        _client = client;
        _camera = string.IsNullOrWhiteSpace(camera) ? null : camera;
        _labels = labels;
        _interval = interval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : interval;
    }

    public override EventSource Source => EventSource.Frigate;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var version = await _client.GetVersionAsync(ct).ConfigureAwait(false);
        SetStatus(MonitorState.Connected, $"Connecté à Frigate {version}" + (_camera is null ? "" : $" (caméra « {_camera} »)"));

        // Premier relevé : on mémorise les événements existants sans notifier.
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            // Fenêtre glissante : tolère un décalage d'horloge entre ce PC et le serveur Frigate.
            var events = await _client.GetEventsAsync(new FrigateEventQuery
            {
                Cameras = _camera is null ? [] : [_camera],
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
            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
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
