namespace ReolinkDBWin.Core.Models;

/// <summary>Type de détection normalisé, quelle que soit la source (Reolink, Frigate, Home Assistant).</summary>
public enum DetectionType
{
    Doorbell,
    Person,
    Vehicle,
    Animal,
    Package,
    Face,
    Motion,
    Other,
}

public enum EventSource
{
    Reolink,
    Frigate,
    HomeAssistant,
    Test,
}

/// <summary>Un événement de détection prêt à être notifié.</summary>
public sealed record DetectionEvent
{
    public required EventSource Source { get; init; }
    public required DetectionType Type { get; init; }
    /// <summary>Libellé brut de la source (ex. « people », « person », « binary_sensor.porte_visitor »).</summary>
    public string? RawLabel { get; init; }
    public string? Camera { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public double? Score { get; init; }
    /// <summary>Identifiant d'événement Frigate, si connu (permet d'ouvrir le clip).</summary>
    public string? FrigateEventId { get; init; }
    /// <summary>Image JPEG associée (instantané), si déjà disponible.</summary>
    public byte[]? Image { get; init; }
}

public static class DetectionTypeInfo
{
    /// <summary>Libellé français affiché à l'utilisateur.</summary>
    public static string DisplayName(DetectionType type) => type switch
    {
        DetectionType.Doorbell => "Sonnette",
        DetectionType.Person => "Personne",
        DetectionType.Vehicle => "Véhicule",
        DetectionType.Animal => "Animal",
        DetectionType.Package => "Colis",
        DetectionType.Face => "Visage",
        DetectionType.Motion => "Mouvement",
        _ => "Autre",
    };

    /// <summary>Titre de notification.</summary>
    public static string NotificationTitle(DetectionType type) => type switch
    {
        DetectionType.Doorbell => "🔔 Quelqu'un sonne à la porte",
        DetectionType.Person => "🧍 Personne détectée",
        DetectionType.Vehicle => "🚗 Véhicule détecté",
        DetectionType.Animal => "🐾 Animal détecté",
        DetectionType.Package => "📦 Colis détecté",
        DetectionType.Face => "🙂 Visage détecté",
        DetectionType.Motion => "👀 Mouvement détecté",
        _ => "Événement détecté",
    };

    /// <summary>
    /// Normalise un libellé (Reolink : people, dog_cat, visitor, md… ; Frigate : person, car, dog… ;
    /// Home Assistant : suffixe d'entité) vers un <see cref="DetectionType"/>.
    /// </summary>
    public static DetectionType FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return DetectionType.Other;
        var l = label.Trim().ToLowerInvariant();
        return l switch
        {
            "visitor" or "doorbell" or "ring" or "press" or "button" => DetectionType.Doorbell,
            "people" or "person" or "human" => DetectionType.Person,
            "vehicle" or "car" or "truck" or "bus" or "motorcycle" or "bicycle" or "boat" => DetectionType.Vehicle,
            "dog_cat" or "dog" or "cat" or "pet" or "animal" or "bird" or "horse" or "bear" or "deer" or "fox" or "raccoon" or "squirrel" or "rabbit" => DetectionType.Animal,
            "package" or "parcel" or "amazon" or "ups" or "fedex" or "usps" or "dhl" or "purolator" or "canada_post" => DetectionType.Package,
            "face" => DetectionType.Face,
            "md" or "motion" => DetectionType.Motion,
            _ => DetectionType.Other,
        };
    }

    /// <summary>Déduit le type à partir d'un identifiant d'entité Home Assistant (ex. binary_sensor.porte_visitor).</summary>
    public static DetectionType FromEntityId(string entityId)
    {
        var name = entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId;
        name = name.ToLowerInvariant();
        // Ordre important : on teste les suffixes les plus spécifiques d'abord.
        (string suffix, DetectionType type)[] map =
        [
            ("visitor", DetectionType.Doorbell), ("visiteur", DetectionType.Doorbell), ("doorbell", DetectionType.Doorbell),
            ("ding", DetectionType.Doorbell),
            ("person", DetectionType.Person), ("people", DetectionType.Person), ("personne", DetectionType.Person),
            ("vehicle", DetectionType.Vehicle), ("car", DetectionType.Vehicle), ("vehicule", DetectionType.Vehicle),
            ("pet", DetectionType.Animal), ("animal", DetectionType.Animal), ("dog_cat", DetectionType.Animal),
            ("dog", DetectionType.Animal), ("cat", DetectionType.Animal),
            ("package", DetectionType.Package), ("colis", DetectionType.Package),
            ("face", DetectionType.Face), ("visage", DetectionType.Face),
            ("motion", DetectionType.Motion), ("mouvement", DetectionType.Motion),
        ];
        // 1) suffixe exact (binary_sensor.sonnette_person) ; 2) mot interne (binary_sensor.sonnette_person_occupancy).
        foreach (var (suffix, type) in map)
            if (name.EndsWith("_" + suffix) || name == suffix) return type;
        foreach (var (suffix, type) in map)
            if (name.Contains("_" + suffix + "_")) return type;
        return DetectionType.Other;
    }
}
