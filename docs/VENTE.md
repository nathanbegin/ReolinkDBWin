# Guide de mise en vente

Ce guide s'adresse à l'éditeur (vous). Il explique comment préparer, vendre et gérer les abonnements.

## 1. Créer votre paire de clés de licence (une seule fois)

```bash
dotnet run --project tools/LicenseTool -- init
```

- La **clé privée** est écrite dans `%USERPROFILE%\.reolinkdbwin-license\private-key.pem` (hors du dépôt).
  **Sauvegardez-la** (clé USB, gestionnaire de mots de passe). Si vous la perdez, vous ne pourrez plus émettre de clés valides ; si elle fuit, n'importe qui pourra en émettre.
- Copiez la **clé publique** affichée dans `src/ReolinkDBWin.Core/Licensing/LicensePublicKey.cs` (`Value = "..."`), puis commitez.

Tant que `Value` est vide, l'application est une « version de développement » sans vérification de licence. La compilation d'une release (étiquette `v*`) échoue volontairement si la clé publique est vide.

## 2. Émettre une licence pour un client

```bash
dotnet run --project tools/LicenseTool -- issue --key %USERPROFILE%\.reolinkdbwin-license\private-key.pem --name "Prénom Nom" --email client@exemple.com --days 365
```

Envoyez la clé (`RDW1-…`) au client : il la colle dans **Licence et à propos…** (menu de l'icône) ou à la fin de l'essai.
Renouvellement : émettez une nouvelle clé de 365 jours à partir de la date d'échéance (`--days` en conséquence).

Vérifier une clé : `dotnet run --project tools/LicenseTool -- verify --public <clé publique> <clé>`.

## 3. Comportement dans l'application
- **Essai** : 14 jours complets dès le premier lancement.
- **Abonnement actif** : rappel 14 jours avant l'échéance (bulle de notification).
- **Période de grâce** : 7 jours après l'échéance, l'application fonctionne encore avec un rappel.
- **Expiré** : le widget, l'historique et les notifications sont désactivés jusqu'à l'entrée d'une nouvelle clé.

Les clés sont vérifiées **hors ligne** (signature ECDSA P-256) : aucun serveur à maintenir. Limite : une clé peut être partagée entre plusieurs PC. Si cela devient un problème, une activation en ligne (limite d'appareils) pourra être ajoutée, par exemple avec l'API de licences de Lemon Squeezy ou Keygen.

## 4. Vendre
Plateformes « marchand officiel » qui encaissent, gèrent les abonnements annuels **et les taxes** (TPS/TVQ, TVA…) à votre place :
- **Lemon Squeezy** ou **Paddle** : abonnements, factures, taxes mondiales ; envoi de la clé par courriel (champ personnalisé ou automatisation par webhook qui appelle `LicenseTool issue`).
- **Gumroad** : plus simple, abonnements possibles.

Renseignez l'adresse de la page d'achat dans `src/ReolinkDBWin/Services/AppInfo.cs` (`PurchaseUrl`) : le bouton « Acheter / renouveler » apparaîtra.

Au Québec, l'inscription aux fichiers de la TPS/TVQ devient obligatoire au-delà de 30 000 $ de revenus sur quatre trimestres ; une plateforme « marchand officiel » simplifie beaucoup cela. Consultez un comptable pour votre situation.

## 5. Avant la première vente — liste de contrôle
- [ ] Clé publique intégrée (étape 1).
- [ ] `PurchaseUrl` renseignée.
- [ ] **Nom commercial** : évitez d'utiliser « Reolink » (marque déposée) dans le nom du produit. Préférez un nom à vous avec la mention « compatible avec les sonnettes Reolink ». Le nom affiché se change dans `AppInfo.ProductName`.
- [ ] **Dépôt GitHub privé** si vous ne voulez pas que le code (et donc une version sans licence) soit accessible ; le lien GitHub des crédits peut pointer vers une page publique de présentation.
- [ ] **Signature de code** (certificat Authenticode, ou Azure Trusted Signing) pour éviter l'avertissement SmartScreen « éditeur inconnu ».
- [ ] Relire `LICENSE.md` (CLUF), `PRIVACY.md` et `THIRD-PARTY-NOTICES.md` ; idéalement les faire valider par un juriste.
- [ ] Tester l'installateur sur un PC Windows propre.

## 6. Publier une version
1. Mettez à jour `<Version>` dans `Directory.Build.props`.
2. Créez une étiquette : `git tag v1.1.0` puis `git push origin v1.1.0`.
3. GitHub Actions compile, teste et publie `ReolinkDBWin-Setup-x.y.z.exe` (installateur) et l'archive portable dans la release.
