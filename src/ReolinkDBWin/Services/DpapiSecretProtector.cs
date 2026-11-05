using System.Security.Cryptography;
using System.Text;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Services;

/// <summary>Chiffre les secrets avec DPAPI : seul l'utilisateur Windows courant peut les déchiffrer.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReolinkDBWin.v1");

    public string Protect(string plain) =>
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith("dpapi:")) return protectedValue; // valeur saisie à la main
        var bytes = Convert.FromBase64String(protectedValue["dpapi:".Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
}
