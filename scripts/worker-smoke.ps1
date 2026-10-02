[CmdletBinding()]
param([string]$BinaryDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'build'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
$probe=Join-Path $bin 'WorkerSmoke.exe'
$flags=@('/nologo','/target:exe','/main:WorkerSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$probe),(Join-Path $repo 'tests\WorkerSmoke.cs'))
& $compiler ($flags+$sources)
if($LASTEXITCODE -ne 0){throw 'Worker smoke compilation failed.'}
& $probe (Join-Path $repo 'work\worker-smoke')
if($LASTEXITCODE -ne 0){throw 'Worker smoke failed.'}
