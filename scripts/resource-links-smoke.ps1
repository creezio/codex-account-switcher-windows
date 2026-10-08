[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Instance,[ValidateSet('page','site')][string]$Kind='page',[switch]$Open)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $repo 'work\test-bin\ResourceLinksSmoke.exe'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($exe)) -Force | Out-Null
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler (@('/nologo','/target:exe','/main:ResourceLinksSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$exe),(Join-Path $repo 'tests\ResourceLinksSmoke.cs'))+$sources)
if($LASTEXITCODE -ne 0){throw 'Smoke compilation failed.'}
& $exe $Instance $Kind $(if($Open){'open'}else{'link'})
if($LASTEXITCODE -ne 0){throw 'The live test requires an existing authorized resource and open instances. No access was added.'}
