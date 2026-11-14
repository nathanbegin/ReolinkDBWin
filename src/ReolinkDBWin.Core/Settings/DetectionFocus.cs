namespace ReolinkDBWin.Core.Settings;

/// <summary>
/// Caméras « en alerte » à mettre en avant dans le widget après une détection. Une seule → plein cadre,
/// plusieurs → mosaïque. L'alerte est toujours bornée et l'utilisateur garde la main :
/// <list type="bullet">
/// <item>une détection met la caméra en avant pendant <see cref="Hold"/> ;</item>
/// <item>tant que l'événement est en cours (Frigate), elle est prolongée, mais jamais au-delà de
/// <see cref="MaxDuration"/> : un objet immobile ou une rue passante ne bloque plus le défilement ;</item>
/// <item>après la durée maximale, ou si l'utilisateur ferme l'alerte, la caméra est mise en sourdine pendant
/// <see cref="Cooldown"/> : elle ne revient pas aussitôt au premier plan.</item>
/// </list>
/// </summary>
public sealed class DetectionFocus
{
    private readonly Dictionary<string, DateTimeOffset> _until = new();
    private readonly Dictionary<string, DateTimeOffset> _since = new();
    private readonly Dictionary<string, DateTimeOffset> _snoozedUntil = new();
    private readonly List<string> _order = [];
    private HashSet<string> _activeNow = [];

    /// <summary>Durée d'affichage après une détection.</summary>
    public TimeSpan Hold { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maintien supplémentaire après la fin d'un événement en cours.</summary>
    public TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>Durée maximale d'une alerte continue sur une caméra.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Sourdine d'une caméra après la durée maximale ou une fermeture manuelle.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Caméras en alerte, dans l'ordre d'arrivée.</summary>
    public IReadOnlyList<string> Cameras => _order;

    public bool IsSnoozed(string cameraId, DateTimeOffset now) =>
        _snoozedUntil.TryGetValue(cameraId, out var until) && until > now;

    /// <summary>Nouvelle détection sur une caméra. Retourne false si elle est en sourdine.</summary>
    public bool Detected(string cameraId, DateTimeOffset now)
    {
        if (IsSnoozed(cameraId, now)) return false;
        if (!_order.Contains(cameraId))
        {
            _order.Add(cameraId);
            _since[cameraId] = now;
        }
        var until = now + Hold;
        if (!_until.TryGetValue(cameraId, out var current) || current < until) _until[cameraId] = until;
        return true;
    }

    /// <summary>
    /// Caméras dont un événement est encore en cours. Cela prolonge seulement les alertes déjà ouvertes
    /// par <see cref="Detected"/> ; cela n'en ouvre pas de nouvelles.
    /// </summary>
    public void SetActive(IEnumerable<string> cameraIds, DateTimeOffset now)
    {
        _activeNow = cameraIds.ToHashSet();
        Prolong(now);
    }

    /// <summary>Retire les caméras dont l'alerte est terminée. Retourne true si l'ensemble a changé.</summary>
    public bool Update(DateTimeOffset now)
    {
        Prolong(now);
        var changed = false;
        foreach (var id in _order.ToList())
        {
            var reachedMax = now - _since[id] >= MaxDuration;
            if (!reachedMax && _until[id] > now) continue;
            Remove(id);
            if (reachedMax) _snoozedUntil[id] = now + Cooldown;
            changed = true;
        }
        foreach (var id in _snoozedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _snoozedUntil.Remove(id);
        return changed;
    }

    /// <summary>L'utilisateur ferme l'alerte : toutes les caméras en alerte sont mises en sourdine.</summary>
    public bool Dismiss(DateTimeOffset now)
    {
        if (_order.Count == 0) return false;
        foreach (var id in _order.ToList())
        {
            _snoozedUntil[id] = now + Cooldown;
            Remove(id);
        }
        return true;
    }

    public void Clear()
    {
        _until.Clear();
        _since.Clear();
        _snoozedUntil.Clear();
        _order.Clear();
        _activeNow = [];
    }

    private void Prolong(DateTimeOffset now)
    {
        foreach (var id in _order)
        {
            if (!_activeNow.Contains(id)) continue;
            var until = now + Linger;
            if (_until[id] < until) _until[id] = until;
        }
    }

    private void Remove(string id)
    {
        _order.Remove(id);
        _until.Remove(id);
        _since.Remove(id);
    }
}
