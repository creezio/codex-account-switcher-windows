[CmdletBinding()]
param([string]$BinaryDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'outputs\v0.7.0'}
$exe=Join-Path $bin 'CodexAccountSwitcher.exe'
$fixture=Join-Path $repo 'work\desktop-validation'
$p=Start-Process -FilePath $exe -ArgumentList @('--ui-test',('"'+$fixture+'"')) -WindowStyle Hidden -PassThru -Wait
$report=Get-Content -LiteralPath (Join-Path $fixture 'results.txt')
$report
if($p.ExitCode -ne 0 -or ($report -match '^FAIL') -or $report[-1] -notmatch '^\d+ tests UI réussis$'){throw 'UI tests failed.'}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testBin=Join-Path $repo 'work\test-bin'
New-Item -ItemType Directory -Path $testBin -Force | Out-Null
$probe=Join-Path $testBin 'CompatibilitySmoke.exe'
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
$flags=@('/nologo','/target:exe','/main:CompatibilitySmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$probe),(Join-Path $repo 'tests\CompatibilitySmoke.cs'))
& $compiler ($flags+$sources)
if($LASTEXITCODE -ne 0){throw 'Compatibility test compilation failed.'}
& $probe (Join-Path $fixture 'data')
if($LASTEXITCODE -ne 0){throw 'Framework could not read the modern app data.'}
$p=Start-Process -FilePath $exe -ArgumentList @('--compat-test',('"'+(Join-Path $fixture 'data')+'"')) -WindowStyle Hidden -PassThru -Wait
if($p.ExitCode -ne 0){throw 'Modern app could not read Framework data.'}
Get-Content -LiteralPath (Join-Path $fixture 'data\compatibility-ok.txt')
