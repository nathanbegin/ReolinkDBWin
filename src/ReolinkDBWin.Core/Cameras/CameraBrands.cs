namespace ReolinkDBWin.Core.Cameras;

/// <summary>Modèle d'adresses RTSP d'une marque de caméras.</summary>
/// <param name="Main">Flux principal. Jetons : {user} {pass} {host} {port} {channel} (1, 2…) {channel0} (0, 1…) {channel2} (01, 02…).</param>
/// <param name="Sub">Flux secondaire (léger), ou null s'il n'existe pas.</param>
public sealed record CameraBrand(string Id, string Name, int DefaultPort, string Main, string? Sub, string Notes)
{
    /// <summary>Construit l'adresse RTSP. Utilisateur et mot de passe sont encodés pour une URL.</summary>
    public string BuildUrl(bool sub, string host, string? user, string? password, int channel = 1, int port = 0)
    {
        var template = sub && Sub is not null ? Sub : Main;
        var ch = Math.Max(1, channel);
        var url = template
            .Replace("{host}", host.Trim())
            .Replace("{port}", (port > 0 ? port : DefaultPort).ToString())
            .Replace("{channel2}", ch.ToString("00"))
            .Replace("{channel0}", (ch - 1).ToString())
            .Replace("{channel}", ch.ToString());
        var u = Uri.EscapeDataString(user?.Trim() ?? "");
        var p = Uri.EscapeDataString(password ?? "");
        if (u.Length == 0)
            return url.Replace("{user}:{pass}@", "").Replace("user={user}&password={pass}", "").TrimEnd('?', '&');
        return url.Replace("{user}", u).Replace("{pass}", p);
    }
}

/// <summary>
/// Adresses RTSP usuelles par marque. Ce sont les formats documentés par les fabricants ; certains modèles
/// ou firmwares diffèrent : la découverte ONVIF (qui demande l'adresse à la caméra) est plus sûre quand elle
/// est disponible.
/// </summary>
public static class CameraBrands
{
    public const string OnvifId = "onvif";

    public static IReadOnlyList<CameraBrand> All { get; } =
    [
        new("reolink", "Reolink", 554,
            "rtsp://{user}:{pass}@{host}:{port}/h264Preview_{channel2}_main",
            "rtsp://{user}:{pass}@{host}:{port}/h264Preview_{channel2}_sub",
            "Activer RTSP dans l'application Reolink (Réseau › Avancé › Configuration du port). Caméras H.265 récentes : remplacer h264Preview par h265Preview pour le flux principal. Les modèles à batterie n'ont pas de RTSP."),
        new("hikvision", "Hikvision (et Annke, HiLook…)", 554,
            "rtsp://{user}:{pass}@{host}:{port}/Streaming/Channels/{channel}01",
            "rtsp://{user}:{pass}@{host}:{port}/Streaming/Channels/{channel}02",
            "Canal 1 pour une caméra seule ; numéro de la caméra sur un enregistreur (NVR)."),
        new("dahua", "Dahua (et Amcrest, Lorex, Imou…)", 554,
            "rtsp://{user}:{pass}@{host}:{port}/cam/realmonitor?channel={channel}&subtype=0",
            "rtsp://{user}:{pass}@{host}:{port}/cam/realmonitor?channel={channel}&subtype=1",
            "Canal 1 pour une caméra seule."),
        new("tapo", "TP-Link Tapo / VIGI", 554,
            "rtsp://{user}:{pass}@{host}:{port}/stream1",
            "rtsp://{user}:{pass}@{host}:{port}/stream2",
            "Créer d'abord un « compte caméra » dans l'application Tapo (Réglages avancés) : ce sont ces identifiants, pas ceux du compte TP-Link."),
        new("axis", "Axis", 554,
            "rtsp://{user}:{pass}@{host}:{port}/axis-media/media.amp",
            "rtsp://{user}:{pass}@{host}:{port}/axis-media/media.amp?resolution=640x360",
            ""),
        new("unifi", "Ubiquiti UniFi Protect", 7447,
            "rtsp://{host}:{port}/{channel}",
            null,
            "Activer RTSP pour la caméra dans UniFi Protect, puis copier l'identifiant du flux affiché (à la place du canal) : mieux vaut coller l'adresse complète donnée par Protect."),
        new("foscam", "Foscam", 88,
            "rtsp://{user}:{pass}@{host}:{port}/videoMain",
            "rtsp://{user}:{pass}@{host}:{port}/videoSub",
            "Le port RTSP est souvent le port HTTP de la caméra (88) ; 554 sur certains modèles."),
        new("amcrest", "Amcrest", 554,
            "rtsp://{user}:{pass}@{host}:{port}/cam/realmonitor?channel={channel}&subtype=0",
            "rtsp://{user}:{pass}@{host}:{port}/cam/realmonitor?channel={channel}&subtype=1",
            ""),
        new("uniview", "Uniview (UNV)", 554,
            "rtsp://{user}:{pass}@{host}:{port}/media/video1",
            "rtsp://{user}:{pass}@{host}:{port}/media/video2",
            ""),
        new("eufy", "Eufy (modèles avec RTSP)", 554,
            "rtsp://{user}:{pass}@{host}:{port}/live0",
            null,
            "Activer RTSP/NAS dans l'application Eufy ; seulement certains modèles filaires."),
        new("wyze", "Wyze (firmware RTSP ou docker-wyze-bridge)", 8554,
            "rtsp://{host}:{port}/{channel}",
            null,
            "Wyze n'a pas de RTSP natif : utiliser docker-wyze-bridge et coller l'adresse qu'il affiche."),
        new(OnvifId, "ONVIF (demander l'adresse à la caméra)", 80,
            "", null,
            "Fonctionne avec la plupart des caméras IP. ONVIF doit être activé sur la caméra ; port usuel : 80, 8000 (Reolink), 8080, 2020 (Tapo)."),
    ];

    public static CameraBrand? Find(string id) => All.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
