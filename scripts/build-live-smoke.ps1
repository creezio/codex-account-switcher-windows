param([string]$BinaryDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' | ForEach-Object FullName
$references = @('/nologo','/target:exe','/platform:anycpu','/warn:4','/warnaserror+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll')
$bin=if($BinaryDirectory){[IO.Path]::GetFullPath($BinaryDirectory)}else{Join-Path $repo 'build'}
& $compiler ($references + @('/main:LiveRelaySmoke',('/out:' + (Join-Path $bin 'LiveRelaySmoke.exe')),(Join-Path $repo 'tests\LiveRelaySmoke.cs')) + $sources)
if ($LASTEXITCODE -ne 0) { throw 'Live smoke harness compilation failed.' }
