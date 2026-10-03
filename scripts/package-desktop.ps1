[CmdletBinding()]
param([switch]$SkipBuild,[string]$BinaryDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'outputs\v0.7.0'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build-desktop.ps1') -OutputDirectory $bin -Portable}
foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.runtimeconfig.json','coreclr.dll','PresentationFramework.dll','CreezioRelay.exe','plugins')){if(-not(Test-Path -LiteralPath (Join-Path $bin $required))){throw "Livraison autonome incomplète : $required"}}
$instructions=@'
CODEX ACCOUNT SWITCHER — 0.7.0-beta.1 — WINDOWS X64

Extraire TOUT le ZIP et lancer CodexAccountSwitcher.exe.
Garder les DLL, fichiers JSON, exécutables et dossier plugins ensemble.
Le runtime .NET 10 de cette interface WPF est inclus. Aucun SDK à installer.
Le moteur de relais utilise .NET Framework 4.8, présent sur Windows 10/11.

Comptes : connecter ou importer, consulter les limites, associer les instances.
Instances : créer un espace, choisir son compte et ouvrir Codex.
Agents : connecter les chats et installer leur intégration locale.
Projets : choisir les participants, les ressources et les règles de délégation.
Tâches : envoyer, suivre, traiter une attente et poursuivre un échange.
Paramètres : thème, supervision, capacités, diagnostic et outils avancés.

La délégation est explicite par défaut. Aucun compte ni service n'est imposé.
Les réinitialisations concernent les limites d'utilisation, pas les achats de
crédits. L'automatisme s'active par compte et ne contourne aucune permission.

MISE À JOUR
Quitter l'ancien switcher via son icône près de l'horloge > Quitter.
Conserver les fenêtres Codex ouvertes. Lancer cette version depuis son dossier.
Les comptes, instances, configuration et tâches existants sont relus sur place.
Ne pas écraser ni supprimer un dossier utilisé par un hôte d'instance ou un MCP.
Le moteur et le plugin restent compatibles avec le protocole 0.6 ; un chat déjà
connecté peut conserver son intégration. Pour déplacer son exécutable MCP,
utiliser Agents > Installer l'intégration puis ouvrir un nouveau chat.

Fermer la fenêtre réduit le switcher dans la zone de notification.
Quitter le switcher ne ferme pas les instances Codex.
Codex Microsoft Store est nécessaire aux instances, Codex CLI aux comptes.
Le binaire n'est pas signé. Lire UI-VALIDATION.md pour les tests et limites.

https://github.com/creezio/codex-account-switcher-windows
'@
[IO.File]::WriteAllText((Join-Path $bin 'LIRE-MOI.txt'),$instructions,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $bin 'LICENSE.txt') -Force
foreach($name in @('UI-VALIDATION.md','UI-REBUILD-PLAN.md','RELAY.md')){Copy-Item -LiteralPath (Join-Path $repo ('docs\'+$name)) -Destination $bin -Force}
$zip=Join-Path $repo 'outputs\CodexAccountSwitcher-0.7.0-beta.1-windows-x64.zip'
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
[IO.File]::WriteAllText((Join-Path $repo 'outputs\SHA256SUMS-0.7.0-beta.1.txt'),$hash+'  '+[IO.Path]::GetFileName($zip)+"`n")
Get-Item -LiteralPath $zip | Select-Object Name,Length
