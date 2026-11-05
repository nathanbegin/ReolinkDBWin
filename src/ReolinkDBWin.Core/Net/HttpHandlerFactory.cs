using System.Net;
using System.Net.Security;

namespace ReolinkDBWin.Core.Net;

public static class HttpHandlerFactory
{
    /// <summary>
    /// Crée un gestionnaire HTTP. Les appareils Reolink et Frigate (port 8971) utilisent souvent un
    /// certificat auto-signé : <paramref name="acceptInvalidCertificate"/> permet de l'accepter.
    /// </summary>
    public static SocketsHttpHandler Create(bool acceptInvalidCertificate, CookieContainer? cookies = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = cookies is not null,
        };
        if (cookies is not null) handler.CookieContainer = cookies;
        if (acceptInvalidCertificate)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }
        return handler;
    }
}
