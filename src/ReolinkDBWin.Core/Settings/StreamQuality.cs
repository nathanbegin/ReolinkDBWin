namespace ReolinkDBWin.Core.Settings;

/// <summary>Qualité demandée pour un flux : sous-flux léger ou principal, et largeur de décodage maximale.</summary>
public readonly record struct StreamQuality(bool Light, int MaxWidth)
{
    public static readonly int[] Tiers = [640, 960, 1280, 1920];

    /// <summary>Haute qualité, sans adaptation.</summary>
    public static StreamQuality Full => new(false, 1920);

    /// <summary>
    /// Choisit la qualité d'après la largeur réellement affichée (pixels physiques) et le zoom :
    /// sous-flux si l'affichage fait au plus <see cref="LightUpTo"/> pixels, largeur de décodage au palier supérieur.
    /// </summary>
    public static StreamQuality Plan(double displayWidthPx, double zoom, bool hasLight, bool adaptive)
    {
        if (!adaptive) return Full;
        var needed = Math.Max(1, displayWidthPx) * Math.Clamp(zoom, 1, 4);
        var tier = Tiers.FirstOrDefault(t => t >= needed * 0.9);
        if (tier == 0) tier = Tiers[^1];
        return new StreamQuality(hasLight && needed <= LightUpTo, tier);
    }

    /// <summary>Le sous-flux Reolink fait 640 px de large : au-delà de ~800 px affichés, on passe au flux principal.</summary>
    public const double LightUpTo = 800;

    /// <summary>
    /// Faut-il remplacer le flux actuel ? Chaque remplacement ouvre une nouvelle connexion et un nouveau
    /// lecteur : on ne le fait que si cela change vraiment quelque chose.
    /// <list type="bullet">
    /// <item>passage du sous-flux au flux principal : oui (agrandissement / zoom) ;</item>
    /// <item>même flux, largeur de décodage plus grande : oui ;</item>
    /// <item>même flux, largeur plus petite : non (le décodage coûte pareil, seule la copie change) ;</item>
    /// <item>retour au sous-flux : oui, mais l'appelant attend que la petite taille dure (anti-rebond).</item>
    /// </list>
    /// </summary>
    public bool ShouldReplace(StreamQuality current)
    {
        if (Light != current.Light) return true;
        return MaxWidth > current.MaxWidth;
    }

    /// <summary>Changement vers une qualité inférieure (à différer pour éviter les allers-retours).</summary>
    public bool IsDowngradeFrom(StreamQuality current) => Light && !current.Light;

}
