using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Settings;

public static class CameraMatcher
{
    /// <summary>Retrouve la caméra du widget concernée par une détection.</summary>
    public static CameraSettings? Find(AppSettings s, DetectionEvent e)
    {
        var cameras = s.MultiView.Cameras;
        if (e.Source is EventSource.Reolink or EventSource.HomeAssistant)
            return cameras.FirstOrDefault(c => c.Enabled && c.Source == CameraSourceKind.Doorbell);
        if (e.Source == EventSource.Frigate && !string.IsNullOrEmpty(e.Camera))
            return cameras.FirstOrDefault(c => c.Enabled && string.Equals(c.EffectiveFrigateCamera(s), e.Camera, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Caméras Frigate à surveiller (vide = toutes).</summary>
    public static IReadOnlyList<string> FrigateCamerasToWatch(AppSettings s)
    {
        var notify = FrigateCamerasToNotify(s);
        if (notify.Count == 0) return [];
        var set = new HashSet<string>(notify, StringComparer.OrdinalIgnoreCase);
        if (s.MultiView.JumpOnDetection)
            foreach (var c in s.MultiView.EnabledCameras)
                if (c.EffectiveFrigateCamera(s) is { } name) set.Add(name);
        return set.OrderBy(x => x).ToList();
    }

    /// <summary>Caméras Frigate dont les détections sont notifiées (vide = toutes).</summary>
    public static IReadOnlyList<string> FrigateCamerasToNotify(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Frigate.Camera)) return [];
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.Frigate.Camera.Trim() };
        foreach (var c in s.MultiView.Cameras.Where(c => c.Notify))
            if (c.EffectiveFrigateCamera(s) is { } name) set.Add(name);
        return set.OrderBy(x => x).ToList();
    }
}
