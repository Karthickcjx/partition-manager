<# Removes C:\ProgramData\PartitionManager ONLY if it was created by a deleted pmstd* test account (owner SID no longer
   resolves to an existing local account) and contains only security-test probe files. Otherwise changes nothing. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [Parameter(Mandatory = $true)][string]$ExpectedOwnerSid)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
$root = Join-Path $env:ProgramData 'PartitionManager'
if (-not (Test-Path -LiteralPath $root)) { 'ABSENT: nothing to clean'; return }
$owner = (Get-Acl -LiteralPath $root).GetOwner([Security.Principal.SecurityIdentifier]).Value
if ($owner -ne $ExpectedOwnerSid) { "KEPT: owner $owner is not the test account"; return }
if (Get-LocalUser | Where-Object { $_.SID.Value -eq $owner }) { 'KEPT: owner account still exists'; return }
$files = Get-ChildItem -LiteralPath $root -Recurse -File -Force
$unexpected = $files | Where-Object { $_.Name -notlike 'pm-std-*' }
if ($unexpected) { 'KEPT: unexpected files: ' + (($unexpected | ForEach-Object FullName) -join ', '); return }
Remove-Item -LiteralPath $root -Recurse -Force
"REMOVED $root (owner $owner, $($files.Count) probe file(s))"
