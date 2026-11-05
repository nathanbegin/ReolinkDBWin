# ReolinkDBWin — Sonnette Reolink sur Windows

Application Windows (zone de notification) pour une **sonnette Reolink** (filaire ou Wi‑Fi), avec intégration **Frigate** et **Home Assistant** :

- 🎥 **Widget vidéo en direct** toujours accessible : un raccourci clavier global (**Ctrl+Alt+D** par défaut) affiche/masque une petite fenêtre vidéo sans bordure, toujours au premier plan, déplaçable et redimensionnable.
- 🔔 **Notifications Windows** quand quelqu'un sonne, ou quand une personne, un véhicule, un animal ou un colis est détecté, avec image, et boutons « Voir en direct » / « Voir le clip ». Le widget peut s'ouvrir automatiquement quand on sonne.
- 🔍 **Détection automatique** de la sonnette sur le réseau (ONVIF + balayage du sous‑réseau), et test de connexion.
- 🗂️ **Historique Frigate** (**Ctrl+Alt+H**) : recherche par caméra, objet et période, vignettes, lecture des clips (vitesse 0,5× à 4×, avance rapide), instantanés et enregistrement des clips en MP4.

## Installation

1. Téléchargez `ReolinkDBWin-win-x64.zip` depuis l'onglet **Actions** (artefact de la dernière compilation) ou **Releases** du dépôt.
2. Décompressez‑le où vous voulez (ex. `C:\Programmes\ReolinkDBWin`) et lancez `ReolinkDBWin.exe`. Aucun prérequis : la version publiée inclut .NET et le lecteur vidéo (LibVLC).
3. Au premier lancement, la fenêtre de paramètres s'ouvre. L'application vit ensuite dans la zone de notification (près de l'horloge) : clic gauche = vidéo en direct, clic droit = menu.

Cochez « Lancer automatiquement à l'ouverture de session » (onglet Général) pour qu'elle démarre avec Windows.

## Configuration

### Sonnette (onglet « Sonnette »)

- Cliquez **Rechercher sur le réseau…** pour trouver la sonnette, ou saisissez son adresse IP.
- Identifiants : ceux de l'application Reolink (utilisateur `admin` par défaut).
- **Tester la connexion** affiche le modèle, le firmware et les détections que l'appareil prend en charge.
- Dans l'application Reolink, vérifiez que les ports **RTSP** et **HTTP** (ou HTTPS) sont activés :
  *Réglages de l'appareil › Réseau › Avancé › Configuration du port*. Si seul HTTPS est activé, cochez « HTTPS ».

Le mot de passe est chiffré avec votre compte Windows (DPAPI) dans `%AppData%\ReolinkDBWin\settings.json`.

### Vidéo en direct

| Source | URL utilisée |
|---|---|
| Sonnette, flux principal | `rtsp://utilisateur:motdepasse@IP:554/h264Preview_01_main` |
| Sonnette, flux secondaire (léger) | `rtsp://…/h264Preview_01_sub` |
| Restream go2rtc de Frigate | `rtsp://IP-frigate:8554/<nom du flux>` |
| URL personnalisée | ce que vous voulez (RTSP, RTMP, HTTP‑FLV…) |

Le **restream Frigate** est recommandé si votre sonnette est déjà dans Frigate : il n'ajoute aucune connexion sur la sonnette (utile en Wi‑Fi).

Raccourcis dans le widget : **M** son, **S** capture d'écran (dans *Images\Sonnette Reolink*), **R** reconnecter, **Échap** masquer, double‑clic sur la barre = agrandir/réduire.

### Notifications

Trois sources possibles (combinables, les doublons sont filtrés par un délai anti‑rafale) :

1. **Sonnette Reolink directement** : l'application interroge l'API de la sonnette (1 fois par seconde par défaut). L'appui sur le bouton est détecté sur les firmwares récents (commande `GetEvents`) ; les détections IA (personne, véhicule, animal, colis) et le mouvement sur tous.
2. **Frigate** : les nouveaux événements de la caméra choisie, avec la vignette de Frigate et un bouton pour ouvrir le clip.
3. **Home Assistant** : en temps réel via l'API WebSocket. Sélectionnez les entités de l'intégration Reolink (ex. `binary_sensor.sonnette_visiteur`, `binary_sensor.sonnette_personne`). Idéal pour l'appui sur le bouton si votre firmware ne le fournit pas via l'API HTTP. Il faut un **jeton d'accès longue durée** (profil Home Assistant › Sécurité).

Si aucune notification n'apparaît : *Paramètres Windows › Système › Notifications* (et le mode « Ne pas déranger »). Le bouton « Envoyer une notification de test » permet de vérifier.

### Frigate

- Adresse : `http://IP:5000` (port interne, sans authentification) ou `https://IP:8971` avec utilisateur/mot de passe (Frigate 0.14+).
- **Tester et charger les caméras**, puis choisissez la caméra de la sonnette.

## Dépannage

- Journal : `%AppData%\ReolinkDBWin\app.log` (lien dans le bas de la fenêtre de paramètres).
- « Erreur de lecture du flux » : vérifiez RTSP activé, l'adresse, le mot de passe ; essayez le flux secondaire ou le restream Frigate ; augmentez le tampon réseau.
- Raccourci clavier indisponible : il est déjà pris par une autre application ; choisissez‑en un autre (onglet Général).
- Les sonnettes Reolink limitent le nombre de sessions simultanées : si l'application Reolink, Home Assistant et Frigate sont tous connectés, privilégiez le restream Frigate pour la vidéo et Home Assistant pour les événements (et décochez « Surveiller directement la sonnette »).

## Compilation

Prérequis : SDK .NET 10.

```bash
dotnet test tests/ReolinkDBWin.Core.Tests
```

```bash
dotnet publish src/ReolinkDBWin/ReolinkDBWin.csproj -c Release -r win-x64 --self-contained true -o publish/ReolinkDBWin
```

Le workflow GitHub Actions `.github/workflows/build.yml` compile, teste et publie l'archive à chaque push ; une étiquette `v*` crée une release.

### Structure

```
src/ReolinkDBWin.Core     Bibliothèque multiplateforme (testée) : API Reolink, Frigate, Home Assistant,
                          découverte réseau, surveillance des événements, configuration
src/ReolinkDBWin          Application WPF : zone de notification, widget vidéo (LibVLC), notifications,
                          historique Frigate, paramètres, raccourcis globaux
tests/ReolinkDBWin.Core.Tests   Tests unitaires (xUnit)
```
