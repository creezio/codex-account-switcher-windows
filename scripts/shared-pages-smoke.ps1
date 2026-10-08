[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Instance,[string]$Phase='read')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe=Join-Path $repo 'work\test-bin\SharedPagesSmoke.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler (@('/nologo','/target:exe','/main:SharedPagesSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$exe),(Join-Path $repo 'tests\SharedPagesSmoke.cs'))+$sources)
if($LASTEXITCODE -ne 0){throw 'Compilation du test impossible.'}
& $exe $Instance $Phase
if($LASTEXITCODE -ne 0){throw 'Échec de la recette Pages partagées.'}
