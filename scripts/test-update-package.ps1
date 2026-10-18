[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BinaryDirectory,[string]$ArchiveDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'update-package-contract.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=[IO.Path]::GetFullPath($BinaryDirectory)
$archives=if($ArchiveDirectory){[IO.Path]::GetFullPath($ArchiveDirectory)}else{Join-Path $repo 'outputs'}
$manifest=Get-Content -LiteralPath (Join-Path $bin 'switcher-update.json') -Raw | ConvertFrom-Json
if($manifest.schemaVersion -ne 1 -or $manifest.platform -cne 'windows-x64' -or $manifest.healthProtocol -ne 1 -or $manifest.entrypoint -cne 'CodexAccountSwitcher.exe'){throw 'Contrat du manifeste incompatible.'}
$bridgeRequired=Test-SwitcherCodexBridgeRequired $manifest.version
$versionedBinaries=@('CodexAccountSwitcher.dll','CreezioRelay.exe','SwitcherLauncher.exe')
if($bridgeRequired){$versionedBinaries+='SwitcherCodexBridge.exe';if(-not(Test-Path -LiteralPath (Join-Path $bin 'SwitcherCodexBridge.exe.config') -PathType Leaf)){throw 'Configuration du pont Codex absente.'}}
foreach($binary in $versionedBinaries){if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin $binary)).ProductVersion -cne $manifest.version){throw "Version de binaire incorrecte : $binary"}}
$archiveName='CodexAccountSwitcher-'+$manifest.version+'-windows-x64.zip'
$zip=Join-Path $archives $archiveName
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum=[IO.File]::ReadAllText((Join-Path $archives ('SHA256SUMS-'+$manifest.version+'.txt'))).Trim()
if($checksum -cne ($hash+'  '+$archiveName)){throw 'SHA256SUMS ne désigne pas exactement cette archive.'}
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($file in $manifest.files){
  $path=[string]$file.path
  if(-not $path -or $path -match '[\\:\x00-\x1f]' -or $path.StartsWith('/') -or @($path.Split('/') | Where-Object {$_ -in @('','.','..')}).Count -gt 0 -or $path -eq 'switcher-update.json' -or $expected.ContainsKey($path) -or $file.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $file.length -lt 0){throw 'Inventaire du manifeste invalide.'}
  $expected.Add($path,$file)
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
  if($archive.Entries.Count -ne $expected.Count+1){throw 'Le ZIP possède des fichiers absents ou non déclarés.'}
  $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($entry in $archive.Entries){
    if(-not $seen.Add($entry.FullName) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000){throw 'Entrée ZIP dupliquée ou liée.'}
    $inputStream=$entry.Open()
    try{
      if($entry.FullName -ceq 'switcher-update.json'){
        $reader=[IO.StreamReader]::new($inputStream,[Text.UTF8Encoding]::new($false,$true),$true,1024,$true)
        try{$archivedManifest=$reader.ReadToEnd()}finally{$reader.Dispose()}
        if($archivedManifest -cne [IO.File]::ReadAllText((Join-Path $bin 'switcher-update.json'))){throw 'Le ZIP contient un autre manifeste.'}
      }else{
        if(-not $expected.ContainsKey($entry.FullName)){throw "Entrée ZIP non déclarée : $($entry.FullName)"}
        $file=$expected[$entry.FullName];$sha=[Security.Cryptography.SHA256]::Create()
        try{$observed=[BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
        if($entry.Length -ne $file.length -or $observed -cne $file.sha256){throw "Contenu ZIP différent du manifeste : $($entry.FullName)"}
      }
    }finally{$inputStream.Dispose()}
  }
  foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json','CreezioRelay.exe','CreezioRelay.exe.config','SwitcherLauncher.exe','SwitcherLauncher.exe.config','coreclr.dll','PresentationFramework.dll','hostfxr.dll','hostpolicy.dll','plugins/creezio-relay/.codex-plugin/plugin.json','plugins/creezio-relay/.mcp.json','plugins/creezio-relay/ui/viewer.html')){if(-not $seen.Contains($required)){throw "Fichier requis absent : $required"}}
  if($bridgeRequired){foreach($required in @('SwitcherCodexBridge.exe','SwitcherCodexBridge.exe.config')){if(-not $seen.Contains($required)){throw "Fichier requis absent : $required"}}}
}finally{$archive.Dispose()}
[pscustomobject]@{Version=$manifest.version;FilesVerified=$expected.Count;Archive=$zip;GitHubDigest='sha256:'+$hash;ReadOnly=$true}
