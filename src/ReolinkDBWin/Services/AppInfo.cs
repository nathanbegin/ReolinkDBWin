using System.Diagnostics;
using System.Reflection;

namespace ReolinkDBWin.Services;

/// <summary>Informations publiques sur le produit (affichées dans « À propos »).</summary>
public static class AppInfo
{
    /// <summary>Nom commercial affiché. Un seul endroit à modifier pour renommer le produit.</summary>
    public const string ProductName = "Vigie Widget";
    /// <summary>Nom technique (dossiers, registre, exécutable).</summary>
    public const string TechnicalName = "VigieWidget";
    public const string Tagline = "Widget vidéo, notifications et historique pour sonnettes et caméras";
    public const string Developer = "Nathan Bégin";
    public const string GitHubUrl = "https://github.com/nathanbegin/ReolinkDBWin";
    /// <summary>Page d'achat / de renouvellement de l'abonnement (vide = bouton masqué).</summary>
    public const string PurchaseUrl = "";
    /// <summary>
    /// Flux des mises à jour : API « dernière release » GitHub. Le dépôt doit être public pour que les
    /// clients y accèdent (sinon, publier les releases dans un dépôt public dédié et changer cette adresse).
    /// Vide = vérification désactivée.
    /// </summary>
    public const string UpdateFeedUrl = "https://api.github.com/repos/nathanbegin/ReolinkDBWin/releases/latest";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Copyright => $"© {DateTime.Now.Year} {Developer}. Tous droits réservés.";

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Ouverture du lien", ex); }
    }
}
