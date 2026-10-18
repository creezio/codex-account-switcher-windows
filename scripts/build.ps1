[CmdletBinding()]
param([string]$OutputDirectory,[switch]$RefreshApplication)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'build' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$buildCursor=$destination
while($buildCursor){if((Test-Path -LiteralPath $buildCursor) -and ((Get-Item -LiteralPath $buildCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Un lien ou une jonction empêche cette compilation.'};$buildCursor=Split-Path -Parent $buildCursor}
foreach($buildName in @('CodexAccountSwitcher.exe','CreezioRelay.exe','SwitcherLauncher.exe','SwitcherLauncher.exe.config','SwitcherCodexBridge.exe','SwitcherCodexBridge.exe.config','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json','CodexAccountSwitcher.pdb','CodexAccountSwitcher.exe.config','CreezioRelay.exe.config','RUNTIME-SHARED.json')){
  $buildFile=Join-Path $destination $buildName
  if(Test-Path -LiteralPath $buildFile){$buildInfo=Get-Item -LiteralPath $buildFile -Force;if(($buildInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $buildInfo.LinkType -eq 'HardLink'){throw "Fichier applicatif partagé ou lié : $buildName. Aucune réécriture autorisée."}}
}
$sharedMarker=Join-Path $destination 'RUNTIME-SHARED.json'
if(Test-Path -LiteralPath $sharedMarker){
  if(-not $RefreshApplication){throw 'Cette livraison partage un runtime immuable. Utiliser la compilation applicative contrôlée ; ne pas réécrire le runtime.'}
  if((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Une jonction ne peut pas recevoir de compilation.'}
  $runtimeMetadata=Get-Content -LiteralPath $sharedMarker -Raw|ConvertFrom-Json
  if(-not $runtimeMetadata.ImmutableRuntimeOnly){throw 'Marqueur de runtime immuable absent.'}
}
$busy=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($destination.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($destination,[StringComparison]::OrdinalIgnoreCase) -ge 0))})
if($busy.Count){throw 'Une application de ce dossier est active ; aucune compilation ne peut la remplacer.'}
# The framework compiler ships with Windows; this build downloads no dependencies.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler not found.' }
$free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($destination).Substring(0,1))).Free
if ($free -lt 100MB) { throw 'At least 100 MB of free space is required for this small build.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$arguments = @('/nologo','/target:winexe','/main:Creezio.Switcher.Program','/platform:anycpu','/optimize+','/warn:4','/warnaserror+',('/out:' + (Join-Path $destination 'CodexAccountSwitcher.exe')),('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll','/reference:System.Net.Http.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll') + $sources
# An existing portable delivery already has its native WPF apphost. Never
# replace that host with the legacy Framework GUI during an app-only refresh.
if(-not $RefreshApplication){
  & $compiler $arguments
  if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
}
$relayArguments = $arguments | Where-Object { $_ -notlike '/target:*' -and $_ -notlike '/main:*' -and $_ -notlike '/out:*' }
& $compiler (@('/target:exe','/main:Creezio.Switcher.RelayCli',('/out:' + (Join-Path $destination 'CreezioRelay.exe'))) + $relayArguments)
if ($LASTEXITCODE -ne 0) { throw 'Relay compilation failed.' }
& $compiler (@('/target:winexe','/main:Creezio.Switcher.SwitcherLauncher',('/out:' + (Join-Path $destination 'SwitcherLauncher.exe'))) + $relayArguments)
if ($LASTEXITCODE -ne 0) { throw 'Stable launcher compilation failed.' }
& $compiler (@('/target:exe','/main:Creezio.Switcher.DesktopGoalProxy',('/out:' + (Join-Path $destination 'SwitcherCodexBridge.exe'))) + $relayArguments)
if ($LASTEXITCODE -ne 0) { throw 'Desktop metadata bridge compilation failed.' }
$config = '<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>'
[IO.File]::WriteAllText((Join-Path $destination 'CodexAccountSwitcher.exe.config'), $config)
[IO.File]::WriteAllText((Join-Path $destination 'CreezioRelay.exe.config'), $config)
[IO.File]::WriteAllText((Join-Path $destination 'SwitcherLauncher.exe.config'), $config)
[IO.File]::WriteAllText((Join-Path $destination 'SwitcherCodexBridge.exe.config'), $config)
$pluginSource = Join-Path $projectRoot 'plugins'
$pluginPending=[Collections.Generic.Stack[string]]::new()
foreach($pluginRoot in @($pluginSource,(Join-Path $destination 'plugins'))){if(Test-Path -LiteralPath $pluginRoot){$pluginPending.Push($pluginRoot)}}
while($pluginPending.Count){$pluginPath=$pluginPending.Pop();$pluginInfo=Get-Item -LiteralPath $pluginPath -Force;if(($pluginInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (-not $pluginInfo.PSIsContainer -and $pluginInfo.LinkType -eq 'HardLink')){throw 'Un fichier de plugin lié ne peut pas être copié ou réécrit.'};if($pluginInfo.PSIsContainer){foreach($pluginChild in Get-ChildItem -LiteralPath $pluginPath -Force){$pluginPending.Push($pluginChild.FullName)}}}
Copy-Item -LiteralPath $pluginSource -Destination $destination -Recurse -Force
Get-Item -LiteralPath (Join-Path $destination 'CodexAccountSwitcher.exe') | Select-Object Name,Length
