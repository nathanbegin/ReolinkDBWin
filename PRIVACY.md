# Politique de confidentialité

**ReolinkDBWin** fonctionne entièrement sur votre ordinateur et votre réseau local.

- **Aucune télémétrie** : l'application n'envoie aucune donnée à l'éditeur ni à un service tiers.
- **Connexions réseau** : uniquement vers les appareils que vous configurez (sonnette/caméras, serveur Frigate, serveur Home Assistant).
- **Données locales** :
  - paramètres : `%AppData%\ReolinkDBWin\settings.json` (mots de passe et jetons chiffrés avec votre compte Windows, DPAPI) ;
  - journal de dépannage : `%AppData%\ReolinkDBWin\app.log` ;
  - cache des images et clips : `%LocalAppData%\ReolinkDBWin\cache` (supprimé automatiquement après 2 jours) ;
  - captures d'écran : `Images\Sonnette Reolink` ;
  - licence : `HKEY_CURRENT_USER\Software\ReolinkDBWin\License`.
- **Clé de licence** : elle contient le nom et le courriel fournis lors de l'achat ; elle est vérifiée hors ligne, sans connexion à un serveur.

Pour tout effacer : désinstallez l'application puis supprimez les dossiers ci-dessus.
