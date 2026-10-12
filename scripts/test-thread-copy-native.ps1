[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CodexExecutable)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $repo 'work\test-bin\ThreadCopyNativeSmoke.exe'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($exe)) -Force | Out-Null
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler (@('/nologo','/target:exe','/main:ThreadCopyNativeSmoke',('/out:'+$exe),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',(Join-Path $repo 'tests\ThreadCopyNativeSmoke.cs'))+$sources)
if($LASTEXITCODE -ne 0){throw 'Native smoke compilation failed'}
foreach($mode in @('legacy','paginated')) {
    & $exe ([IO.Path]::GetFullPath($CodexExecutable)) $mode (Join-Path $repo 'work\thread-transfer-probe')
    if($LASTEXITCODE -ne 0){throw "Native thread copy failed: $mode"}
}
