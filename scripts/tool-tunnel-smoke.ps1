[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Instance,[string]$SchemaFile,[string]$Phase,[string]$PluginName)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $repo 'work\test-bin\ToolTunnelSmoke.exe'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($exe)) -Force | Out-Null
if(-not $SchemaFile){$SchemaFile=Join-Path $repo 'work\tunnel-tool-schemas.json'}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SchemaFile))) -Force | Out-Null
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler (@('/nologo','/target:exe','/main:ToolTunnelSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$exe),(Join-Path $repo 'tests\ToolTunnelSmoke.cs'))+$sources)
if($LASTEXITCODE -ne 0){throw 'Compilation du test impossible.'}
& $exe $Instance $SchemaFile $Phase $PluginName
if($LASTEXITCODE -ne 0){throw 'Le test du transport a échoué.'}
