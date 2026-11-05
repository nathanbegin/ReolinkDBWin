using System.Text.Json.Serialization;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Settings;

/// <summary>Configuration complète de l'application, sérialisée dans %AppData%\VigieWidget\settings.json.</summary>
public sealed class AppSettings
{
    public ReolinkSettings Reolink { get; set; } = new();
    public StreamSettings Stream { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public FrigateSettings Frigate { get; set; } = new();
    public HomeAssistantSettings HomeAssistant { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public WindowSettings LiveWindow { get; set; } = new();
    public MultiViewSettings MultiView { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool FirstRunCompleted { get; set; }

    [JsonIgnore]
    public bool IsReolinkConfigured => !string.IsNullOrWhiteSpace(Reolink.Host);

    /// <summary>Garantit qu'au moins la sonnette figure dans la liste des caméras (migration et valeurs par défaut).</summary>
    public AppSettings Normalize()
    {
        MultiView ??= new MultiViewSettings();
        MultiView.Cameras ??= [];
        MultiView.Cameras.RemoveAll(c => c is null);
        if (MultiView.Cameras.Count == 0) MultiView.Cameras.Add(CameraSettings.CreateDoorbell());
        foreach (var c in MultiView.Cameras)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) c.Id = Guid.NewGuid().ToString("N");
            c.DwellSeconds = Math.Clamp(c.DwellSeconds, 2, 3600);
            c.TransitionMs = Math.Clamp(c.TransitionMs, 0, 5000);
        }
        // Identifiants uniques
        var seen = new HashSet<string>();
        foreach (var c in MultiView.Cameras)
            if (!seen.Add(c.Id)) { c.Id = Guid.NewGuid().ToString("N"); seen.Add(c.Id); }
        return this;
    }
}

public sealed class ReolinkSettings
{
    public string Host { get; set; } = "";
    /// <summary>0 = port par défaut.</summary>
    public int Port { get; set; }
    public bool UseHttps { get; set; }
    public bool AcceptInvalidCertificate { get; set; } = true;
    public string Username { get; set; } = "admin";
    [JsonIgnore] public string Password { get; set; } = "";
    [JsonPropertyName("password")] public string? PasswordProtected { get; set; }
    public int Channel { get; set; }
    /// <summary>Surveillance directe des événements de la sonnette (interrogation de l'API).</summary>
    public bool MonitorEvents { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;
    public int RtspPort { get; set; } = 554;
}

public enum StreamSource
{
    /// <summary>RTSP direct, flux principal (haute qualité).</summary>
    ReolinkMain,
    /// <summary>RTSP direct, flux secondaire (léger).</summary>
    ReolinkSub,
    /// <summary>Restream go2rtc de Frigate (rtsp://frigate:8554/&lt;nom&gt;) : n'ajoute pas de charge à la sonnette.</summary>
    FrigateRestream,
    /// <summary>URL saisie manuellement.</summary>
    Custom,
}

public sealed class StreamSettings
{
    public StreamSource Source { get; set; } = StreamSource.ReolinkMain;
    public string CustomUrl { get; set; } = "";
    /// <summary>Nom du flux go2rtc dans Frigate (souvent le même que la caméra).</summary>
    public string FrigateStreamName { get; set; } = "";
    public int FrigateRtspPort { get; set; } = 8554;
    /// <summary>Tampon réseau de VLC (ms). Plus bas = moins de latence, plus haut = plus stable.</summary>
    public int NetworkCachingMs { get; set; } = 400;
    public bool UseTcp { get; set; } = true;
    public bool StartMuted { get; set; } = true;
    /// <summary>Garde le flux ouvert quand le widget est masqué, pour un affichage instantané.</summary>
    public bool KeepAliveWhenHidden { get; set; }
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool FromReolink { get; set; } = true;
    public bool FromFrigate { get; set; }
    public bool FromHomeAssistant { get; set; }

    public bool Doorbell { get; set; } = true;
    public bool Person { get; set; } = true;
    public bool Vehicle { get; set; }
    public bool Animal { get; set; }
    public bool Package { get; set; } = true;
    public bool Face { get; set; }
    public bool Motion { get; set; }
    public bool Other { get; set; }

    /// <summary>Délai minimum entre deux notifications du même type (toutes sources confondues).</summary>
    public int CooldownSeconds { get; set; } = 30;
    public bool IncludeSnapshot { get; set; } = true;
    /// <summary>Affiche automatiquement le widget vidéo quand quelqu'un sonne.</summary>
    public bool OpenLiveOnDoorbell { get; set; } = true;
    public bool Sound { get; set; } = true;

    public bool IsTypeEnabled(DetectionType type) => type switch
    {
        DetectionType.Doorbell => Doorbell,
        DetectionType.Person => Person,
        DetectionType.Vehicle => Vehicle,
        DetectionType.Animal => Animal,
        DetectionType.Package => Package,
        DetectionType.Face => Face,
        DetectionType.Motion => Motion,
        _ => Other,
    };

    public bool IsSourceEnabled(EventSource source) => source switch
    {
        EventSource.Reolink => FromReolink,
        EventSource.Frigate => FromFrigate,
        EventSource.HomeAssistant => FromHomeAssistant,
        _ => true,
    };
}

public sealed class FrigateSettings
{
    /// <summary>Ex. http://192.168.1.10:5000 (sans authentification) ou https://frigate.local:8971.</summary>
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    [JsonIgnore] public string Password { get; set; } = "";
    [JsonPropertyName("password")] public string? PasswordProtected { get; set; }
    public bool AcceptInvalidCertificate { get; set; } = true;
    /// <summary>Nom de la caméra de la sonnette dans Frigate.</summary>
    public string Camera { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 5;
    /// <summary>Objets Frigate à notifier (vide = tous).</summary>
    public List<string> NotifyLabels { get; set; } = [];

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed class HomeAssistantSettings
{
    /// <summary>Ex. http://homeassistant.local:8123</summary>
    public string BaseUrl { get; set; } = "";
    [JsonIgnore] public string AccessToken { get; set; } = "";
    [JsonPropertyName("accessToken")] public string? AccessTokenProtected { get; set; }
    public bool AcceptInvalidCertificate { get; set; }
    /// <summary>Entités binary_sensor à surveiller (ex. binary_sensor.sonnette_visitor).</summary>
    public List<string> EntityIds { get; set; } = [];

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed class HotkeySettings
{
    public string ToggleLive { get; set; } = "Ctrl+Alt+D";
    public string OpenHistory { get; set; } = "Ctrl+Alt+H";
}

public sealed class WindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 480;
    public double Height { get; set; } = 380;
    public bool Topmost { get; set; } = true;
}
