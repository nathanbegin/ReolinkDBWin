namespace ReolinkDBWin.Core.Licensing;

/// <summary>
/// Clé publique de vérification des licences (SubjectPublicKeyInfo, base64).
/// Générez la paire de clés avec : <c>dotnet run --project tools/LicenseTool -- init</c>
/// puis collez ici la clé publique affichée. NE JAMAIS mettre la clé privée dans le dépôt.
/// Vide = version de développement (aucune vérification de licence).
/// </summary>
public static class LicensePublicKey
{
    public const string Value = "";
}
