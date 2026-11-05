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
                if (!Uri.TryCreate(s.Frigate.BaseUrl, UriKind.Absolute, out var frigate)) return null;
                var name = string.IsNullOrWhiteSpace(s.Stream.FrigateStreamName) ? s.Frigate.Camera : s.Stream.FrigateStreamName;
                if (string.IsNullOrWhiteSpace(name)) return null;
                var port = s.Stream.FrigateRtspPort > 0 ? s.Stream.FrigateRtspPort : 8554;
                return $"rtsp://{frigate.Host}:{port}/{Uri.EscapeDataString(name.Trim())}";
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
                if (!Uri.TryCreate(s.Frigate.BaseUrl.Contains("://") ? s.Frigate.BaseUrl : "http://" + s.Frigate.BaseUrl, UriKind.Absolute, out var frigate)
                    || string.IsNullOrWhiteSpace(s.Frigate.BaseUrl)) return null;
                var name = string.IsNullOrWhiteSpace(camera.StreamName) ? camera.FrigateCamera : camera.StreamName;
                if (string.IsNullOrWhiteSpace(name)) return null;
                var port = s.Stream.FrigateRtspPort > 0 ? s.Stream.FrigateRtspPort : 8554;
                return $"rtsp://{frigate.Host}:{port}/{Uri.EscapeDataString(name.Trim())}";
            }
        }
    }

    /// <summary>Masque le mot de passe d'une URL pour l'affichage.</summary>
    public static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo)) return url;
        var user = uri.UserInfo.Split(':')[0];
        return url.Replace(uri.UserInfo + "@", user + ":•••@");
    }
}
