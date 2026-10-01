[CmdletBinding()]
param([switch]$ProtocolSmoke, [switch]$LiveReadOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testOutput = Join-Path $projectRoot 'work\test-bin'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$references = @('/nologo','/target:exe','/platform:anycpu','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll')
$testExe = Join-Path $testOutput 'Tests.exe'
& $compiler ($references + @('/main:TestRunner',('/out:' + $testExe),(Join-Path $projectRoot 'tests\TestRunner.cs'),(Join-Path $projectRoot 'tests\ResetTests.cs')) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe (Join-Path $projectRoot 'work\test-sandbox')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
if ($ProtocolSmoke -or $LiveReadOnly) {
    $smokeExe = Join-Path $testOutput 'ProtocolSmoke.exe'
    & $compiler ($references + @('/main:ProtocolSmoke',('/out:' + $smokeExe),(Join-Path $projectRoot 'tests\ProtocolSmoke.cs')) + $sources)
    if ($LASTEXITCODE -ne 0) { throw 'Protocol test compilation failed.' }
    $smokeArgs = @((Join-Path $projectRoot 'work\protocol-smoke'))
    if ($LiveReadOnly) { $smokeArgs += '--live-read-only' }
    & $smokeExe $smokeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Protocol smoke test failed.' }
}
