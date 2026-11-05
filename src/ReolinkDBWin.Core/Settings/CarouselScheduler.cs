namespace ReolinkDBWin.Core.Settings;

public sealed record CarouselItem(string Id, TimeSpan Dwell, TransitionKind Transition, TimeSpan TransitionDuration);

/// <summary>Changement de caméra décidé par l'ordonnanceur.</summary>
public sealed record CarouselSwitch(string FromId, string ToId, TransitionKind Transition, TimeSpan Duration, bool IsPriority);

/// <summary>
/// Ordonnanceur du défilement des caméras. Il ne bascule vers la caméra suivante que lorsqu'elle est
/// <em>prête</em> (déjà en lecture en arrière-plan) : l'affichage ne montre jamais un écran de chargement.
/// <see cref="WantedIds"/> indique les flux à garder ouverts (caméra courante + suivante préchargée
/// + caméra prioritaire d'une détection).
/// </summary>
public sealed class CarouselScheduler
{
    private readonly List<CarouselItem> _items;
    private int _current;
    private int _next;
    private DateTimeOffset _due;
    private DateTimeOffset? _waitingSince;
    private string? _priorityId;
    private DateTimeOffset _priorityRequestedAt;
    private TimeSpan _priorityHold;

    public CarouselScheduler(IEnumerable<CarouselItem> items)
    {
        _items = items.ToList();
        _next = _items.Count > 1 ? 1 : 0;
    }

    /// <summary>Défilement automatique (false = mode une caméra, changement manuel uniquement).</summary>
    public bool AutoAdvance { get; init; } = true;
    /// <summary>Précharger la caméra suivante (vrai en défilement automatique).</summary>
    public bool PreloadNext { get; init; } = true;
    /// <summary>Attente maximale d'une caméra pas encore prête avant de la sauter.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Pause manuelle du défilement.</summary>
    public bool Paused { get; set; }

    public IReadOnlyList<CarouselItem> Items => _items;
    public int Count => _items.Count;
    public int CurrentIndex => _current;
    public CarouselItem? Current => _items.Count == 0 ? null : _items[_current];
    public CarouselItem? Next => _items.Count < 2 ? null : _items[_next];
    public string? PendingPriorityId => _priorityId;
    public DateTimeOffset SwitchDueAt => _due;
    public bool IsWaitingForNext => _waitingSince is not null;

    /// <summary>Flux à garder ouverts.</summary>
    public IReadOnlyCollection<string> WantedIds
    {
        get
        {
            var set = new List<string>(3);
            if (Current is { } c) set.Add(c.Id);
            if (PreloadNext && AutoAdvance && Next is { } n && !set.Contains(n.Id)) set.Add(n.Id);
            if (_priorityId is { } p && !set.Contains(p)) set.Add(p);
            return set;
        }
    }

    public void Start(DateTimeOffset now, string? startId = null)
    {
        if (_items.Count == 0) return;
        var idx = startId is null ? -1 : _items.FindIndex(i => i.Id == startId);
        _current = idx >= 0 ? idx : 0;
        _next = (_current + 1) % _items.Count;
        _due = now + _items[_current].Dwell;
        _waitingSince = null;
        _priorityId = null;
    }

    public TimeSpan Remaining(DateTimeOffset now) => _due > now ? _due - now : TimeSpan.Zero;

    /// <summary>Demande d'afficher une caméra (détection) dès qu'elle est prête, pendant <paramref name="hold"/>.</summary>
    public bool Prioritize(string id, DateTimeOffset now, TimeSpan hold)
    {
        if (_items.FindIndex(i => i.Id == id) < 0) return false;
        _priorityId = id;
        _priorityRequestedAt = now;
        _priorityHold = hold;
        return true;
    }

    /// <summary>À appeler régulièrement. Retourne un changement à effectuer, ou null.</summary>
    public CarouselSwitch? Tick(DateTimeOffset now, Func<string, bool> isReady)
    {
        if (_items.Count == 0) return null;

        if (_priorityId is { } pid)
        {
            if (pid == _items[_current].Id)
            {
                _priorityId = null;
                _due = now + _priorityHold; // déjà affichée : on prolonge
                return null;
            }
            if (isReady(pid))
            {
                _priorityId = null;
                return SwitchTo(_items.FindIndex(i => i.Id == pid), now, _priorityHold, priority: true);
            }
            if (now - _priorityRequestedAt >= MaxWait) _priorityId = null; // abandon
            return null; // on garde la caméra courante en attendant
        }

        if (!AutoAdvance || Paused || _items.Count < 2 || now < _due) return null;

        var next = _items[_next];
        if (isReady(next.Id)) return SwitchTo(_next, now, next.Dwell, priority: false);

        _waitingSince ??= now;
        if (now - _waitingSince.Value >= MaxWait)
        {
            // La caméra suivante ne répond pas : on la saute (elle sera retentée au prochain tour).
            _waitingSince = null;
            _next = (_next + 1) % _items.Count;
            if (_next == _current) _next = (_current + 1) % _items.Count;
            _due = now; // on retente immédiatement avec la suivante
        }
        return null;
    }

    /// <summary>Changement manuel (← / →). Bascule même si la caméra n'est pas prête.</summary>
    public CarouselSwitch? Step(int direction, DateTimeOffset now)
    {
        if (_items.Count < 2) return null;
        _priorityId = null;
        var target = ((_current + direction) % _items.Count + _items.Count) % _items.Count;
        return SwitchTo(target, now, _items[target].Dwell, priority: false);
    }

    /// <summary>Affiche immédiatement une caméra précise.</summary>
    public CarouselSwitch? Show(string id, DateTimeOffset now)
    {
        var idx = _items.FindIndex(i => i.Id == id);
        if (idx < 0 || idx == _current) return null;
        _priorityId = null;
        return SwitchTo(idx, now, _items[idx].Dwell, priority: false);
    }

    private CarouselSwitch SwitchTo(int index, DateTimeOffset now, TimeSpan dwell, bool priority)
    {
        var from = _items[_current];
        var to = _items[index];
        _current = index;
        _next = (index + 1) % _items.Count;
        _due = now + dwell;
        _waitingSince = null;
        return new CarouselSwitch(from.Id, to.Id, to.Transition, to.TransitionDuration, priority);
    }
}
