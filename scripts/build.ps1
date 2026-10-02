[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'build' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
# The framework compiler ships with Windows; this build downloads no dependencies.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler not found.' }
$free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($destination).Substring(0,1))).Free
if ($free -lt 100MB) { throw 'At least 100 MB of free space is required for this small build.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$arguments = @('/nologo','/target:winexe','/main:Creezio.Switcher.Program','/platform:anycpu','/optimize+','/warn:4','/warnaserror+',('/out:' + (Join-Path $destination 'CodexAccountSwitcher.exe')),('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll') + $sources
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$relayArguments = $arguments | Where-Object { $_ -notlike '/target:*' -and $_ -notlike '/main:*' -and $_ -notlike '/out:*' }
& $compiler (@('/target:exe','/main:Creezio.Switcher.RelayCli',('/out:' + (Join-Path $destination 'CreezioRelay.exe'))) + $relayArguments)
if ($LASTEXITCODE -ne 0) { throw 'Relay compilation failed.' }
$config = '<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>'
[IO.File]::WriteAllText((Join-Path $destination 'CodexAccountSwitcher.exe.config'), $config)
[IO.File]::WriteAllText((Join-Path $destination 'CreezioRelay.exe.config'), $config)
Get-Item -LiteralPath (Join-Path $destination 'CodexAccountSwitcher.exe') | Select-Object Name,Length
