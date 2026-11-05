namespace ReolinkDBWin.Core.Settings;

public static class StreamUrlBuilder
{
    /// <summary>Construit l'URL du flux vidéo en direct selon la configuration.</summary>
    public static string? Build(AppSettings s)
    {
        switch (s.Stream.Source)
        {
            case StreamSource.Custom:
                return string.IsNullOrWhiteSpace(s.Stream.CustomUrl) ? null : s.Stream.CustomUrl.Trim();

            case StreamSource.FrigateRestream:
            {
                var name = string.IsNullOrWhiteSpace(s.Stream.FrigateStreamName) ? s.Frigate.Camera : s.Stream.FrigateStreamName;
                return FrigateRestreamUrl(s, name);
            }

            default:
            {
                if (string.IsNullOrWhiteSpace(s.Reolink.Host)) return null;
                var stream = s.Stream.Source == StreamSource.ReolinkSub ? "sub" : "main";
                var channel = (s.Reolink.Channel + 1).ToString("00");
                var port = s.Reolink.RtspPort > 0 ? s.Reolink.RtspPort : 554;
                var user = Uri.EscapeDataString(s.Reolink.Username ?? "");
                var pass = Uri.EscapeDataString(s.Reolink.Password ?? "");
                var cred = user.Length > 0 ? $"{user}:{pass}@" : "";
                return $"rtsp://{cred}{s.Reolink.Host.Trim()}:{port}/h264Preview_{channel}_{stream}";
            }
        }
    }

    /// <summary>URL du flux d'une caméra du widget.</summary>
    public static string? Build(AppSettings s, CameraSettings camera)
    {
        switch (camera.Source)
        {
            case CameraSourceKind.Doorbell:
                return Build(s);
            case CameraSourceKind.Url:
                return string.IsNullOrWhiteSpace(camera.Url) ? null : camera.Url.Trim();
            default:
            {
                var name = string.IsNullOrWhiteSpace(camera.StreamName) ? camera.FrigateCamera : camera.StreamName;
                return FrigateRestreamUrl(s, name);
            }
        }
    }

    /// <summary>
    /// URL RTSP du restream go2rtc de Frigate : rtsp://[utilisateur:mdp@]hôte:8554/flux.
    /// L'hôte est <see cref="StreamSettings.FrigateRtspHost"/> s'il est renseigné, sinon celui de l'adresse Frigate.
    /// </summary>
    public static string? FrigateRestreamUrl(AppSettings s, string? streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName)) return null;
        var host = FrigateRtspHost(s);
        if (host is null) return null;
        var port = s.Stream.FrigateRtspPort > 0 ? s.Stream.FrigateRtspPort : 8554;
        var cred = string.IsNullOrWhiteSpace(s.Stream.FrigateRtspUsername)
            ? ""
            : $"{Uri.EscapeDataString(s.Stream.FrigateRtspUsername.Trim())}:{Uri.EscapeDataString(s.Stream.FrigateRtspPassword ?? "")}@";
        return $"rtsp://{cred}{host}:{port}/{Uri.EscapeDataString(streamName.Trim())}";
    }

    public static string? FrigateRtspHost(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.Stream.FrigateRtspHost))
        {
            var h = s.Stream.FrigateRtspHost.Trim();
            // Tolère une saisie du type « http://192.168.1.10:5000 » ou « 192.168.1.10:8554 ».
            if (h.Contains("://") && Uri.TryCreate(h, UriKind.Absolute, out var u)) return u.Host;
            var colon = h.LastIndexOf(':');
            return colon > 0 && !h.Contains(']') && h.Count(c => c == ':') == 1 ? h[..colon] : h;
        }
        var baseUrl = s.Frigate.BaseUrl?.Trim() ?? "";
        if (baseUrl.Length == 0) return null;
        return Uri.TryCreate(baseUrl.Contains("://") ? baseUrl : "http://" + baseUrl, UriKind.Absolute, out var b) ? b.Host : null;
    }

    /// <summary>Masque le mot de passe d'une URL pour l'affichage.</summary>
    public static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo)) return url;
        var user = uri.UserInfo.Split(':')[0];
        return url.Replace(uri.UserInfo + "@", user + ":•••@");
    }
}
