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
Version 0.2.0 (beta)

1. Extraire le ZIP et ouvrir CodexAccountSwitcher.exe.
2. Importer le compte local ou ajouter un compte dans le navigateur.
3. Les credits de reset s'affichent avec les quotas.
   Le reset automatique est active par defaut : compte local uniquement,
   a 1 % restant ou moins, si un credit compatible est disponible.
   Le controle se fait chaque minute tant que le switcher reste ouvert.
   Desactivez l'option dans la barre laterale ou les parametres si besoin.
4. Pour basculer : quitter completement Codex/ChatGPT et ses sessions CLI,
   puis choisir Utiliser ce compte et rouvrir Codex.

Windows 10/11 et .NET Framework 4.8. Codex CLI (codex.exe) requis.
Le binaire n'est pas signe. Les comptes sont chiffres avec Windows DPAPI.
La premiere version prend en charge uniquement le stockage Codex fichier.
Pas de bascule automatique ni de reprise automatique des conversations.

Documentation, code et limitations :
https://github.com/creezio/codex-account-switcher-windows

Pour quitter le switcher : clic droit sur son icone pres de l'horloge > Quitter.
'@
[IO.File]::WriteAllText((Join-Path $packageRoot 'LIRE-MOI.txt'), $readme)
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE.txt') -Force
$zip = Join-Path $output 'CodexAccountSwitcher-0.2.0-windows.zip'
Compress-Archive -LiteralPath @((Join-Path $packageRoot 'CodexAccountSwitcher.exe'),(Join-Path $packageRoot 'CodexAccountSwitcher.exe.config'),(Join-Path $packageRoot 'LIRE-MOI.txt'),(Join-Path $packageRoot 'LICENSE.txt')) -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS-0.2.0.txt'),($hash + '  ' + [IO.Path]::GetFileName($zip) + "`n"))
Get-Item -LiteralPath $zip | Select-Object Name,Length
