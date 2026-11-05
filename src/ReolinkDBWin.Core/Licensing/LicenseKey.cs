using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReolinkDBWin.Core.Licensing;

/// <summary>Contenu d'une licence (signé par l'éditeur).</summary>
public sealed record LicenseInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("email")] public string Email { get; init; } = "";
    [JsonPropertyName("plan")] public string Plan { get; init; } = "annual";
    [JsonPropertyName("issued")] public required DateOnly Issued { get; init; }
    [JsonPropertyName("expires")] public required DateOnly Expires { get; init; }
}

/// <summary>
/// Clé de licence hors ligne : <c>VIGIE1-&lt;données base64url&gt;.&lt;signature base64url&gt;</c>.
/// Signée avec ECDSA P-256 / SHA-256 ; l'application ne contient que la clé publique.
/// </summary>
public static class LicenseKey
{
    public const string Prefix = "VIGIE1-";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string Create(LicenseInfo info, ECDsa privateKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(info, Json);
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256);
        return Prefix + B64Url(payload) + "." + B64Url(signature);
    }

    public static bool TryVerify(string? key, ECDsa publicKey, out LicenseInfo? info, out string error)
    {
        info = null;
        var k = Normalize(key);
        if (k.Length == 0) { error = "Clé vide."; return false; }
        if (!k.StartsWith(Prefix, StringComparison.Ordinal)) { error = "Format de clé non reconnu."; return false; }
        var parts = k[Prefix.Length..].Split('.');
        if (parts.Length != 2) { error = "Format de clé non reconnu."; return false; }
        byte[] payload, signature;
        try
        {
            payload = FromB64Url(parts[0]);
            signature = FromB64Url(parts[1]);
        }
        catch (FormatException)
        {
            error = "Clé corrompue (caractères invalides).";
            return false;
        }
        if (!publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256))
        {
            error = "Clé invalide (signature incorrecte).";
            return false;
        }
        try
        {
            info = JsonSerializer.Deserialize<LicenseInfo>(payload, Json);
        }
        catch (JsonException)
        {
            info = null;
        }
        if (info is null) { error = "Clé illisible."; return false; }
        error = "";
        return true;
    }

    /// <summary>Retire espaces et retours à la ligne (copier-coller depuis un courriel).</summary>
    public static string Normalize(string? key) =>
        key is null ? "" : new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());

    public static ECDsa ImportPublicKey(string spkiBase64)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
        return ecdsa;
    }

    private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        return Convert.FromBase64String(b);
    }
}
