[CmdletBinding()]
param([switch]$SkipBuild,[string]$BinaryDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'outputs\v0.10.0-beta.1'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build-desktop.ps1') -OutputDirectory $bin -Portable}
foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.runtimeconfig.json','coreclr.dll','PresentationFramework.dll','CreezioRelay.exe','plugins')){if(-not(Test-Path -LiteralPath (Join-Path $bin $required))){throw "Livraison autonome incomplète : $required"}}
$instructions=@'
CODEX ACCOUNT SWITCHER — 0.10.0-beta.1 — WINDOWS X64

Extraire TOUT le ZIP et lancer CodexAccountSwitcher.exe.
Garder les DLL, fichiers JSON, exécutables et dossier plugins ensemble.
Le runtime .NET 10 de cette interface WPF est inclus. Aucun SDK à installer.
Le moteur de relais utilise .NET Framework 4.8, présent sur Windows 10/11.

Instances : donner un nom, connecter un compte permanent, préparer et ouvrir.
Le plugin et les skills sont installés et vérifiés automatiquement.
Dans Codex : « Délègue cette mission à Léa ». Le résultat revient dans ce chat.
Outils partagés : sélectionner un propriétaire, des outils et des clients.
Dans Codex : « Utilise les outils Pages de Principal pour modifier cette page ».
Aucun prompt envoyé au propriétaire. Codex doit rester ouvert sur les deux côtés.
Les fichiers joints ne sont pas transférés : publication de nouvelles versions
Sites indisponible dans le tunnel. Lire TOOL-TUNNEL.md pour les possibilités.
Comptes : consulter les limites et configurer leurs réinitialisations.
Réglages avancés : anciens canaux, projets, ressources et règles facultatives.
Tâches : envoyer un prompt vers un chat nouveau ou ciblé et lire sa réponse.
PC distants : partager des canaux avec invitations et droits révocables.
Assistance : recevoir une question, faire analyser et répondre au chat source.
Paramètres : thème, supervision, capacités, diagnostic et outils avancés.

La délégation est explicite par défaut. Aucun compte ni service n'est imposé.
Les réinitialisations concernent les limites d'utilisation, pas les achats de
crédits. L'automatisme s'active par compte et ne contourne aucune permission.

MISE À JOUR
Quitter l'ancien switcher via son icône près de l'horloge > Quitter.
Conserver les fenêtres Codex ouvertes. Lancer cette version depuis son dossier.
Les comptes, instances, configuration et tâches existants sont relus sur place.
Ne pas écraser ni supprimer un dossier utilisé par un hôte d'instance ou un MCP.
Les nouvelles tâches utilisent un format séparé des moteurs 0.6/0.7.
Ne pas faire tourner deux moteurs concurrents. L’intégration est mise à jour
automatiquement ; ouvrir un nouveau chat pour charger les nouveaux outils.
Une instance neuve nécessite un premier message dans Codex. Les permissions
natives des chats restent inchangées. Vérification périodique tant que le
switcher reste ouvert, même près de l’horloge (5 minutes, intervalle réglable).
Les anciens chats peuvent conserver leur MCP et leur ancien dossier de programme.

Fermer la fenêtre réduit le switcher dans la zone de notification.
Quitter le switcher ne ferme pas les instances Codex.
Codex Microsoft Store est nécessaire aux instances, Codex CLI aux comptes.
Le binaire n'est pas signé. Lire WINDOWS-CONSOLE.md et
WINDOWS-CONSOLE-VALIDATION.md pour les tests et limites.

https://github.com/creezio/codex-account-switcher-windows
'@
[IO.File]::WriteAllText((Join-Path $bin 'LIRE-MOI.txt'),$instructions,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $bin 'LICENSE.txt') -Force
foreach($name in @('TOOL-TUNNEL.md','SIMPLE-INSTANCES.md','UI-VALIDATION.md','UI-REBUILD-PLAN.md','RELAY.md','WINDOWS-CONSOLE.md','WINDOWS-CONSOLE-VALIDATION.md')){Copy-Item -LiteralPath (Join-Path $repo ('docs\'+$name)) -Destination $bin -Force}
$zip=Join-Path $repo 'outputs\CodexAccountSwitcher-0.10.0-beta.1-windows-x64.zip'
# All published runtime assemblies are required. Test helpers and debug symbols are not shipped.
$items=Get-ChildItem -LiteralPath $bin | Where-Object {$_.Name -notmatch '(Smoke|Tests|\.pdb$)' } | ForEach-Object FullName
Compress-Archive -LiteralPath $items -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
  foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','coreclr.dll','CreezioRelay.exe','plugins/creezio-relay/.codex-plugin/plugin.json')){
    if(-not($archive.Entries | Where-Object {$_.FullName.Replace('\','/') -eq $required})){throw "Fichier absent du ZIP : $required"}
  }
}finally{$archive.Dispose()}
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $repo 'outputs\SHA256SUMS-0.10.0-beta.1.txt'),$hash+'  '+[IO.Path]::GetFileName($zip)+"`n")
Get-Item -LiteralPath $zip | Select-Object Name,Length
