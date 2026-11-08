param([string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' }
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t }) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
$v = Get-Volume -DriveLetter C; "C: used GiB = {0:N1} of {1:N1}" -f (($v.Size - $v.SizeRemaining)/1GB), ($v.Size/1GB)
