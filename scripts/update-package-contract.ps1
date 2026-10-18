# Shared SemVer feature threshold used by package creation and verification.
function Test-SwitcherCodexBridgeRequired([string]$Version) {
  if($Version.Length -gt 100 -or $Version -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'){throw 'Version de paquet invalide.'}
  $major=[int]$Matches[1];$minor=[int]$Matches[2];$patch=[int]$Matches[3];$prerelease=[string]$Matches[4]
  $parts=$prerelease.Split('.');foreach($part in $parts){if($part -match '\A0[0-9]+\z'){throw 'Version prerelease non canonique.'}}
  $core=[Version]::new($major,$minor,$patch).CompareTo([Version]'0.29.0')
  if($core -ne 0){return $core -gt 0}
  if(-not $prerelease){return $true}
  $threshold=@('beta','3')
  for($index=0;$index -lt $threshold.Count;$index++){
    if($index -ge $parts.Count){return $false}
    $left=$parts[$index];$right=$threshold[$index]
    if($left -ceq $right){continue}
    $leftNumeric=$left -match '\A[0-9]+\z';$rightNumeric=$right -match '\A[0-9]+\z'
    if($leftNumeric -ne $rightNumeric){return -not $leftNumeric}
    if($leftNumeric -and $left.Length -ne $right.Length){return $left.Length -gt $right.Length}
    return [StringComparer]::Ordinal.Compare($left,$right) -gt 0
  }
  return $true
}
