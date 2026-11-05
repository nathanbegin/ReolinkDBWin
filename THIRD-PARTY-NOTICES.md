# Avis concernant les composants tiers

Vigie Widget utilise les composants suivants. Leurs licences s'appliquent à ces composants.

| Composant | Licence | Source |
|---|---|---|
| LibVLC (VideoLAN.LibVLC.Windows) | LGPL 2.1 ou ultérieure | https://code.videolan.org/videolan/vlc |
| LibVLCSharp, LibVLCSharp.WPF | LGPL 2.1 ou ultérieure | https://code.videolan.org/videolan/LibVLCSharp |
| Microsoft.Toolkit.Uwp.Notifications | MIT | https://github.com/CommunityToolkit/WindowsCommunityToolkit |
| .NET, WPF, Windows Forms, System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet |

## LGPL (LibVLC / LibVLCSharp)
LibVLC et LibVLCSharp sont liés **dynamiquement** (bibliothèques séparées `libvlc.dll`, `libvlccore.dll`, `LibVLCSharp*.dll` dans le dossier d'installation). Conformément à la LGPL :
- vous pouvez remplacer ces bibliothèques par une version modifiée compatible ;
- le code source de ces composants est disponible aux adresses ci-dessus (ou sur demande auprès de l'éditeur, pendant au moins trois ans) ;
- le texte de la licence est disponible à https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html.

Certains modules de LibVLC sont sous GPL ; Vigie Widget n'utilise que la bibliothèque LibVLC distribuée par VideoLAN dans le paquet NuGet officiel.

## Marques
Reolink, Frigate, Home Assistant, VLC et Windows sont des marques de leurs propriétaires respectifs. Vigie Widget est un produit indépendant compatible avec ces produits, sans affiliation.
