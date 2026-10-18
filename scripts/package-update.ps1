[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$BinaryDirectory,
  [string]$ArchiveDirectory,
  [switch]$ManifestOnly,
  [switch]$Replace
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'update-package-contract.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=[IO.Path]::GetFullPath($BinaryDirectory).TrimEnd('\')
$archives=if($ArchiveDirectory){[IO.Path]::GetFullPath($ArchiveDirectory).TrimEnd('\')}else{Join-Path $repo 'outputs'}
if($bin.Length -lt 5 -or $bin.StartsWith('\\',[StringComparison]::Ordinal)){throw 'Le payload doit être un dossier local explicite.'}
function Assert-UpdatePlainPath([string]$Path) {
  $cursor=[IO.Path]::GetFullPath($Path)
  while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Lien ou jonction refusé : $cursor"};$cursor=[IO.Path]::GetDirectoryName($cursor)}
}
Assert-UpdatePlainPath $bin
Assert-UpdatePlainPath $archives
if(-not(Test-Path -LiteralPath $bin -PathType Container)){throw 'Dossier de livraison absent.'}
if($archives -eq $bin -or $archives.StartsWith($bin+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Les archives doivent rester hors du payload.'}
$packageDrive=Get-PSDrive -Name ([IO.Path]::GetPathRoot($archives).Substring(0,1))
if($packageDrive.Free -lt 20GB){throw 'Au moins 20 Gio libres requis avant de préparer la mise à jour.'}
$required=@('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json','CreezioRelay.exe','CreezioRelay.exe.config','SwitcherLauncher.exe','SwitcherLauncher.exe.config','coreclr.dll','PresentationFramework.dll','hostfxr.dll','hostpolicy.dll','plugins/creezio-relay/.codex-plugin/plugin.json','plugins/creezio-relay/.mcp.json','plugins/creezio-relay/ui/viewer.html')
foreach($relative in $required){if(-not(Test-Path -LiteralPath (Join-Path $bin $relative) -PathType Leaf)){throw "Payload incomplet : $relative"}}
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin 'CreezioRelay.exe')).ProductVersion
$guiVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin 'CodexAccountSwitcher.dll')).ProductVersion
if($version -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z' -or $version -ne $guiVersion){throw 'Les versions réelles du relais et de la GUI doivent être identiques.'}
$withoutBuildMetadata=($version -split '\+',2)[0];$prerelease=($withoutBuildMetadata -split '-',2)
if($prerelease.Count -gt 1){foreach($identifier in $prerelease[1].Split('.')){if($identifier -match '\A0[0-9]+\z'){throw 'Version prerelease non canonique.'}}}
if(Test-SwitcherCodexBridgeRequired $version){
  $required+=@('SwitcherCodexBridge.exe','SwitcherCodexBridge.exe.config')
  foreach($relative in @('SwitcherCodexBridge.exe','SwitcherCodexBridge.exe.config')){if(-not(Test-Path -LiteralPath (Join-Path $bin $relative) -PathType Leaf)){throw "Payload incomplet : $relative"}}
  if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bin 'SwitcherCodexBridge.exe')).ProductVersion -cne $version){throw 'La version réelle du pont Codex doit correspondre à la livraison.'}
}
$zip=Join-Path $archives ('CodexAccountSwitcher-'+$version+'-windows-x64.zip')
$sums=Join-Path $archives ('SHA256SUMS-'+$version+'.txt')
$manifestPath=Join-Path $bin 'switcher-update.json'
foreach($control in @($manifestPath,$zip,$sums)){
  Assert-UpdatePlainPath $control
  if(Test-Path -LiteralPath $control){$controlInfo=Get-Item -LiteralPath $control -Force;if($controlInfo.PSIsContainer -or $controlInfo.LinkType -eq 'HardLink'){throw "Fichier de contrôle partagé ou invalide : $control"}}
}
if(-not $ManifestOnly -and -not $Replace -and ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $sums))){throw 'Cette version possède déjà une archive. -Replace est réservé à une livraison non publiée.'}
$pending=[Collections.Generic.Stack[string]]::new();$pending.Push($bin)
$paths=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
while($pending.Count){
  $directory=$pending.Pop()
  foreach($item in Get-ChildItem -LiteralPath $directory -Force){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Lien ou jonction dans le payload : $($item.FullName)"}
    if($item.PSIsContainer){$pending.Push($item.FullName);continue}
    $relative=$item.FullName.Substring($bin.Length+1).Replace('\','/')
    if($relative -eq 'switcher-update.json'){continue}
    if($relative -match '[\x00-\x1f:]' -or $relative.Split('/') -contains '..' -or $paths.ContainsKey($relative)){throw 'Chemin de payload invalide ou dupliqué.'}
    $paths.Add($relative,$item.FullName)
  }
}
if($paths.Count -gt 5000){throw 'Payload trop volumineux.'}
$ordered=[string[]]@($paths.Keys);[Array]::Sort($ordered,[StringComparer]::Ordinal)
$files=@();$total=[long]0
foreach($relative in $ordered){
  Assert-UpdatePlainPath $paths[$relative]
  $file=Get-Item -LiteralPath $paths[$relative] -Force;$total+=$file.Length
  if($file.Length -gt 256MB -or $total -gt 1GB){throw 'Taille du payload hors limites.'}
  $files+=[ordered]@{path=$relative;length=[long]$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$manifest=[ordered]@{schemaVersion=1;version=$version;platform='windows-x64';healthProtocol=1;entrypoint='CodexAccountSwitcher.exe';files=$files}
$manifestText=$manifest | ConvertTo-Json -Depth 6
if([Text.Encoding]::UTF8.GetByteCount($manifestText) -gt 2MB -or $total+[Text.Encoding]::UTF8.GetByteCount($manifestText) -gt 1GB){throw 'Manifeste ou payload trop volumineux.'}
$manifestTemporary=$manifestPath+'.package-tmp';$zipTemporary=$zip+'.package-tmp'
$temporaryPaths=@($manifestTemporary);if(-not $ManifestOnly){$temporaryPaths+=$zipTemporary}
foreach($temporary in $temporaryPaths){Assert-UpdatePlainPath $temporary;if(Test-Path -LiteralPath $temporary){throw "Fichier temporaire existant à examiner : $temporary"}}
if(-not $ManifestOnly){New-Item -ItemType Directory -Path $archives -Force | Out-Null}
$ownsManifestTemporary=$false;$ownsZipTemporary=$false
try{
  $manifestBytes=[Text.UTF8Encoding]::new($false).GetBytes($manifestText)
  $manifestStream=[IO.File]::Open($manifestTemporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$ownsManifestTemporary=$true
  try{$manifestStream.Write($manifestBytes,0,$manifestBytes.Length)}finally{$manifestStream.Dispose()}
  Move-Item -LiteralPath $manifestTemporary -Destination $manifestPath -Force;$ownsManifestTemporary=$false
  if($ManifestOnly){[pscustomobject]@{Version=$version;Manifest=$manifestPath;Files=$files.Count;UncompressedBytes=$total;ManifestOnly=$true};return}
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zipStream=[IO.File]::Open($zipTemporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$ownsZipTemporary=$true
  try{
    $archive=[IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try{
      foreach($file in $files){
        Assert-UpdatePlainPath $paths[$file.path]
        $entry=$archive.CreateEntry($file.path,[IO.Compression.CompressionLevel]::Optimal)
        $inputStream=[IO.File]::Open($paths[$file.path],[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try{
          $outputStream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();$buffer=New-Object byte[] 65536;$copied=[long]0
          try{while(($count=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0){$outputStream.Write($buffer,0,$count);[void]$sha.TransformBlock($buffer,0,$count,$null,0);$copied+=$count};[void]$sha.TransformFinalBlock($buffer,0,0);$observed=[BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant();if($copied -ne $file.length -or $observed -ne $file.sha256){throw "Payload modifié pendant le packaging : $($file.path)"}}finally{$sha.Dispose();$outputStream.Dispose()}
        }finally{$inputStream.Dispose()}
      }
      $entry=$archive.CreateEntry('switcher-update.json',[IO.Compression.CompressionLevel]::Optimal);$outputStream=$entry.Open();try{$outputStream.Write($manifestBytes,0,$manifestBytes.Length)}finally{$outputStream.Dispose()}
    }finally{$archive.Dispose()}
  }finally{$zipStream.Dispose()}
  if((Get-Item -LiteralPath $zipTemporary).Length -gt 512MB){throw 'Archive trop volumineuse pour la mise à jour intégrée.'}
  $archive=[IO.Compression.ZipFile]::OpenRead($zipTemporary)
  try{if($archive.Entries.Count -ne $files.Count+1){throw 'Inventaire ZIP incohérent.'};foreach($requiredPath in $required+@('switcher-update.json')){if(-not($archive.Entries | Where-Object {$_.FullName -ceq $requiredPath})){throw "Fichier absent du ZIP : $requiredPath"}}}finally{$archive.Dispose()}
  $hash=(Get-FileHash -LiteralPath $zipTemporary -Algorithm SHA256).Hash.ToLowerInvariant()
  Move-Item -LiteralPath $zipTemporary -Destination $zip -Force;$ownsZipTemporary=$false
  [IO.File]::WriteAllText($sums,$hash+'  '+[IO.Path]::GetFileName($zip)+"`n",[Text.UTF8Encoding]::new($false))
  [pscustomobject]@{Version=$version;Archive=$zip;Length=(Get-Item -LiteralPath $zip).Length;Sha256=$hash;GitHubDigest='sha256:'+$hash;Checksums=$sums;Manifest=$manifestPath;Files=$files.Count;UncompressedBytes=$total}
}finally{
  if($ownsManifestTemporary -and (Test-Path -LiteralPath $manifestTemporary)){Remove-Item -LiteralPath $manifestTemporary}
  if($ownsZipTemporary -and (Test-Path -LiteralPath $zipTemporary)){Remove-Item -LiteralPath $zipTemporary}
}
