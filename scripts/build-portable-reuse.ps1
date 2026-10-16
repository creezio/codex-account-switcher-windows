[CmdletBinding()]
param(
  [string]$OutputDirectory,
  [string]$RuntimeDirectory,
  [string]$RuntimeVersion='10.0.12',
  [switch]$ResumeIncomplete,
  [switch]$RefreshApplication
)
$ErrorActionPreference='Stop'
function Assert-PlainBuildPath([string]$Path) {
  $cursor=[IO.Path]::GetFullPath($Path)
  while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Lien ou jonction refusé : $cursor"};$cursor=Split-Path -Parent $cursor}
}

function Assert-IncompleteApplicationDirectory([string]$Directory) {
  if(Test-Path -LiteralPath (Join-Path $Directory 'RUNTIME-SHARED.json')){throw 'Cette livraison possède déjà un runtime partagé ; aucune reprise autorisée.'}
  $applicationNames=@('CodexAccountSwitcher.exe','CodexAccountSwitcher.exe.config','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json','CodexAccountSwitcher.pdb','CreezioRelay.exe','CreezioRelay.exe.config')
  $pending=[Collections.Generic.Stack[string]]::new()
  $pending.Push($Directory)
  while($pending.Count){
    $current=$pending.Pop()
    $directoryInfo=Get-Item -LiteralPath $current -Force
    if(-not $directoryInfo.PSIsContainer -or ($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'La reprise refuse les liens, jonctions et destinations non répertoires.'}
    foreach($child in Get-ChildItem -LiteralPath $current -Force){
      if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'La reprise refuse tout lien ou jonction descendant.'}
      if($current -eq $Directory){
        if($child.PSIsContainer){if($child.Name -ne 'plugins'){throw "Sous-dossier inattendu dans la compilation partielle : $($child.Name)"}}
        elseif($child.Name -notin $applicationNames){throw "Fichier non applicatif dans la compilation partielle : $($child.Name)"}
      }
      if($child.PSIsContainer){$pending.Push($child.FullName)}
    }
  }
  $busy=@(Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $PID -and
      (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($Directory+'\',[StringComparison]::OrdinalIgnoreCase)) -or
       ($_.CommandLine -and $_.CommandLine.IndexOf($Directory,[StringComparison]::OrdinalIgnoreCase) -ge 0))
  })
  if($busy.Count){throw 'La compilation partielle est référencée par un processus actif ; reprise refusée.'}
}

