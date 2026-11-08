<# Disposable EC2 VM only. Downloads one file from a short-lived URL (never logged) and verifies its SHA-256; deletes it on mismatch. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [Parameter(Mandatory = $true)][string]$Url,
      [Parameter(Mandatory = $true)][string]$ExpectedSha256, [Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
$Url = $null
$h = (Get-FileHash $Destination -Algorithm SHA256).Hash
if ($h -ne $ExpectedSha256.ToUpperInvariant()) { Remove-Item $Destination -Force; throw "hash mismatch $h" }
"OK $Destination $h"
