# Consignes du projet (Vigie Widget)

## À chaque modification livrée (obligatoire)
1. **Incrémenter la version** dans `Directory.Build.props` (`<Version>`), selon SemVer :
   - correctif seulement → CORRECTIF (1.5.0 → 1.5.1) ;
   - nouvelle fonction compatible → MINEUR (1.5.1 → 1.6.0) ;
   - changement incompatible (paramètres, licence…) → MAJEUR.
2. **Documenter** dans `CHANGELOG.md` : nouvelle section `## [x.y.z] — AAAA-MM-JJ` en tête, rubriques
   `Ajouté` / `Modifié` / `Corrigé` / `Supprimé`, en français, du point de vue de l'utilisateur.
   La CI refuse une release dont l'étiquette n'a pas d'entrée dans le CHANGELOG ; ces notes sont affichées
   dans la fenêtre de mise à jour de l'application.
3. Mettre à jour le `README.md` si le comportement visible change.
4. **Mises à jour sans perte** : ne jamais casser la lecture de `settings.json` existant (ajouter des
   propriétés avec valeur par défaut ; migrer dans `AppSettings.Normalize()` si besoin), ni changer
   `AppId` de l'installateur, les dossiers de données ou le registre sans migration.

## Publier une version
Dans GitHub : Actions › Build › « Run workflow » sur la branche voulue, cocher « publier » (ou `git tag vX.Y.Z` identique à `<Version>` puis `git push origin vX.Y.Z`) : GitHub Actions teste, publie
l'installateur et l'archive, avec les notes du CHANGELOG. Les applications installées la proposent
automatiquement (flux : `AppInfo.UpdateFeedUrl`).

## Vérifications avant commit
- `dotnet build ReolinkDBWin.sln` sans avertissement ; `dotnet test tests/ReolinkDBWin.Core.Tests`.
- La logique testable va dans `src/ReolinkDBWin.Core` (multiplateforme, avec tests) ; l'interface WPF
  dans `src/ReolinkDBWin`.
- Aucune information personnelle dans le code (adresses, mots de passe, domaines) ; crédits : Nathan Bégin.
