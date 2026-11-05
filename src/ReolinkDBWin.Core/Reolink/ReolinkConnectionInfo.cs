namespace ReolinkDBWin.Core.Reolink;

public sealed record ReolinkConnectionInfo
{
    public required string Host { get; init; }
    /// <summary>0 = port par défaut (80 en HTTP, 443 en HTTPS).</summary>
    public int Port { get; init; }
    public bool UseHttps { get; init; }
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "";
    public bool AcceptInvalidCertificate { get; init; } = true;

    public int EffectivePort => Port > 0 ? Port : (UseHttps ? 443 : 80);

    public Uri ApiUri => new UriBuilder(UseHttps ? "https" : "http", Host, EffectivePort, "/cgi-bin/api.cgi").Uri;
}
