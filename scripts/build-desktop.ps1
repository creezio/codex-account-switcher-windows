[CmdletBinding()]
param([string]$OutputDirectory,[switch]$Portable)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localSdk=Join-Path $env:LOCALAPPDATA 'Creezio\tools\dotnet\dotnet.exe'
$dotnet=if(Test-Path -LiteralPath $localSdk){$localSdk}else{'dotnet'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repo 'work\desktop-bin'}
$target=[IO.Path]::GetFullPath($OutputDirectory)
$active=Get-Process -Name CodexAccountSwitcher,CreezioRelay -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetDirectoryName($_.Path) -eq $target }
if($active){throw 'Un exécutable de ce dossier est actif. Choisir un dossier de livraison libre.'}
$free=(Get-PSDrive -Name ([IO.Path]::GetPathRoot($target).Substring(0,1))).Free
if($free -lt 2GB){throw 'Au moins 2 Gio libres requis pour préparer la livraison.'}
# Engine retains its Framework ABI and plugin protocol; the WPF app uses modern .NET.
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $target
if($Portable){& $dotnet publish (Join-Path $repo 'desktop\Switcher.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $target}
else{& $dotnet build (Join-Path $repo 'desktop\Switcher.Desktop.csproj') -c Release -o $target}
if($LASTEXITCODE -ne 0){throw 'Échec de compilation WPF.'}
