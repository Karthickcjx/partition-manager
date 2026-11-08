<# Read-only: prints compact rows from a security result JSON on the VM (optionally only non-install rows). #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [Parameter(Mandatory = $true)][string]$Label, [switch]$SkipInstallRows)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
$j = Get-Content -Raw "C:\PMCloudTest-20261107\Security\security-$Label.json" | ConvertFrom-Json
$j.Rows | Where-Object { -not $SkipInstallRows -or $_.Area -ne 'Install' -or $_.Status -ne 'PASS' } | ForEach-Object {
    $d = ($_.Detail -replace '\s+', ' '); "{0} | {1} | {2} | {3}" -f $_.Status, $_.Area, $_.Check, $d.Substring(0, [Math]::Min(260, $d.Length)) }
"install rows: " + (@($j.Rows | Where-Object Area -eq 'Install').Count) + " (fail " + (@($j.Rows | Where-Object { $_.Area -eq 'Install' -and $_.Status -eq 'FAIL' }).Count) + ")"
"created: " + ($j.Created -join '; ')
