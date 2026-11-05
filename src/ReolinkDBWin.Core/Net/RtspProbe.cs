using System.Net.Sockets;
using System.Text;

namespace ReolinkDBWin.Core.Net;

public enum RtspProbeResult
{
    Ok,
    /// <summary>Nom d'hôte introuvable.</summary>
    HostNotFound,
    /// <summary>Port fermé, filtré ou hôte injoignable (proxy, pare-feu…).</summary>
    Unreachable,
    /// <summary>Identifiants requis ou refusés.</summary>
    Unauthorized,
    /// <summary>Le serveur répond, mais ce flux n'existe pas.</summary>
    StreamNotFound,
    /// <summary>Le port répond, mais ce n'est pas un serveur RTSP (ex. proxy HTTP).</summary>
    NotRtsp,
    Error,
}

public sealed record RtspProbeReport(RtspProbeResult Result, string Message, int? StatusCode = null);

/// <summary>
/// Diagnostic rapide d'une URL RTSP : connexion TCP puis requête DESCRIBE, pour distinguer
/// « hôte injoignable », « flux inexistant » et « authentification requise » sans lancer la vidéo.
/// </summary>
public static class RtspProbe
{
    public static async Task<RtspProbeReport> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            return new RtspProbeReport(RtspProbeResult.Error, "Ce n'est pas une URL RTSP.");
        var port = uri.Port > 0 ? uri.Port : 554;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(uri.Host, port, cts.Token).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            return new RtspProbeReport(RtspProbeResult.HostNotFound, $"Nom d'hôte « {uri.Host} » introuvable.");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new RtspProbeReport(RtspProbeResult.Unreachable,
                $"Impossible de joindre {uri.Host}:{port}. Le port est fermé ou non relayé (proxy inverse, Cloudflare, pare-feu). Utilisez l'IP locale du serveur Frigate.");
        }

        try
        {
            var stream = client.GetStream();
            // URL sans identifiants dans la ligne de requête.
            var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
            var request = $"DESCRIBE {clean} RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\nUser-Agent: VigieWidget\r\n";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
                request += $"Authorization: Basic {basic}\r\n";
            }
            request += "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);

            var buffer = new byte[2048];
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return Interpret(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RtspProbeReport(RtspProbeResult.NotRtsp, "Le port est ouvert mais ne répond pas comme un serveur RTSP.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new RtspProbeReport(RtspProbeResult.Error, "Erreur : " + ex.Message);
        }
    }

    internal static RtspProbeReport Interpret(string response)
    {
        var firstLine = response.Split('\n')[0].Trim();
        var parts = firstLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var code))
        {
            return firstLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
                ? new RtspProbeReport(RtspProbeResult.NotRtsp, "Le port répond en HTTP, pas en RTSP (proxy ?). Utilisez l'IP locale du serveur.")
                : new RtspProbeReport(RtspProbeResult.NotRtsp, "Réponse inattendue : ce n'est pas un serveur RTSP.");
        }
        return code switch
        {
            >= 200 and < 300 => new RtspProbeReport(RtspProbeResult.Ok, "Flux disponible ✔", code),
            401 or 403 => new RtspProbeReport(RtspProbeResult.Unauthorized,
                "Le serveur RTSP exige des identifiants (ou les refuse). Renseignez l'utilisateur et le mot de passe RTSP de go2rtc.", code),
            404 => new RtspProbeReport(RtspProbeResult.StreamNotFound,
                "Le serveur RTSP répond, mais ce flux n'existe pas. Vérifiez le nom dans la section go2rtc › streams de Frigate.", code),
            _ => new RtspProbeReport(RtspProbeResult.Error, $"Le serveur RTSP a répondu : {firstLine}", code),
        };
    }
}