$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pipelineLock=Join-Path $repo 'work\portable-build.lock'
$pipelineCursor=$pipelineLock
while($pipelineCursor){if(Test-Path -LiteralPath $pipelineCursor){$pipelineInfo=Get-Item -LiteralPath $pipelineCursor -Force;if(($pipelineInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (-not $pipelineInfo.PSIsContainer -and $pipelineInfo.LinkType -eq 'HardLink')){throw 'Chemin lié refusé pour le verrou de compilation.'}};$pipelineCursor=Split-Path -Parent $pipelineCursor}
$pipelineLease=[IO.FileStream]::new($pipelineLock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,1,[IO.FileOptions]::DeleteOnClose)
try {
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repo 'outputs\v0.19.0-beta.1'}
if(-not $RuntimeDirectory){$RuntimeDirectory=Join-Path $repo 'outputs\v0.16.1-beta.1'}
$target=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$source=[IO.Path]::GetFullPath($RuntimeDirectory).TrimEnd('\')
Assert-PlainBuildPath $target
Assert-PlainBuildPath $source
$outputs=[IO.Path]::GetFullPath((Join-Path $repo 'outputs')).TrimEnd('\')+'\'
if(-not $target.StartsWith($outputs,[StringComparison]::OrdinalIgnoreCase) -or $target -eq $source){throw 'La nouvelle livraison doit être un dossier distinct dans outputs.'}
if(Test-Path -LiteralPath $target){
  if($RefreshApplication){
    $metadata=Get-Content -LiteralPath (Join-Path $target 'RUNTIME-SHARED.json') -Raw|ConvertFrom-Json
    if(-not $metadata.ImmutableRuntimeOnly -or $metadata.RuntimeVersion -ne $RuntimeVersion){throw 'Runtime partagé compatible requis pour actualiser les seuls fichiers applicatifs.'}
    $active=@(Get-CimInstance Win32_Process|Where-Object {$_.ProcessId -ne $PID -and (($_.ExecutablePath -and $_.ExecutablePath.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)) -or ($_.CommandLine -and $_.CommandLine.IndexOf($target,[StringComparison]::OrdinalIgnoreCase) -ge 0))})
    if($active.Count){throw 'Cette livraison est utilisée ; aucune actualisation autorisée.'}
    $runtimeNames=@($metadata.Files|ForEach-Object {[string]$_.RelativePath})
    if($runtimeNames.Count -lt 200 -or ($runtimeNames|Sort-Object -Unique).Count -ne $runtimeNames.Count -or $runtimeNames -notcontains 'coreclr.dll' -or $runtimeNames -notcontains 'PresentationFramework.dll'){throw 'Inventaire du runtime incomplet ou dupliqué ; actualisation refusée.'}
    foreach($runtimeEntry in $metadata.Files){
      $runtimeRelative=[string]$runtimeEntry.RelativePath
      if([IO.Path]::IsPathRooted($runtimeRelative) -or $runtimeRelative.Split([char]92) -contains '..' -or $runtimeRelative.Contains('/')){throw 'Chemin de runtime non autorisé.'}
      $runtimeExisting=Join-Path $target $runtimeRelative
      Assert-PlainBuildPath $runtimeExisting
      if((Get-Item -LiteralPath $runtimeExisting -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Lien de runtime symbolique refusé.'}
      if((Get-FileHash -LiteralPath $runtimeExisting -Algorithm SHA256).Hash -ne $runtimeEntry.Sha256){throw 'Runtime modifié avant compilation ; actualisation refusée.'}
    }
  }else{
  if(-not $ResumeIncomplete){throw 'Le dossier de livraison existe déjà. Ne pas republier sur des fichiers de runtime partagés. -ResumeIncomplete exige une compilation applicative incomplète sans runtime ni processus actif.'}
  # A failed app-only publish may leave independent binaries. Reuse these files
  # without deleting the directory or ever accepting a previously linked runtime.
  Assert-IncompleteApplicationDirectory $target
  }
}
if([IO.Path]::GetPathRoot($target) -ne [IO.Path]::GetPathRoot($source)){throw 'Les liens physiques exigent le même volume.'}
if(-not(Test-Path -LiteralPath $source -PathType Container)){throw 'Runtime portable existant introuvable.'}
if((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Le runtime source ne doit pas être une jonction.'}
$drive=Get-PSDrive -Name ([IO.Path]::GetPathRoot($target).Substring(0,1))
if($drive.Free -lt 2GB){throw 'Au moins 2 Gio libres requis pour les petits fichiers applicatifs et le ZIP.'}
$dotnet=Join-Path $env:LOCALAPPDATA 'Creezio\tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnet)){throw 'SDK local existant requis ; aucun telechargement ni installation.'}
$packages=Join-Path $env:USERPROFILE '.nuget\packages'
$oldConfig=Get-Content -LiteralPath (Join-Path $source 'CodexAccountSwitcher.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
  if(-not($oldConfig.runtimeOptions.includedFrameworks | Where-Object {$_.name -eq $framework -and $_.version -eq $RuntimeVersion})){throw "Version du runtime source incompatible : $framework $RuntimeVersion"}
}
# Require the already cached official runtime packs. The publish target exports the
# final resolved inventory: overlapping core/desktop assemblies can legitimately differ.
$packRoots=@()
foreach($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')){
  foreach($subdirectory in @('lib\net10.0','native')){
    $packRoot=Join-Path $packages "$pack\$RuntimeVersion\runtimes\win-x64\$subdirectory"
    if(-not(Test-Path -LiteralPath $packRoot -PathType Container)){throw "Pack absent du cache local : $pack"}
    $packRoots+=[IO.Path]::GetFullPath($packRoot).TrimEnd('\')+'\'
  }
}
$temporaryTargets=Join-Path $repo 'work\publish-application-only.targets'
$inventory=Join-Path $repo 'work\publish-runtime-inventory.txt'
foreach($pipelineFile in @($temporaryTargets,$inventory,(Join-Path $target 'RUNTIME-SHARED.json'))){
  if(Test-Path -LiteralPath $pipelineFile){$pipelineInfo=Get-Item -LiteralPath $pipelineFile -Force;if(($pipelineInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $pipelineInfo.LinkType -eq 'HardLink'){throw 'Un fichier de contrôle lié ne peut pas être réécrit.'}}
}
$targetsText=@'
<Project>
  <!-- No shared runtime destination is ever passed to a Copy task. -->
  <Target Name="SwitcherPublishApplicationOnly" AfterTargets="ComputeFilesToPublish">
    <WriteLinesToFile File="$(SwitcherRuntimeInventory)" Lines="@(ResolvedFileToPublish->'%(FullPath)|%(RelativePath)')" Overwrite="true" Encoding="UTF-8" />
    <ItemGroup>
      <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)"
        Condition="'%(ResolvedFileToPublish.RelativePath)' != 'CodexAccountSwitcher.exe' and '%(ResolvedFileToPublish.RelativePath)' != 'CodexAccountSwitcher.dll' and '%(ResolvedFileToPublish.RelativePath)' != 'CodexAccountSwitcher.deps.json' and '%(ResolvedFileToPublish.RelativePath)' != 'CodexAccountSwitcher.runtimeconfig.json' and '%(ResolvedFileToPublish.RelativePath)' != 'CodexAccountSwitcher.pdb'" />
    </ItemGroup>
    <Error Condition="'@(ResolvedFileToPublish)' == ''" Text="Application-only publish inventory is empty." />
  </Target>
</Project>
'@
[IO.File]::WriteAllText($temporaryTargets,$targetsText,[Text.UTF8Encoding]::new($false))
$project=Join-Path $repo 'desktop\Switcher.Desktop.csproj'
# The source is the local package cache, never a network feed. Missing dependencies fail.
& $dotnet restore $project -r win-x64 --source $packages -p:SelfContained=true ("-p:RuntimeFrameworkVersion="+$RuntimeVersion) -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Restauration hors ligne impossible ; aucune installation effectuée.'}
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $target -RefreshApplication:$RefreshApplication | Out-Host
# Publish app files first into a fresh directory. Runtime hardlinks do not exist yet.
& $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $target ("-p:RuntimeFrameworkVersion="+$RuntimeVersion) -p:CopyLocalLockFileAssemblies=false -p:SkipCopyUnchangedFiles=true ("-p:CustomAfterMicrosoftCommonTargets="+$temporaryTargets) ("-p:SwitcherRuntimeInventory="+$inventory)
if($LASTEXITCODE -ne 0){throw 'Publication applicative échouée ; aucun lien partagé créé.'}
foreach($required in @('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json')){
  if(-not(Test-Path -LiteralPath (Join-Path $target $required) -PathType Leaf)){throw "Publication applicative incomplète : $required"}
}
$newConfig=Get-Content -LiteralPath (Join-Path $target 'CodexAccountSwitcher.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){
  if(-not($newConfig.runtimeOptions.includedFrameworks | Where-Object {$_.name -eq $framework -and $_.version -eq $RuntimeVersion})){throw "Le nouvel executable doit etre autonome avec le runtime prevu : $framework"}
}
# Match exactly the runtime variants selected by this SDK, not similarly named DLLs.
$runtimeFiles=@{}
$applicationFiles=@('CodexAccountSwitcher.exe','CodexAccountSwitcher.dll','CodexAccountSwitcher.deps.json','CodexAccountSwitcher.runtimeconfig.json','CodexAccountSwitcher.pdb')
foreach($line in Get-Content -LiteralPath $inventory){
  $fields=$line.Split('|')
  if($fields.Length -ne 2){throw 'Inventaire de publication invalide.'}
  $file=[IO.Path]::GetFullPath($fields[0]); $relative=$fields[1].Replace('/','\')
  Assert-PlainBuildPath $file
  if($relative -in $applicationFiles){continue}
  if([IO.Path]::IsPathRooted($relative) -or $relative.Split([char]92) -contains '..' -or $relative -match '^(CodexAccountSwitcher|CreezioRelay)(\.|\\)'){throw 'Chemin de runtime non autorise.'}
  if(-not($packRoots | Where-Object {$file.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)})){throw "Fichier hors packs runtime officiels en cache : $relative"}
  if([IO.Path]::GetExtension($file) -notin @('.dll','.exe')){throw "Type de runtime inattendu : $relative"}
  $oldFile=Join-Path $source $relative
  Assert-PlainBuildPath $oldFile
  Assert-PlainBuildPath (Join-Path $target $relative)
  if(-not(Test-Path -LiteralPath $oldFile -PathType Leaf)){throw "Runtime source incomplet : $relative"}
  if(((Get-Item -LiteralPath $oldFile).Attributes -band [IO.FileAttributes]::ReparsePoint) -or ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Un fichier de runtime ne doit pas etre un lien symbolique.'}
  $expected=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
  $actual=(Get-FileHash -LiteralPath $oldFile -Algorithm SHA256).Hash
  if($actual -ne $expected){throw "Runtime source different du pack officiel resolu : $relative"}
  if(Test-Path -LiteralPath (Join-Path $target $relative)){
    if(-not $RefreshApplication -or (Get-FileHash -LiteralPath (Join-Path $target $relative) -Algorithm SHA256).Hash -ne $expected){throw "Runtime de destination inattendu ou modifié : $relative"}
  }
  $runtimeFiles[$relative]=[pscustomobject]@{RelativePath=$relative;Source=$oldFile;Sha256=$actual;Length=(Get-Item -LiteralPath $file).Length}
}
if($runtimeFiles.Count -lt 200 -or -not $runtimeFiles.ContainsKey('coreclr.dll') -or -not $runtimeFiles.ContainsKey('PresentationFramework.dll')){throw 'Inventaire de runtime incomplet.'}
# Marker is written before links: every later normal build must refuse this directory.
$marker=Join-Path $target 'RUNTIME-SHARED.json'
[IO.File]::WriteAllText($marker,(@{RuntimeVersion=$RuntimeVersion;SourceDirectory=$source;ImmutableRuntimeOnly=$true;Files=@($runtimeFiles.Values | Sort-Object RelativePath | Select-Object RelativePath,Sha256,Length)} | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$sourceMarker=Join-Path $source 'RUNTIME-SHARED.json'
if(-not(Test-Path -LiteralPath $sourceMarker)){
  # The source becomes shared too. Protect it before the first link is created.
  [IO.File]::WriteAllText($sourceMarker,(@{RuntimeVersion=$RuntimeVersion;SharedRuntimeSource=$true;ImmutableRuntimeOnly=$true} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
foreach($entry in $runtimeFiles.Values){
  $link=Join-Path $target $entry.RelativePath
  Assert-PlainBuildPath $link
  $parent=Split-Path -Parent $link
  if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent | Out-Null}
  if(-not(Test-Path -LiteralPath $link)){New-Item -ItemType HardLink -Path $link -Target $entry.Source | Out-Null}
  if((Get-FileHash -LiteralPath $link -Algorithm SHA256).Hash -ne $entry.Sha256){throw "Lien de runtime invalide : $($entry.RelativePath)"}
}
Remove-Item -LiteralPath $temporaryTargets,$inventory
[pscustomobject]@{OutputDirectory=$target;RuntimeFilesShared=$runtimeFiles.Count;RuntimeBytesReused=($runtimeFiles.Values | Measure-Object Length -Sum).Sum;RuntimeVersion=$RuntimeVersion}
} finally { $pipelineLease.Dispose() }
