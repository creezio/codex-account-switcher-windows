[CmdletBinding()]
param([string]$Channel)
$ErrorActionPreference = 'Stop'
$connectionPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'connection.json'
if (-not (Test-Path -LiteralPath $connectionPath)) { throw 'Install this plugin from Codex Account Switcher first.' }
$connection = Get-Content -LiteralPath $connectionPath -Raw | ConvertFrom-Json
if (-not $env:CODEX_THREAD_ID -or -not $env:CODEX_APP_TOOLS_PIPE_PATH) { throw 'Run this script as a tool command inside the calling Codex chat.' }
$actualHome = if ($env:CODEX_HOME) { [IO.Path]::GetFullPath($env:CODEX_HOME) } else { Join-Path $env:USERPROFILE '.codex' }
if ([IO.Path]::GetFullPath($connection.Home).TrimEnd('\') -ne $actualHome.TrimEnd('\')) { throw 'This plugin belongs to another Codex profile.' }
$request = @{operation='bind';root=$connection.Root;channel=$Channel}
$request | ConvertTo-Json -Compress | & $connection.Executable
if ($LASTEXITCODE -ne 0) { throw 'Relay connection failed. Check the returned diagnostic.' }
