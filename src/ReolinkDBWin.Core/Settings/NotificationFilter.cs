using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Settings;

/// <summary>
/// Décide si un événement doit produire une notification : source et type activés,
/// et délai anti-rafale (par type, toutes sources confondues, pour éviter les doublons
/// quand Reolink, Frigate et Home Assistant signalent la même chose).
/// </summary>
public sealed class NotificationFilter
{
    private readonly Dictionary<DetectionType, DateTimeOffset> _last = new();
    private readonly object _gate = new();

    public bool ShouldNotify(DetectionEvent e, NotificationSettings settings, DateTimeOffset now)
    {
        if (e.Source == EventSource.Test) return true;
        if (!settings.Enabled) return false;
        if (!settings.IsSourceEnabled(e.Source)) return false;
        if (!settings.IsTypeEnabled(e.Type)) return false;

        lock (_gate)
        {
            // La sonnette a un délai plus court : on veut savoir si quelqu'un insiste.
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, e.Type == DetectionType.Doorbell
                ? Math.Min(settings.CooldownSeconds, 10)
                : settings.CooldownSeconds));
            if (_last.TryGetValue(e.Type, out var last) && now - last < cooldown) return false;
            _last[e.Type] = now;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) _last.Clear();
    }
}
