# Ajouter des caméras de différentes marques

Vigie Widget lit tout flux **RTSP** (et RTMP / HTTP). Trois façons d'ajouter une caméra, de la plus simple à la plus manuelle :

| Méthode | Quand l'utiliser |
|---|---|
| **1. Importer de Frigate** | Vos caméras sont déjà dans Frigate : un clic, et la caméra ne reçoit aucune connexion de plus. |
| **2. Assistant « Ajouter… »** | Caméra hors Frigate : choisissez la marque, ou **ONVIF** pour que la caméra donne elle-même son adresse. |
| **3. Adresse saisie à la main** | Marque absente de la liste, ou adresse particulière. |

Tout se passe dans **Paramètres › Caméras**.

## 1. Par Frigate (recommandé si vous l'utilisez)

1. Onglet **Frigate** : adresse du serveur, puis « Tester et charger les caméras ».
2. Onglet **Vidéo en direct** : « Hôte RTSP » = l'IP locale du serveur Frigate (port 8554).
3. Onglet **Caméras** : **Importer de Frigate**.

Chaque caméra utilise son flux go2rtc (section `go2rtc › streams` de la configuration de Frigate). Si un flux `nom_sub` existe, il est repris comme flux léger. Les détections de Frigate sont associées automatiquement à la bonne caméra.

## 2. Assistant « Ajouter… »

1. **Marque** : choisissez-la dans la liste (ou **ONVIF**).
2. **Adresse IP** : saisissez-la, ou cliquez **Rechercher (ONVIF)** pour lister les caméras du réseau.
3. **Utilisateur / mot de passe** de la caméra.
4. - Marque connue : les deux adresses (principal et léger) sont remplies ; **Tester le flux** vérifie l'adresse et le port.
   - ONVIF : cliquez **Interroger la caméra** ; le profil le plus grand devient le flux principal, le plus petit le flux léger.
5. **Ajouter**, puis **Enregistrer** les paramètres.

Les adresses peuvent être corrigées à la main avant d'ajouter. Celles qui contiennent un mot de passe sont **chiffrées** dans le fichier de paramètres.

### ONVIF : à vérifier sur la caméra
- ONVIF doit être **activé** (souvent désactivé par défaut), parfois avec un compte ONVIF distinct.
- **Port ONVIF** usuel : 80 ; **8000** (Reolink) ; 8080 ; **2020** (TP-Link Tapo) ; 8899 (certaines caméras génériques).
- L'**heure** de la caméra doit être juste : l'authentification ONVIF la prend en compte.

## 3. Adresses RTSP par marque

Remplacez `UTILISATEUR`, `MOTDEPASSE` et `IP`. Si le mot de passe contient des caractères spéciaux (`@ : / ? #`…), l'assistant les encode pour vous ; à la main, écrivez par exemple `%40` pour `@`.

| Marque | Flux principal | Flux léger | Remarques |
|---|---|---|---|
| **Reolink** | `rtsp://UTILISATEUR:MOTDEPASSE@IP:554/h264Preview_01_main` | `…/h264Preview_01_sub` | Activer RTSP (application Reolink › Réseau › Avancé › Configuration du port). Caméras H.265 : `h265Preview_01_main`. Sur un enregistreur : `_02_`, `_03_`… Les modèles à batterie n'ont pas de RTSP. |
| **Hikvision**, Annke, HiLook | `rtsp://UTILISATEUR:MOTDEPASSE@IP:554/Streaming/Channels/101` | `…/Channels/102` | Enregistreur : `201`/`202` pour la caméra 2, etc. |
| **Dahua**, Amcrest, Lorex, Imou | `rtsp://UTILISATEUR:MOTDEPASSE@IP:554/cam/realmonitor?channel=1&subtype=0` | `…&subtype=1` | |
| **TP-Link Tapo / VIGI** | `rtsp://UTILISATEUR:MOTDEPASSE@IP:554/stream1` | `…/stream2` | Créer un « compte caméra » dans l'application Tapo (Réglages avancés). |
| **Axis** | `rtsp://UTILISATEUR:MOTDEPASSE@IP/axis-media/media.amp` | `…/media.amp?resolution=640x360` | |
| **Uniview (UNV)** | `rtsp://UTILISATEUR:MOTDEPASSE@IP:554/media/video1` | `…/media/video2` | |
| **Foscam** | `rtsp://UTILISATEUR:MOTDEPASSE@IP:88/videoMain` | `…/videoSub` | Port 88 ou 554 selon le modèle. |
| **Ubiquiti UniFi Protect** | adresse affichée par Protect (`rtsp://IP:7447/…`) | — | Activer RTSP pour la caméra dans Protect et copier l'adresse. |
| **Eufy** (certains modèles filaires) | `rtsp://UTILISATEUR:MOTDEPASSE@IP/live0` | — | Activer RTSP/NAS dans l'application. |
| **Wyze**, Ring, Nest, Blink, Arlo | — | — | Pas de RTSP natif. Wyze : passer par *docker-wyze-bridge*. Les autres ne sont pas accessibles en direct sur le réseau local. |

Ces formats sont ceux documentés par les fabricants ; ils varient selon le modèle et le firmware. En cas de doute, utilisez **ONVIF**, ou cherchez « RTSP URL » suivi du modèle.

## Conseils

- **Flux léger** : renseignez-le quand il existe. Avec la qualité adaptative, il sert pour le petit widget, la mosaïque et les alertes, et réduit beaucoup le processeur et la mémoire.
- **Wi-Fi** : gardez « Forcer RTSP sur TCP » coché.
- **Plusieurs lecteurs sur la même caméra** : beaucoup de caméras limitent le nombre de connexions. Si Frigate lit déjà la caméra, préférez son restream go2rtc.
- **Détections** : pour qu'une détection Frigate mette la caméra en avant, renseignez « Caméra Frigate » (nom de la caméra dans Frigate) dans l'éditeur de la caméra.
- **Tester** : le bouton « Tester cette caméra » indique si l'hôte est injoignable, si le flux n'existe pas, ou si des identifiants sont demandés.
