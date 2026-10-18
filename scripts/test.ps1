[CmdletBinding()]
param([switch]$ProtocolSmoke, [switch]$LiveReadOnly, [switch]$DesktopSmoke, [string]$DesktopAuthFile, [string]$TestDirectory,[switch]$HeadlessOnly,[switch]$StorageOnly,[switch]$ChatWatchOnly,[switch]$ToolContractOnly,[switch]$WebhookOnly,[switch]$McpBootstrapOnly,[switch]$EnrollmentOnly,[switch]$PluginsOnly,[switch]$UpdatesOnly,[switch]$LifecycleOnly,[switch]$InstallationOnly,[switch]$SkillsOnly,[switch]$ManagementOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testOutput = Join-Path $projectRoot 'work\test-bin'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$references = @('/nologo','/target:exe','/platform:anycpu','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll','/reference:System.Net.Http.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
$testExe = Join-Path $testOutput 'Tests.exe'
$sources += Join-Path $projectRoot 'tests\ProcessResourceTests.cs'
$sources += Join-Path $projectRoot 'tests\StorageTests.cs'
$sources += Join-Path $projectRoot 'tests\StorageBudgetTests.cs'
$sources += Join-Path $projectRoot 'tests\OwnedTemporaryTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudBackendTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudMutationJournalTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudEnvironmentUpdateTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudJobTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudCapacityTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudMissionQueueTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudQueueApiTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudQueueRepositoryTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudTasksBackendTests.cs'
$sources += Join-Path $projectRoot 'tests\RelayReconnectTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudEnvironmentSetupTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudGitHubTests.cs'
$sources += Join-Path $projectRoot 'tests\AtomicReplaceTests.cs'
$sources += Join-Path $projectRoot 'tests\HeadlessToolTests.cs'
$sources += Join-Path $projectRoot 'tests\HeadlessRuntimeTests.cs'
$sources += Join-Path $projectRoot 'tests\HeadlessTransportTests.cs'
$sources += Join-Path $projectRoot 'tests\HeadlessApprovalTests.cs'
$sources += Join-Path $projectRoot 'tests\HeadlessSourceTests.cs'
$sources += Join-Path $projectRoot 'tests\SitesArchiveTests.cs'
$sources += Join-Path $projectRoot 'tests\ToolContractCompatibilityTests.cs'
$sources += Join-Path $projectRoot 'tests\NativeFileUploadTests.cs'
$sources += Join-Path $projectRoot 'tests\ChatWatchTests.cs'
$sources += Join-Path $projectRoot 'tests\CodexCompatibilityTests.cs'
$sources += Join-Path $projectRoot 'tests\WebhookTests.cs'
$sources += Join-Path $projectRoot 'tests\WebhookHttpAuthTests.cs'
$sources += Join-Path $projectRoot 'tests\WebhookDispatchTests.cs'
$sources += Join-Path $projectRoot 'tests\WebhookChatTests.cs'
$sources += Join-Path $projectRoot 'tests\WebhookWebSocketTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalMcpTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalHttpTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalComposerOptionsTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalComposerTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalPermissionTests.cs'
$sources += Join-Path $projectRoot 'tests\VaultConcurrencyTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalAdministrationTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalEnrollmentTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalPluginTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalSkillTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalConversationManagementTests.cs'
$sources += Join-Path $projectRoot 'tests\DesktopGoalProxyTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalManagementHttpTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalNativeHeadlessTests.cs'
$sources += Join-Path $projectRoot 'tests\RelayMcpBootstrapTests.cs'
$sources += Join-Path $projectRoot 'tests\SwitcherUpdateTests.cs'
$sources += Join-Path $projectRoot 'tests\SwitcherLifecycleTests.cs'
$sources += Join-Path $projectRoot 'tests\SwitcherInstallationTests.cs'
$sources += Join-Path $projectRoot 'tests\PortalAttachmentTests.cs'
$sources += Join-Path $projectRoot 'tests\CloudflareTunnelTests.cs'
& $compiler ($references + @('/main:TestRunner',('/out:' + $testExe),(Join-Path $projectRoot 'tests\TestRunner.cs'),(Join-Path $projectRoot 'tests\ThreadCopyTests.cs'),(Join-Path $projectRoot 'tests\TransferTests.cs'),(Join-Path $projectRoot 'tests\ResetTests.cs'),(Join-Path $projectRoot 'tests\LoginBrowserTests.cs'),(Join-Path $projectRoot 'tests\InstanceTests.cs'),(Join-Path $projectRoot 'tests\SimpleWorkflowTests.cs'),(Join-Path $projectRoot 'tests\ToolTunnelTests.cs'),(Join-Path $projectRoot 'tests\SharedPagesTests.cs'),(Join-Path $projectRoot 'tests\ResourceLinksTests.cs'),(Join-Path $projectRoot 'tests\ResourceCatalogTests.cs'),(Join-Path $projectRoot 'tests\RelayTests.cs'),(Join-Path $projectRoot 'tests\GeneralRelayTests.cs'),(Join-Path $projectRoot 'tests\ProductTests.cs'),(Join-Path $projectRoot 'tests\ConsoleTests.cs'),(Join-Path $projectRoot 'tests\RemoteTests.cs'),(Join-Path $projectRoot 'tests\AssistanceTests.cs')) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
if (-not $TestDirectory) { $TestDirectory = Join-Path $projectRoot 'work\test-sandbox' }
$testArgs=@($TestDirectory);if($HeadlessOnly){$testArgs+='--headless'}
if($StorageOnly){$testArgs+='--storage'}
if($ChatWatchOnly){$testArgs+='--chat-watch'}
if($ToolContractOnly){$testArgs+='--tool-contract'}
if($WebhookOnly){$testArgs+='--webhook'}
if($McpBootstrapOnly){$testArgs+='--mcp-bootstrap'}
if($EnrollmentOnly){$testArgs+='--enrollment'}
if($PluginsOnly){$testArgs+='--plugins'}
if($SkillsOnly){$testArgs+='--skills'}
if($ManagementOnly){$testArgs+='--management'}
if($UpdatesOnly){$testArgs+='--updates'}
if($LifecycleOnly){$testArgs+='--lifecycle'}
if($InstallationOnly){$testArgs+='--installation'}
& $testExe @testArgs
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
