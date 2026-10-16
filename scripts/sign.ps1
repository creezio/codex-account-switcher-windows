[CmdletBinding()]
param([Parameter(Mandatory)][string]$BinaryDirectory,[Parameter(Mandatory)][string]$CertificateThumbprint,[string]$TimestampServer='http://timestamp.digicert.com')
$ErrorActionPreference='Stop'
$folder=[IO.Path]::GetFullPath($BinaryDirectory)
if($CertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$'){throw 'Invalid certificate thumbprint.'}
$certificate=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$CertificateThumbprint)
if(-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)){throw 'A valid signing certificate with a private key is required.'}
if(-not ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')){throw 'The certificate does not permit code signing.'}
foreach($name in @('CodexAccountSwitcher.exe','CreezioRelay.exe')){
    $path=Join-Path $folder $name
    $signature=Set-AuthenticodeSignature -LiteralPath $path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if($signature.Status -ne 'Valid'){throw ('Signing failed: '+$name+' / '+$signature.Status)}
    if((Get-AuthenticodeSignature -LiteralPath $path).Status -ne 'Valid'){throw ('Signature verification failed: '+$name)}
}
Write-Output 'Both product binaries are signed and verified. Package with -SkipBuild to preserve signatures.'
