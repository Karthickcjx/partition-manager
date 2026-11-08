<# Read-only: reports ACLs/owners of the install folder, its ancestors and the ProgramData folders, plus leftovers
   from security test runs. Changes nothing. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
foreach ($p in "$env:ProgramFiles\PartitionManager", $env:ProgramFiles, "$env:SystemDrive\", "$env:ProgramData\PartitionManager", "$env:ProgramData\PartitionManager\Audit") {
    if (Test-Path -LiteralPath $p) { $a = Get-Acl -LiteralPath $p; "{0}`n  owner={1}`n  sddl={2}" -f $p, $a.Owner, $a.Sddl } else { "$p : ABSENT" }
}
if (Test-Path "$env:ProgramData\PartitionManager") { Get-ChildItem -LiteralPath "$env:ProgramData\PartitionManager" -Recurse -Force | ForEach-Object { "  item {0} owner={1}" -f $_.FullName, (Get-Acl -LiteralPath $_.FullName).Owner } }
"users: " + ((Get-LocalUser | Where-Object Name -like 'pmstd*' | ForEach-Object Name) -join ',')
"profiles: " + ((Get-CimInstance Win32_UserProfile | Where-Object LocalPath -like '*pmstd*' | ForEach-Object LocalPath) -join ',')
$j = Get-Content -Raw 'C:\PMCloudTest-20261107\Security\security-baseline.json' | ConvertFrom-Json
"created: " + ($j.Created -join '; ')
$j.Rows | Where-Object { $_.Area -in 'Account', 'Audit' -or $_.Check -like '*DACL*' } | ForEach-Object { "row {0} | {1} | {2}" -f $_.Status, $_.Check, $_.Detail }
