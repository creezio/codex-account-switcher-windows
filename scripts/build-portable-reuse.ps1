[CmdletBinding()]
param(
  [string]$OutputDirectory,
  [string]$RuntimeDirectory,
  [string]$RuntimeVersion='10.0.12'
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repo 'outputs\v0.17.0-beta.2'}
if(-not $RuntimeDirectory){$RuntimeDirectory=Join-Path $repo 'outputs\v0.16.1-beta.1'}
$target=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$source=[IO.Path]::GetFullPath($RuntimeDirectory).TrimEnd('\')
$outputs=[IO.Path]::GetFullPath((Join-Path $repo 'outputs')).TrimEnd('\')+'\'
if(-not $target.StartsWith($outputs,[StringComparison]::OrdinalIgnoreCase) -or $target -eq $source){throw 'La nouvelle livraison doit être un dossier distinct dans outputs.'}
if(Test-Path -LiteralPath $target){throw 'Le dossier de livraison existe déjà. Ne pas republier sur des fichiers de runtime partagés.'}
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
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $target | Out-Host
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
  if($relative -in $applicationFiles){continue}
  if([IO.Path]::IsPathRooted($relative) -or $relative.Split([char]92) -contains '..' -or $relative -match '^(CodexAccountSwitcher|CreezioRelay)(\.|\\)'){throw 'Chemin de runtime non autorise.'}
  if(-not($packRoots | Where-Object {$file.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)})){throw "Fichier hors packs runtime officiels en cache : $relative"}
  if([IO.Path]::GetExtension($file) -notin @('.dll','.exe')){throw "Type de runtime inattendu : $relative"}
  $oldFile=Join-Path $source $relative
  if(-not(Test-Path -LiteralPath $oldFile -PathType Leaf)){throw "Runtime source incomplet : $relative"}
  if(((Get-Item -LiteralPath $oldFile).Attributes -band [IO.FileAttributes]::ReparsePoint) -or ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Un fichier de runtime ne doit pas etre un lien symbolique.'}
  $expected=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
  $actual=(Get-FileHash -LiteralPath $oldFile -Algorithm SHA256).Hash
  if($actual -ne $expected){throw "Runtime source different du pack officiel resolu : $relative"}
  if(Test-Path -LiteralPath (Join-Path $target $relative)){throw "Le filtre de publication a laisse un runtime inattendu : $relative"}
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
  $parent=Split-Path -Parent $link
  if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent | Out-Null}
  New-Item -ItemType HardLink -Path $link -Target $entry.Source | Out-Null
  if((Get-FileHash -LiteralPath $link -Algorithm SHA256).Hash -ne $entry.Sha256){throw "Lien de runtime invalide : $($entry.RelativePath)"}
}
Remove-Item -LiteralPath $temporaryTargets,$inventory
[pscustomobject]@{OutputDirectory=$target;RuntimeFilesShared=$runtimeFiles.Count;RuntimeBytesReused=($runtimeFiles.Values | Measure-Object Length -Sum).Sum;RuntimeVersion=$RuntimeVersion}
