<# Read-only: running Partition Manager processes with start time and owner. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
Get-CimInstance Win32_Process -Filter "Name like 'PartitionManager%'" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    "{0} pid {1} parent {2} started {3} owner {4}\{5}" -f $_.Name, $_.ProcessId, $_.ParentProcessId, $_.CreationDate.ToUniversalTime().ToString('o'), $o.Domain, $o.User
}
