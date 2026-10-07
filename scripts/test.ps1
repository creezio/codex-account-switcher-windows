[CmdletBinding()]
param([switch]$ProtocolSmoke, [switch]$LiveReadOnly, [switch]$DesktopSmoke, [string]$DesktopAuthFile, [string]$TestDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testOutput = Join-Path $projectRoot 'work\test-bin'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$references = @('/nologo','/target:exe','/platform:anycpu','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll')
$testExe = Join-Path $testOutput 'Tests.exe'
& $compiler ($references + @('/main:TestRunner',('/out:' + $testExe),(Join-Path $projectRoot 'tests\TestRunner.cs'),(Join-Path $projectRoot 'tests\ResetTests.cs'),(Join-Path $projectRoot 'tests\InstanceTests.cs'),(Join-Path $projectRoot 'tests\SimpleWorkflowTests.cs'),(Join-Path $projectRoot 'tests\ToolTunnelTests.cs'),(Join-Path $projectRoot 'tests\SharedPagesTests.cs'),(Join-Path $projectRoot 'tests\ResourceLinksTests.cs'),(Join-Path $projectRoot 'tests\ResourceCatalogTests.cs'),(Join-Path $projectRoot 'tests\RelayTests.cs'),(Join-Path $projectRoot 'tests\GeneralRelayTests.cs'),(Join-Path $projectRoot 'tests\ProductTests.cs'),(Join-Path $projectRoot 'tests\ConsoleTests.cs'),(Join-Path $projectRoot 'tests\RemoteTests.cs'),(Join-Path $projectRoot 'tests\AssistanceTests.cs')) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
if (-not $TestDirectory) { $TestDirectory = Join-Path $projectRoot 'work\test-sandbox' }
& $testExe $TestDirectory
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
if ($DesktopSmoke) {
    $desktopExe = Join-Path $testOutput 'InstanceSmoke.exe'
    & $compiler ($references + @('/main:InstanceSmoke',('/out:' + $desktopExe),(Join-Path $projectRoot 'tests\InstanceSmoke.cs')) + $sources)
    if ($LASTEXITCODE -ne 0) { throw 'Desktop smoke compilation failed.' }
    $desktopArgs = @((Join-Path $projectRoot 'work\instance-smoke'))
    if ($DesktopAuthFile) { $desktopArgs += (Resolve-Path -LiteralPath $DesktopAuthFile).Path }
    & $desktopExe $desktopArgs
    if ($LASTEXITCODE -ne 0) { throw 'Desktop smoke test failed.' }
}
