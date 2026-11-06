using System.Text.Json.Serialization;

namespace ReolinkDBWin.Core.Settings;

/// <summary>Mode d'affichage du widget vidéo.</summary>
public enum LiveViewMode
{
    /// <summary>Une caméra à la fois (changement manuel avec ← / →).</summary>
    Single,
    /// <summary>Mosaïque : toutes les caméras en même temps.</summary>
    Grid,
    /// <summary>Défilement automatique : chaque caméra est affichée pendant sa durée, la suivante est préchargée.</summary>
    Carousel,
}

public enum TransitionKind
{
    Cut,
    Fade,
    Slide,
    Zoom,
}

public enum CameraSourceKind
{
    /// <summary>La sonnette configurée dans l'onglet Sonnette (source choisie dans l'onglet Vidéo).</summary>
    Doorbell,
    /// <summary>Flux go2rtc de Frigate (rtsp://frigate:8554/&lt;nom&gt;).</summary>
    FrigateRestream,
    /// <summary>URL saisie (RTSP, RTMP, HTTP…).</summary>
    Url,
}

/// <summary>Une caméra affichable dans le widget.</summary>
public sealed class CameraSettings
{
    public const string DoorbellId = "doorbell";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Caméra";
    public bool Enabled { get; set; } = true;
    public CameraSourceKind Source { get; set; } = CameraSourceKind.FrigateRestream;
    /// <summary>Nom du flux go2rtc (source FrigateRestream).</summary>
    public string StreamName { get; set; } = "";
    /// <summary>URL complète (source Url).</summary>
    public string Url { get; set; } = "";
    /// <summary>
    /// Flux léger facultatif (nom go2rtc ou URL, selon la source), utilisé quand la vidéo est affichée petite
    /// (mosaïque, alerte, petit widget) : beaucoup moins de mémoire et de processeur.
    /// </summary>
    public string LightAddress { get; set; } = "";
    /// <summary>Nom de la caméra dans Frigate, pour associer les détections (vide = déduit).</summary>
    public string FrigateCamera { get; set; } = "";
    /// <summary>Durée d'affichage en mode défilement.</summary>
    public int DwellSeconds { get; set; } = 10;
    /// <summary>Transition utilisée pour arriver sur cette caméra.</summary>
    public TransitionKind Transition { get; set; } = TransitionKind.Fade;
    public int TransitionMs { get; set; } = 600;
    /// <summary>Envoyer aussi des notifications pour les détections Frigate de cette caméra.</summary>
    public bool Notify { get; set; }

    public static CameraSettings CreateDoorbell() => new()
    {
        Id = DoorbellId,
        Name = "Sonnette",
        Source = CameraSourceKind.Doorbell,
        Notify = true,
    };

    /// <summary>Nom Frigate effectif utilisé pour associer les détections.</summary>
    public string? EffectiveFrigateCamera(AppSettings app)
    {
        if (!string.IsNullOrWhiteSpace(FrigateCamera)) return FrigateCamera.Trim();
        return Source switch
        {
            CameraSourceKind.Doorbell => string.IsNullOrWhiteSpace(app.Frigate.Camera) ? null : app.Frigate.Camera.Trim(),
            CameraSourceKind.FrigateRestream => string.IsNullOrWhiteSpace(StreamName) ? null : StreamName.Trim(),
            _ => null,
        };
    }
}

public sealed class MultiViewSettings
{
    public LiveViewMode Mode { get; set; } = LiveViewMode.Single;
    public List<CameraSettings> Cameras { get; set; } = [];
    /// <summary>Colonnes de la mosaïque (0 = automatique).</summary>
    public int GridColumns { get; set; }
    /// <summary>Basculer sur la caméra concernée lors d'une détection.</summary>
    public bool JumpOnDetection { get; set; } = true;
    /// <summary>Durée pendant laquelle la caméra d'une détection reste affichée avant de reprendre le défilement.</summary>
    public int DetectionHoldSeconds { get; set; } = 30;
    /// <summary>Attente maximale qu'une caméra soit prête avant de la sauter.</summary>
    public int MaxWaitSeconds { get; set; } = 10;
    public bool ShowCameraNames { get; set; } = true;
    /// <summary>Zoom à la molette (centré sur le curseur) dans le widget ; glisser pour se déplacer.</summary>
    public bool EnableMouseZoom { get; set; } = true;
    public double MaxZoom { get; set; } = 4;
    /// <summary>Petit voyant d'état en bas du widget (vert = en direct, orange = connexion, rouge = erreur).</summary>
    public bool ShowStatusLed { get; set; } = true;
    /// <summary>
    /// Qualité adaptative : flux léger et décodage à la taille réellement affichée ; bascule sans coupure
    /// vers la haute qualité quand la vidéo est agrandie ou zoomée.
    /// </summary>
    public bool AdaptiveQuality { get; set; } = true;

    [JsonIgnore]
    public IEnumerable<CameraSettings> EnabledCameras => Cameras.Where(c => c.Enabled);
}
