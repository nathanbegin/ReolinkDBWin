namespace ReolinkDBWin.Core.Licensing;

public enum LicenseState
{
    /// <summary>Aucune clé publique intégrée : version de développement, aucune restriction.</summary>
    DevelopmentBuild,
    Trial,
    TrialExpired,
    Active,
    /// <summary>Active, mais expire bientôt (rappel de renouvellement).</summary>
    ExpiringSoon,
    /// <summary>Expirée depuis peu : période de grâce, l'application fonctionne encore.</summary>
    Grace,
    Expired,
}

public sealed record LicenseStatus(LicenseState State, LicenseInfo? License, int DaysRemaining, string Message)
{
    public bool AllowsUse => State is LicenseState.DevelopmentBuild or LicenseState.Trial or LicenseState.Active
        or LicenseState.ExpiringSoon or LicenseState.Grace;

    public bool ShouldRemind => State is LicenseState.ExpiringSoon or LicenseState.Grace
        || (State == LicenseState.Trial && DaysRemaining <= 3);
}

/// <summary>Stockage de la clé et de la date de début d'essai (registre sous Windows).</summary>
public interface ILicenseStore
{
    string? LoadKey();
    void SaveKey(string? key);
    DateOnly? LoadTrialStart();
    void SaveTrialStart(DateOnly date);
}

public sealed class InMemoryLicenseStore : ILicenseStore
{
    public string? Key { get; set; }
    public DateOnly? TrialStart { get; set; }
    public string? LoadKey() => Key;
    public void SaveKey(string? key) => Key = key;
    public DateOnly? LoadTrialStart() => TrialStart;
    public void SaveTrialStart(DateOnly date) => TrialStart = date;
}

/// <summary>Évalue l'état de la licence : essai, abonnement actif, rappel, période de grâce, expiration.</summary>
public sealed class LicenseManager
{
    private readonly string _publicKey;
    private readonly ILicenseStore _store;
    private readonly Func<DateOnly> _today;

    public LicenseManager(string publicKeySpkiBase64, ILicenseStore store, Func<DateOnly>? today = null)
    {
        _publicKey = publicKeySpkiBase64?.Trim() ?? "";
        _store = store;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    public int TrialDays { get; init; } = 14;
    public int ReminderDays { get; init; } = 14;
    public int GraceDays { get; init; } = 7;

    public bool IsDevelopmentBuild => _publicKey.Length == 0;

    public LicenseStatus Evaluate()
    {
        if (IsDevelopmentBuild)
            return new LicenseStatus(LicenseState.DevelopmentBuild, null, int.MaxValue, "Version de développement (vérification de licence désactivée).");

        var today = _today();
        var key = _store.LoadKey();
        if (!string.IsNullOrWhiteSpace(key) && Verify(key, out var info, out _))
            return FromLicense(info!, today);

        var start = _store.LoadTrialStart();
        if (start is null || start > today)
        {
            start = today;
            _store.SaveTrialStart(today);
        }
        var left = TrialDays - (today.DayNumber - start.Value.DayNumber);
        return left > 0
            ? new LicenseStatus(LicenseState.Trial, null, left, $"Essai gratuit : {left} jour(s) restant(s).")
            : new LicenseStatus(LicenseState.TrialExpired, null, 0, "La période d'essai est terminée. Entrez une clé de licence pour continuer.");
    }

    /// <summary>Vérifie et enregistre une clé. Retourne l'état résultant, ou une erreur.</summary>
    public (bool Ok, LicenseStatus? Status, string Error) Activate(string key)
    {
        if (IsDevelopmentBuild) return (false, null, "Version de développement : aucune clé n'est nécessaire.");
        if (!Verify(key, out var info, out var error)) return (false, null, error);
        var status = FromLicense(info!, _today());
        if (status.State == LicenseState.Expired) return (false, status, status.Message);
        _store.SaveKey(LicenseKey.Normalize(key));
        return (true, status, "");
    }

    public void Deactivate() => _store.SaveKey(null);

    private bool Verify(string key, out LicenseInfo? info, out string error)
    {
        using var pub = LicenseKey.ImportPublicKey(_publicKey);
        return LicenseKey.TryVerify(key, pub, out info, out error);
    }

    private LicenseStatus FromLicense(LicenseInfo info, DateOnly today)
    {
        var left = info.Expires.DayNumber - today.DayNumber;
        var until = info.Expires.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-CA"));
        if (left > ReminderDays)
            return new LicenseStatus(LicenseState.Active, info, left, $"Licence active jusqu'au {until}.");
        if (left >= 0)
            return new LicenseStatus(LicenseState.ExpiringSoon, info, left, $"Votre abonnement expire le {until} ({left} jour(s)). Pensez à le renouveler.");
        if (-left <= GraceDays)
            return new LicenseStatus(LicenseState.Grace, info, GraceDays + left, $"Votre abonnement a expiré le {until}. Renouvelez-le dans les {GraceDays + left} jour(s).");
        return new LicenseStatus(LicenseState.Expired, info, 0, $"Votre abonnement a expiré le {until}. Renouvelez-le pour continuer.");
    }
}
