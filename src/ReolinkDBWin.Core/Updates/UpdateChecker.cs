using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ReolinkDBWin.Core.Updates;

public sealed record UpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public required Uri PageUrl { get; init; }
    /// <summary>Installateur (…-Setup-x.y.z.exe), si présent dans la version publiée.</summary>
    public Uri? InstallerUrl { get; init; }
    public string? InstallerName { get; init; }
    public long? InstallerSize { get; init; }
    /// <summary>Empreinte SHA-256 publiée par GitHub (« sha256:… »), vérifiée après téléchargement.</summary>
    public string? InstallerSha256 { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Vérifie la dernière version publiée (API des releases GitHub) et télécharge l'installateur.
/// L'installateur Inno Setup met à jour l'installation existante (même AppId) en conservant
/// paramètres et licence, qui sont stockés hors du dossier d'installation.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly Uri _feed;
    private readonly HttpClient _http;

    public UpdateChecker(string feedUrl, string userAgent, HttpMessageHandler? handler = null)
    {
        _feed = new Uri(feedUrl);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Dernière version publiée, ou null si aucune (ou flux inaccessible : dépôt privé…).</summary>
    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(_feed, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // aucune release (ou dépôt privé)
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    internal static UpdateInfo? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o) return null;
        if (o["draft"]?.GetValue<bool>() == true || o["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = o["tag_name"]?.ToString();
        var version = ParseVersion(tag);
        if (tag is null || version is null || !Uri.TryCreate(o["html_url"]?.ToString(), UriKind.Absolute, out var page)) return null;

        JsonObject? installer = null;
        if (o["assets"] is JsonArray assets)
        {
            installer = assets.OfType<JsonObject>().FirstOrDefault(a =>
            {
                var name = a["name"]?.ToString() ?? "";
                return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
            });
        }
        var digest = installer?["digest"]?.ToString();
        return new UpdateInfo
        {
            Version = version,
            Tag = tag,
            Title = o["name"]?.ToString(),
            Notes = o["body"]?.ToString(),
            PageUrl = page,
            InstallerUrl = Uri.TryCreate(installer?["browser_download_url"]?.ToString(), UriKind.Absolute, out var u) ? u : null,
            InstallerName = installer?["name"]?.ToString(),
            InstallerSize = long.TryParse(installer?["size"]?.ToString(), out var size) ? size : null,
            InstallerSha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
            PublishedAt = DateTimeOffset.TryParse(o["published_at"]?.ToString(), out var d) ? d : null,
        };
    }

    /// <summary>« v1.5.0 », « 1.5 », « 1.5.0-beta » → 1.5.0.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(['-', '+', ' ']);
        if (cut > 0) t = t[..cut];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Version candidate, Version current) =>
        Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Télécharge l'installateur et vérifie son empreinte si GitHub la fournit.</summary>
    public async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (info.InstallerUrl is null) throw new InvalidOperationException("Cette version ne contient pas d'installateur.");
        var tmp = destination + ".part";
        using (var resp = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.InstallerSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total.Value);
            }
        }
        if (info.InstallerSha256 is { } expected)
        {
            await using var fs = File.OpenRead(tmp);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(tmp);
                throw new InvalidOperationException("Le fichier téléchargé est corrompu (empreinte SHA-256 différente).");
            }
        }
        File.Move(tmp, destination, overwrite: true);
        progress?.Report(1);
    }

    public void Dispose() => _http.Dispose();
}
