$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $repo 'work\test-bin\CatalogSmoke.exe'
$sources=Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler (@('/nologo','/target:exe','/main:CatalogSmoke','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll',('/out:'+$exe),(Join-Path $repo 'tests\CatalogSmoke.cs'))+$sources)
if($LASTEXITCODE -ne 0){throw 'Catalog benchmark compilation failed.'}
& $exe (Join-Path $repo 'work\catalog-benchmark')
if($LASTEXITCODE -ne 0){throw 'Catalog benchmark failed.'}
