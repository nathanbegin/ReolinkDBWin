namespace ReolinkDBWin.Core.Settings;

/// <summary>
/// Caméras « en alerte » à mettre en avant dans le widget : une détection récente (maintenue pendant
/// <see cref="Hold"/>) ou un événement Frigate encore en cours. Une seule → plein cadre, plusieurs → mosaïque.
/// </summary>
public sealed class DetectionFocus
{
    private readonly Dictionary<string, DateTimeOffset> _until = new();
    private readonly List<string> _order = [];
    private HashSet<string> _activeNow = [];

    public TimeSpan Hold { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maintien supplémentaire après la fin d'un événement Frigate.</summary>
    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Caméras en alerte, dans l'ordre d'arrivée.</summary>
    public IReadOnlyList<string> Cameras => _order;

    public void Detected(string cameraId, DateTimeOffset now)
    {
        var until = now + Hold;
        _until[cameraId] = _until.TryGetValue(cameraId, out var u) && u > until ? u : until;
        if (!_order.Contains(cameraId)) _order.Add(cameraId);
    }

    /// <summary>Remplace l'ensemble des caméras qui ont une détection en cours.</summary>
    public void SetActive(IEnumerable<string> cameraIds, DateTimeOffset now)
    {
        var active = cameraIds.ToHashSet();
        foreach (var id in _activeNow.Where(id => !active.Contains(id)))
            if (_until.TryGetValue(id, out var u)) _until[id] = u > now + Linger ? u : now + Linger; // fin d'événement : court maintien
        foreach (var id in active)
        {
            if (!_until.ContainsKey(id) || _until[id] < now + Linger) _until[id] = now + Linger;
            if (!_order.Contains(id)) _order.Add(id);
        }
        _activeNow = active;
    }

    /// <summary>Retire les caméras dont l'alerte est terminée. Retourne true si l'ensemble a changé.</summary>
    public bool Update(DateTimeOffset now)
    {
        var expired = _order.Where(id => !_activeNow.Contains(id) && (!_until.TryGetValue(id, out var u) || u <= now)).ToList();
        foreach (var id in expired)
        {
            _order.Remove(id);
            _until.Remove(id);
        }
        // Un événement en cours prolonge l'alerte.
        foreach (var id in _activeNow) _until[id] = now + Linger;
        return expired.Count > 0;
    }

    public void Clear()
    {
        _until.Clear();
        _order.Clear();
        _activeNow = [];
    }
}
