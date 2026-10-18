# Livraisons avec mise à jour intégrée

Le payload Windows x64 comprend le Switcher autonome, son relais Framework,
`SwitcherLauncher.exe`, `SwitcherCodexBridge.exe` avec sa configuration et le plugin
`creezio-relay`. Le lanceur est compilé depuis
`src/*.cs`, avec l’entrée `Creezio.Switcher.SwitcherLauncher`. Les exécutables et
`CodexAccountSwitcher.dll` portent la même version réelle, sans préfixe `v`.

## Préparer une livraison

Mettre à jour la version dans `src/Program.cs`, `src/RelayWorker.cs` et le titre du
README. Exécuter les tests du changement avant l’unique compilation finale.
Sur le poste de développement, réutiliser le runtime immuable existant :

```powershell
./scripts/build-portable-reuse.ps1 -OutputDirectory outputs/vVERSION -RuntimeDirectory outputs/v0.16.1-beta.1
./scripts/package-desktop.ps1 -SkipBuild -BinaryDirectory outputs/vVERSION
./scripts/test-update-package.ps1 -BinaryDirectory outputs/vVERSION
```

Remplacer `VERSION` par la version réelle, par exemple `0.28.0-beta.8`. Un SDK et
les packs officiels déjà en cache sont requis pour le build réutilisant le runtime.
Les assemblages du runtime sont vérifiés puis liés physiquement ; ils ne sont
jamais réécrits. La CI prépare un payload autonome sur son runner, puis applique
les mêmes contrôles de packaging. Les scripts exigent 20 Gio libres avant le
packaging. Ils ne créent aucune copie complète intermédiaire du payload.
Les builds portables écrivent le manifeste avec `package-update.ps1 -ManifestOnly`
après publication, sans produire de ZIP. L’application peut ainsi adopter ce
dossier vérifié avant de préparer une archive de release.

`package-desktop.ps1` ajoute la documentation puis appelle `package-update.ps1`
lorsque le lanceur est présent. Les anciens payloads sans lanceur gardent le
parcours ZIP historique. Pour un payload déjà préparé, on peut appeler directement :

```powershell
./scripts/package-update.ps1 -BinaryDirectory outputs/vVERSION
```

Le manifeste `switcher-update.json` déclare `schemaVersion: 1`, `platform:
windows-x64`, `healthProtocol: 1`, `entrypoint: CodexAccountSwitcher.exe` et tous
les fichiers du ZIP sauf lui-même : chemin relatif avec `/`, longueur et SHA-256
en minuscules. Les liens symboliques, jonctions et doublons de chemins sont
refusés. Les octets lus pendant la compression doivent encore correspondre au
manifeste. Aucun binaire n’est exécuté par le packaging ou son test.

À partir de `0.29.0-beta.3`, le manifeste doit également contenir
`SwitcherCodexBridge.exe` et `SwitcherCodexBridge.exe.config`. La version réelle
du pont doit correspondre à celle de la livraison. Les manifestes des versions
antérieures restent lisibles sans ces deux fichiers ; `healthProtocol` reste à 1.

Le pont est préparé pour les instances au prochain lancement de leur interface
Codex. Une interface déjà ouverte avec une ancienne version doit être relancée
une seule fois pour activer ce transport des objectifs. La mise à jour ne ferme
pas automatiquement ces interfaces. Windows, Switcher Live et l’API continuent
d’utiliser les capacités annoncées par l’instance pour chaque action.

## Publier et vérifier

Les deux assets attendus sont exactement :

- `CodexAccountSwitcher-VERSION-windows-x64.zip`
- `SHA256SUMS-VERSION.txt`

Pousser le tag `vVERSION` dans le dépôt officiel déclenche la CI. Elle vérifie le
tag contre la version réelle des binaires, termine tous les tests, puis crée une
release en brouillon avec ces deux assets. Le job de publication est limité aux
pushs de tags du dépôt officiel ; une branche, une PR, un fork ou un lancement
manuel ne publie rien. Seul ce job reçoit `contents: write`.

La CI conserve le statut prerelease pour une bêta. Pour une publication manuelle,
créer également un brouillon et joindre ces deux fichiers sans les renommer.
Le résultat du script expose `GitHubDigest`, au format `sha256:<empreinte ZIP>`.
Après upload, le champ `digest` de l’asset dans l’API GitHub doit correspondre à
cette valeur et au fichier SHA256SUMS avant de publier le brouillon. Le job CI ne
publie qu’après cette vérification des deux assets ; en cas de désaccord, il laisse
le brouillon en place. Les scripts locaux de build/packaging ne publient rien.

Une version publiée est immuable. `-Replace` permet uniquement de reprendre une
archive locale qui n’a pas été publiée. Ne pas republier d’autres octets sous le
même tag/version. Tester ensuite la recherche, le téléchargement et la préparation
depuis l’application ; une mise à jour active des fichiers versionnés, sans
écraser les exécutables utilisés par des hôtes, workers ou connexions MCP.
