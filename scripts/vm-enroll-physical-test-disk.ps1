<# TEST VM ONLY (run as SYSTEM via SSM on the identified disposable EC2 instance). Enrols the VM's own boot disk for
   in-place shrink/extend by writing its MSFT_Disk.UniqueId to HKLM\SOFTWARE\PartitionManager\Helper\PhysicalTestDiskIds.
   Requested explicitly by the owner for this VM; a snapshot must exist first. -Remove withdraws the enrolment.
   Nothing else on the disk is changed by this script. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [switch]$DisposableVmConfirmed,
      [Parameter(Mandatory = $true)][string]$RecoverySnapshotId, [switch]$Remove)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
if ($RecoverySnapshotId -notmatch '^snap-[0-9a-f]{17}$') { throw 'A recovery snapshot id is required.' }
$key = 'HKLM:\SOFTWARE\PartitionManager\Helper'
if (-not (Test-Path $key)) { throw 'Helper policy key is absent; refusing.' }
if ($Remove) {
    Remove-ItemProperty $key -Name PhysicalTestDiskIds -ErrorAction SilentlyContinue
    "enrolment removed; PhysicalTestDiskIds present: $([bool](Get-ItemProperty $key).PhysicalTestDiskIds)"
    return
}
$boot = @(Get-Disk | Where-Object { $_.IsBoot -and $_.IsSystem })
if ($boot.Count -ne 1) { throw "Expected exactly one boot+system disk, found $($boot.Count)." }
$d = $boot[0]
if ([string]::IsNullOrWhiteSpace($d.UniqueId)) { throw 'Boot disk has no UniqueId; refusing.' }
Set-ItemProperty $key -Name PhysicalTestDiskIds -Value @($d.UniqueId) -Type MultiString
Set-ItemProperty $key -Name PhysicalTestEnrolmentNote -Value "boot disk $($d.Number) $($d.FriendlyName) enrolled $(Get-Date -Format o); recovery snapshot $RecoverySnapshotId" -Type String
"enrolled disk $($d.Number) ($($d.FriendlyName), $([math]::Round($d.Size/1GB,1)) GiB) UniqueId=$($d.UniqueId)"
Get-Partition -DiskNumber $d.Number | ForEach-Object { "  partition $($_.PartitionNumber) offset=$($_.Offset) size=$($_.Size) letter=$($_.DriveLetter) type=$($_.Type)" }
$c = Get-PartitionSupportedSize -DriveLetter C
"C: supported size: min $([math]::Round($c.SizeMin/1GB,2)) GiB, max $([math]::Round($c.SizeMax/1GB,2)) GiB"
