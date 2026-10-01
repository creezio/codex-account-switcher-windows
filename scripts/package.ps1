[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$output = Join-Path $projectRoot 'outputs'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$packageRoot = Join-Path $projectRoot 'build'
$readme = @'
CODEX ACCOUNT SWITCHER POUR WINDOWS - CREEZIO
Version 0.3.0 (beta multi-instance)

1. Extraire le ZIP et ouvrir CodexAccountSwitcher.exe.
2. Dans Comptes, importer ou ajouter les comptes souhaites.
3. Dans Instances, creer et nommer vos espaces Codex.
4. Dans Comptes > Associer aux instances, choisir toutes les instances
   (y compris les prochaines) ou seulement certaines.
5. Dans chaque instance fermee, choisir puis configurer un compte et ouvrir.
   Fermer ne cible que cette instance et interrompt ses taches apres confirmation.
   La session habituelle n'est jamais fermee par le switcher.
6. Les limites restent partagees lorsqu'un compte sert a plusieurs instances.
   Le reset automatique a 1 % utilise une reinitialisation disponible,
   avec une seule demande par compte utilise. Aucun achat de credits.

Windows 10/11 et .NET Framework 4.8. Codex installe via Microsoft Store requis
pour ouvrir des instances. Codex CLI (codex.exe) requis pour les comptes/quotas.
Le binaire n'est pas signe. Les comptes sont chiffres avec Windows DPAPI.
Seul le stockage Codex fichier est pris en charge.
Pas de bascule automatique ni de reprise automatique des conversations.

Documentation, code et limitations :
https://github.com/creezio/codex-account-switcher-windows

Pour quitter le switcher : clic droit sur son icone pres de l'horloge > Quitter.
Les instances ouvertes restent actives apres avoir quitte le switcher.
Archiver conserve les profils et conversations. Ne pas partager leurs dossiers.
Le multi-instance utilise un lanceur MSIX de diagnostic : compatibilite beta.
Fermer le switcher et ses instances gerees avant de remplacer son executable.
'@
[IO.File]::WriteAllText((Join-Path $packageRoot 'LIRE-MOI.txt'), $readme)
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE.txt') -Force
$zip = Join-Path $output 'CodexAccountSwitcher-0.3.0-windows.zip'
Compress-Archive -LiteralPath @((Join-Path $packageRoot 'CodexAccountSwitcher.exe'),(Join-Path $packageRoot 'CodexAccountSwitcher.exe.config'),(Join-Path $packageRoot 'LIRE-MOI.txt'),(Join-Path $packageRoot 'LICENSE.txt')) -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS-0.3.0.txt'),($hash + '  ' + [IO.Path]::GetFileName($zip) + "`n"))
Get-Item -LiteralPath $zip | Select-Object Name,Length
