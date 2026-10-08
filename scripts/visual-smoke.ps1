[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$probe=Join-Path $repo 'work\test-bin\ProductVisualSmoke.exe'
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
$flags=@('/nologo','/target:exe','/main:ProductVisualSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$probe),(Join-Path $repo 'tests\ProductVisualSmoke.cs'))
& $compiler ($flags+$sources)
if($LASTEXITCODE -ne 0){throw 'Visual smoke compilation failed.'}
& $probe (Join-Path $repo 'work\relay-visual')
if($LASTEXITCODE -ne 0){throw 'Visual smoke failed.'}
