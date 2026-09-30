# LanLink

Transfert de fichiers, synchronisation de dossiers, chat et contrôle à distance (écran, souris/clavier, presse-papiers, son, multi-écrans) entre PC Windows d'un réseau local.
C# / .NET 8 — interface WPF, cœur réseau indépendant et testé.

## Structure

| Projet | Rôle |
|---|---|
| `src/LanLink.Core` | Protocole, TLS 1.3, authentification, transfert avec reprise, découverte, stockage |
| `src/LanLink.App` | Interface WPF (MVVM), icône de notification, thèmes sombre/clair |
| `tests/LanLink.Core.Tests` | Tests unitaires et de bout en bout (vrai TLS sur localhost) |

```
dotnet build
dotnet test
dotnet run --project src/LanLink.App
```

Ports : TCP 45870 (tout le trafic), UDP 45871 (découverte).

## Sécurité

- **TLS 1.3** avec certificats auto-signés par appareil (ECDSA P-256), authentification mutuelle des certificats.
- **Mot de passe permanent par PC** (12 caractères minimum), dérivé en Argon2id. Après le handshake TLS, les deux
  côtés échangent des preuves HMAC liées aux empreintes des deux certificats : un intermédiaire ne peut pas relayer.
- Le client refuse des paramètres Argon2 trop faibles annoncés par le serveur. 5 échecs bloquent une adresse 5 minutes.
- Identité et vérificateur du mot de passe chiffrés au repos avec DPAPI (`%LOCALAPPDATA%\LanLink\identity.bin`).
- Noms et chemins reçus assainis (traversée de répertoires, noms réservés Windows, flux NTFS).

**Limite connue** : ce n'est pas un vrai PAKE. Quelqu'un qui se fait passer pour un PC peut recueillir une preuve et
tenter de deviner le mot de passe hors ligne (coût Argon2 par essai) : choisis une phrase de passe longue.
Remplacer le handshake par OPAQUE/SPAKE2 supprimerait cette limite.

## Protocole (v1)

Trames `[type:1][longueur:4 BE][charge utile]` sur TLS. Handshake : `Hello` (sel, paramètres Argon2, nonce) →
`ClientAuth` (preuve) → `ServerAuth` (preuve). Ensuite des requêtes : `Offer` (transfert), `Chat`, `RemoteRequest`
(la connexion devient alors dédiée à la session : `Video`/`VideoAck`, `Input`, `Clipboard`, `Audio`, `RemoteSettings`, `RemoteEnd`),
`SyncPairRequest` (appairage) et `SyncOpen` (passe de synchronisation dédiée : `SyncListRequest`/`SyncListing`, `SyncGet`,
`SyncPut`, `SyncDelete`, `SyncDone`).
Transfert : par fichier, `FileStart` → `ResumeInfo` (octets déjà reçus + SHA-256 du préfixe) → `ResumeAck` →
`Data`… → `FileEnd` (SHA-256 complet) → `FileResult`. Les `.part` restent dans `<réception>\.lanlink` pour la reprise.

## Contrôle à distance

Onglet « Écran distant » : **Voir l'écran** (lecture seule) ou **Voir et contrôler**. Après le mot de passe, l'utilisateur du
PC partagé voit une fenêtre de demande et choisit ce qu'il autorise (contrôle, presse-papiers, son) ; une barre orange avec
un bouton « Arrêter » reste affichée pendant tout le partage. Le partage peut être désactivé dans Réglages.

- Image : JPEG par capture GDI, réglable (Économie / Équilibré / Netteté), un écran à la fois avec choix du moniteur,
  image non renvoyée si l'écran n'a pas changé, contrôle de flux (2 images non acquittées au maximum).
- Entrées : coordonnées normalisées dans l'écran choisi (multi-écrans et mise à l'échelle DPI sans décalage), lots
  d'événements avec fusion des déplacements ; toutes les touches et boutons encore enfoncés sont relâchés à la fin de session
  ou à la coupure.
- Presse-papiers texte/image dans les deux sens (2 Mo max), son du PC (WASAPI loopback, PCM 16 bits).
- Limites : le bureau sécurisé (UAC, Ctrl+Alt+Suppr) n'est pas visible ni pilotable ; les applications lancées en
  administrateur ignorent les entrées tant que LanLink n'est pas lui-même administrateur ; le curseur distant est affiché
  en flèche générique.

## Synchronisation de dossiers

Onglet « Synchronisation » : **Créer avec le destinataire du haut…** choisit un dossier local ; l'autre PC voit une
demande, choisit son propre dossier et accepte. La synchronisation est **bidirectionnelle** :

- Le PC qui l'a créée (initiateur) se connecte toutes les minutes et 3 s après une modification locale ; l'autre PC est
  passif. Les changements faits sur le PC passif sont donc repris à la passe suivante (≤ 1 min). Si l'adresse IP de l'autre
  PC change, il est retrouvé par son nom grâce à la découverte réseau.
- Chaque côté compare son dossier au **dernier état synchronisé** : fichier nouveau = copié, modifié d'un côté = propagé,
  supprimé d'un côté et inchangé de l'autre = supprimé partout. Modifié des deux côtés (ou modifié d'un côté et supprimé de
  l'autre) : la version la plus récente est gardée.
- **Rien n'est perdu** : tout fichier écrasé ou supprimé va dans `.lanlink-sync/trash/<date>/` (30 jours).
- Garde-fous : suppression massive refusée (≥ 10 fichiers et plus de la moitié de l'historique), dossier absent refusé,
  fichiers modifiés depuis moins de 2 s reportés, certificat de l'autre PC épinglé (un autre certificat bloque la paire),
  chemins validés à chaque requête, empreinte SHA-256 vérifiée pour chaque fichier.
- Le mot de passe de l'autre PC est conservé chiffré (DPAPI) dans `sync-pairs.json` côté initiateur.
- Limites : les dossiers vides ne sont pas synchronisés ; un gros fichier interrompu recommence à zéro à la passe suivante ;
  une passe termine quand la liste des fichiers a été comparée, donc de très grands dossiers (centaines de milliers de
  fichiers) sont lents à analyser ; pas de renommage détecté (un renommage = suppression + nouveau fichier).

## À faire

- Synchronisation de dossiers, presse-papiers partagé
- Capture DXGI (plus rapide sur grands écrans), encodage vidéo (H.264) à la place du JPEG
- Reprise des gros fichiers en synchronisation, détection des renommages
- Transferts parallèles, icône dédiée, installateur
- Transport interchangeable pour Internet (relais / NAT traversal)
