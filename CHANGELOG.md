# Journal des modifications

Toutes les modifications notables de **Vigie Widget** sont consignées ici.
Format : [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) — versions : [SemVer](https://semver.org/lang/fr/)
(MAJEUR.MINEUR.CORRECTIF : correctif = corrections seulement, mineur = nouvelles fonctions compatibles).

## [1.5.2] — 2026-11-09

### Corrigé
- **Plantage « mémoire insuffisante » après plusieurs heures** : la mémoire réservée par l'application
  grossissait d'environ 600 Mo par heure jusqu'à l'arrêt du processus. En cause : le décodage par la carte
  graphique, qui allouait des ressources Direct3D à chaque ouverture de flux sans les rendre, combiné au
  défilement des caméras qui ouvre et ferme un flux toutes les quelques secondes.
  - Le décodage par la carte graphique est désormais **désactivé par défaut** (réactivable dans
    Paramètres › Vidéo en direct › « Décodage par la carte graphique »).
  - Widget masqué avec « Garder le flux ouvert » : le défilement est **suspendu** tant que personne ne regarde
    (il reprend à l'affichage) ; les flux restent prêts pour un affichage instantané.
  - Qualité adaptative : plus d'allers-retours de qualité à l'ouverture du widget ; un flux n'est rouvert que
    si cela change vraiment l'image, et le retour au flux léger attend 20 secondes de petite taille.
- **Garde-fou** : si la mémoire réservée dépasse 3 Go, l'application redémarre d'elle-même, proprement,
  pendant que le widget est masqué (consigné dans le journal).
- Relance après une mise à jour ou un redémarrage : la nouvelle instance attend que l'ancienne soit fermée.
- Journal : les messages VLC sans conséquence (« Failed to set on top », « buffer deadlock prevented ») ne
  sont plus enregistrés.

## [1.5.1] — 2026-11-06

### Corrigé
- Mise à jour intégrée : sous Windows, l'installateur téléchargé restait verrouillé pendant la vérification
  de son empreinte, ce qui empêchait de l'installer (ou de supprimer un fichier corrompu).
- Compilation GitHub : actions mises à jour (Node.js 20 obsolète).

## [1.5.0] — 2026-11-06

### Ajouté
- **Mises à jour intégrées** : vérification automatique (au démarrage puis chaque jour) ou depuis le menu
  « Rechercher des mises à jour… » ; affichage des nouveautés, téléchargement (empreinte SHA-256 vérifiée)
  et installation silencieuse par-dessus la version actuelle, puis redémarrage. Paramètres, caméras et
  licence sont conservés. Option « Ignorer cette version ».
- **Qualité adaptative** (activée par défaut) : flux léger (sous-flux) et décodage à la taille réellement
  affichée ; bascule sans coupure vers le flux principal quand la vidéo est agrandie ou zoomée.
- Champ **flux léger** par caméra et pour la sonnette en restream Frigate ; détection automatique des
  sous-flux go2rtc (`_sub`, `_low`, `_lq`…) lors de l'import.
- Version affichée et vérification des mises à jour dans Paramètres › Général.

### Amélioré
- **Consommation mémoire fortement réduite** : threads de décodage VLC plafonnés (2 à 4 au lieu d'environ
  un par cœur), tampons d'image à la taille affichée, fenêtre d'alerte en flux léger, mémoire rendue à
  Windows quand le widget, l'alerte ou l'historique se ferment, ramasse-miettes plus économe.

## [1.4.1] — 2026-11-06

### Corrigé
- **Fermetures inattendues** : les opérations du lecteur vidéo (démarrage, arrêt, libération) sont
  désormais exécutées une à la fois ; un flux ne peut plus être libéré pendant son démarrage.
- La mémoire d'une image n'est plus libérée pendant sa copie à l'écran.
- Aucune erreur ne peut plus remonter dans le code natif de VLC (rappels vidéo protégés).
- Historique : le lecteur n'est plus libéré pendant un arrêt en cours.
- Sortie de l'application : le moteur vidéo n'est plus libéré pendant la fermeture des flux.

### Ajouté
- Journal de diagnostic : détection d'une fermeture brutale au lancement suivant avec recopie des erreurs
  Windows, raison des fermetures et du masquage du widget, cycle de vie des flux, erreurs VLC, mémoire
  toutes les 10 minutes ; menu « Ouvrir le journal de diagnostic ».

## [1.4.0] — 2026-11-06

### Ajouté
- **Fenêtre d'alerte Vigie** (style par défaut) : image entière puis vidéo en direct de la caméra concernée,
  boutons sous l'image, compte à rebours.
- **Mise en avant des détections** dans le widget : caméra concernée en plein cadre, mosaïque s'il y en a
  plusieurs, tant que l'événement Frigate est en cours.
- Option « Ouvrir aussi le widget lors d'une détection ».

### Modifié
- Notification Windows : l'instantané s'affiche en entier (image en ligne au lieu de la bannière rognée).
- Bouton « premier plan / arrière-plan » : nouvelle icône et état activé/désactivé visible.

## [1.3.0] — 2026-11-05

### Ajouté
- Restream Frigate : hôte et identifiants RTSP distincts de l'adresse web (Frigate derrière un proxy HTTPS).
- Liste des flux go2rtc réels (« Charger les flux ») et import des caméras avec ces noms.
- Boutons « Tester le flux » et « Tester cette caméra » avec diagnostic précis.

## [1.2.0] — 2026-11-05

### Modifié
- Le produit s'appelle désormais **Vigie Widget** (exécutable, installateur, dossiers de données, licence).
  Les données de l'ancien nom sont reprises automatiquement.

## [1.1.0] — 2026-11-05

### Ajouté
- Plusieurs caméras : modes une caméra, mosaïque et défilement préchargé avec transitions.
- Zoom à la molette et voyant d'état.
- Licence annuelle (essai de 14 jours), installateur, documents de vente.

## [1.0.0] — 2026-11-05

### Ajouté
- Première version : widget vidéo en direct par raccourci clavier, notifications (Reolink, Frigate,
  Home Assistant), détection de la sonnette sur le réseau, historique Frigate, paramètres.
