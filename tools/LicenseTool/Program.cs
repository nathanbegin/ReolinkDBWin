// Outil de l'éditeur : génère la paire de clés et émet les clés de licence des clients.
//   init   --out <dossier>                                   crée private-key.pem (à garder secret) et affiche la clé publique
//   issue  --key <private-key.pem> --name "Nom" [--email x] [--days 365] [--plan annual] [--id ID]
//   verify --public <base64> <clé>
using System.Security.Cryptography;
using ReolinkDBWin.Core.Licensing;

var cmd = args.FirstOrDefault()?.ToLowerInvariant();
string? Opt(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

switch (cmd)
{
    case "init":
    {
        var dir = Opt("out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reolinkdbwin-license");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "private-key.pem");
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} existe déjà. Supprimez-le d'abord si vous voulez vraiment une nouvelle paire (les anciennes licences deviendraient invalides).");
            return 1;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine($"Clé privée écrite dans : {path}");
        Console.WriteLine("⚠ Gardez-la secrète, sauvegardez-la hors ligne, et ne la mettez JAMAIS dans le dépôt Git.");
        Console.WriteLine();
        Console.WriteLine("Clé publique à coller dans src/ReolinkDBWin.Core/Licensing/LicensePublicKey.cs :");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "issue":
    {
        var keyPath = Opt("key");
        var name = Opt("name");
        if (keyPath is null || name is null)
        {
            Console.Error.WriteLine("Usage : issue --key private-key.pem --name \"Nom du client\" [--email courriel] [--days 365] [--plan annual] [--id ID]");
            return 1;
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = int.TryParse(Opt("days"), out var d) ? d : 365;
        var info = new LicenseInfo
        {
            Id = Opt("id") ?? "L-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Name = name,
            Email = Opt("email") ?? "",
            Plan = Opt("plan") ?? "annual",
            Issued = today,
            Expires = today.AddDays(days),
        };
        Console.WriteLine($"# {info.Id} — {info.Name} — valide jusqu'au {info.Expires:yyyy-MM-dd}");
        Console.WriteLine(LicenseKey.Create(info, key));
        return 0;
    }
    case "verify":
    {
        var pub = Opt("public");
        var licence = args.LastOrDefault();
        if (pub is null || licence is null || licence == pub)
        {
            Console.Error.WriteLine("Usage : verify --public <clé publique base64> <clé de licence>");
            return 1;
        }
        using var k = LicenseKey.ImportPublicKey(pub);
        if (LicenseKey.TryVerify(licence, k, out var info, out var error))
        {
            Console.WriteLine($"Valide : {info!.Id} — {info.Name} <{info.Email}> — {info.Plan} — du {info.Issued:yyyy-MM-dd} au {info.Expires:yyyy-MM-dd}");
            return 0;
        }
        Console.Error.WriteLine("Invalide : " + error);
        return 2;
    }
    default:
        Console.WriteLine("Commandes : init | issue | verify  (voir docs/VENTE.md)");
        return cmd is null ? 0 : 1;
}
