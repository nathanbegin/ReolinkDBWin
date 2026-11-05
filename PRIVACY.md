# Politique de confidentialité

**Vigie Widget** fonctionne entièrement sur votre ordinateur et votre réseau local.

- **Aucune télémétrie** : l'application n'envoie aucune donnée à l'éditeur ni à un service tiers.
- **Connexions réseau** : uniquement vers les appareils que vous configurez (sonnette/caméras, serveur Frigate, serveur Home Assistant).
- **Données locales** :
  - paramètres : `%AppData%\VigieWidget\settings.json` (mots de passe et jetons chiffrés avec votre compte Windows, DPAPI) ;
  - journal de dépannage : `%AppData%\VigieWidget\app.log` ;
  - cache des images et clips : `%LocalAppData%\VigieWidget\cache` (supprimé automatiquement après 2 jours) ;
  - captures d'écran : `Images\Vigie Widget` ;
  - licence : `HKEY_CURRENT_USER\Software\VigieWidget\License`.
- **Clé de licence** : elle contient le nom et le courriel fournis lors de l'achat ; elle est vérifiée hors ligne, sans connexion à un serveur.

Pour tout effacer : désinstallez l'application puis supprimez les dossiers ci-dessus.
