<# Read-only: reports whether the manual-test accounts exist on the VM and their group memberships. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
"host: $env:COMPUTERNAME"
foreach ($n in 'pmrdp-std', 'pmrdp-adm') {
    $u = Get-LocalUser -Name $n -ErrorAction SilentlyContinue
    if (-not $u) { "$n : NOT FOUND"; continue }
    $groups = foreach ($g in 'S-1-5-32-544', 'S-1-5-32-555', 'S-1-5-32-545') { $grp = Get-LocalGroup -SID $g; if (Get-LocalGroupMember -SID $g -ErrorAction SilentlyContinue | Where-Object SID -eq $u.SID) { $grp.Name } }
    "$n : enabled=$($u.Enabled) groups=$($groups -join ', ')"
}
