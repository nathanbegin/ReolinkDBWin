namespace ReolinkDBWin.Services;

/// <summary>Noms français des objets Frigate.</summary>
public static class LabelNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = "Personne",
        ["car"] = "Voiture",
        ["truck"] = "Camion",
        ["bus"] = "Autobus",
        ["motorcycle"] = "Moto",
        ["bicycle"] = "Vélo",
        ["boat"] = "Bateau",
        ["dog"] = "Chien",
        ["cat"] = "Chat",
        ["bird"] = "Oiseau",
        ["horse"] = "Cheval",
        ["bear"] = "Ours",
        ["deer"] = "Chevreuil",
        ["fox"] = "Renard",
        ["raccoon"] = "Raton laveur",
        ["squirrel"] = "Écureuil",
        ["rabbit"] = "Lapin",
        ["package"] = "Colis",
        ["face"] = "Visage",
        ["license_plate"] = "Plaque d'immatriculation",
        ["amazon"] = "Livreur Amazon",
        ["ups"] = "Livreur UPS",
        ["fedex"] = "Livreur FedEx",
        ["usps"] = "Livreur USPS",
        ["dhl"] = "Livreur DHL",
        ["purolator"] = "Livreur Purolator",
        ["canada_post"] = "Postes Canada",
        ["umbrella"] = "Parapluie",
        ["backpack"] = "Sac à dos",
    };

    public static string Get(string label) =>
        Names.TryGetValue(label, out var n) ? n : (label.Length > 0 ? char.ToUpper(label[0]) + label[1..].Replace('_', ' ') : label);

    public static IReadOnlyList<string> Defaults { get; } = ["person", "car", "dog", "cat", "package", "bicycle", "motorcycle", "face"];
}
