<# Disposable EC2 VM only (run as SYSTEM via SSM). Prepares or removes the fixture for the RDP manual checks:
     - protected image root C:\PMCloudTest-20261107\Disks (SYSTEM/Administrators full, Users read)
     - one 512 MiB expandable VHDX, GPT, one NTFS volume labelled PMRDPTEST, with sentinel files and hashes
     - helper policy WriteMode=VirtualDisksOnly, AllowedImageRoots=<image root>
   It never touches a disk unless Get-DiskImage maps that disk number to the fixture VHDX and the disk is
   file-backed, not boot/system, and exactly the fixture size. No accounts or passwords are handled here.
   -Teardown removes only what -Setup created (policy key only if it still holds this fixture's values). #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [switch]$DisposableVmConfirmed,
      [switch]$Setup, [switch]$Teardown, [switch]$Reattach, [switch]$Verify)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
$root = 'C:\PMCloudTest-20261107\Disks'
$vhdx = Join-Path $root 'pm-rdp-test.vhdx'
$size = 512MB
$key = 'HKLM:\SOFTWARE\PartitionManager\Helper'
$state = 'C:\PMCloudTest-20261107\manual-fixture.json'

function Fixture-Disk {
    $img = Get-DiskImage -ImagePath $vhdx -ErrorAction SilentlyContinue
    if (-not $img -or -not $img.Attached) { return $null }
    $d = Get-Disk -Number $img.Number
    if ($d.BusType -ne 'File Backed Virtual' -or $d.IsBoot -or $d.IsSystem -or $d.Size -ne $size) { throw "Disk $($d.Number) does not look like the fixture; refusing." }
    $d
}
function Attach { $f = "$env:TEMP\pm-dp-$([guid]::NewGuid().ToString('N')).txt"; Set-Content $f "select vdisk file=`"$vhdx`"`r`nattach vdisk" -Encoding ascii; diskpart /s $f | Out-Null; Remove-Item $f }
function Show {
    $d = Fixture-Disk
    $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
    "policy: WriteMode=$($p.WriteMode) AllowedImageRoots=$($p.AllowedImageRoots -join ';')"
    if ($d) {
        "fixture disk $($d.Number) $($d.PartitionStyle) size=$($d.Size) location=$($d.Location)"
        Get-Partition -DiskNumber $d.Number | ForEach-Object { $v = $_ | Get-Volume -ErrorAction SilentlyContinue; "  partition $($_.PartitionNumber) offset=$($_.Offset) size=$($_.Size) letter=$($_.DriveLetter) label=$($v.FileSystemLabel) fs=$($v.FileSystem)" }
    } else { 'fixture disk: not attached' }
}

if ($Setup) {
    if (Test-Path $vhdx) { throw "Fixture already exists at $vhdx; run -Teardown first." }
    if (Test-Path $key) { throw 'A helper policy key already exists; refusing to overwrite it.' }
    New-Item -ItemType Directory -Force $root | Out-Null
    icacls $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
    if ($LASTEXITCODE) { throw 'icacls failed' }
    $f = "$env:TEMP\pm-dp-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content $f "create vdisk file=`"$vhdx`" maximum=512 type=expandable`r`nselect vdisk file=`"$vhdx`"`r`nattach vdisk" -Encoding ascii
    diskpart /s $f | Out-Null; Remove-Item $f
    $d = Fixture-Disk
    if (-not $d) { throw 'fixture did not attach' }
    if ($d.PartitionStyle -ne 'RAW') { throw 'new fixture is not RAW; refusing.' }
    Initialize-Disk -Number $d.Number -PartitionStyle GPT
    $part = New-Partition -DiskNumber $d.Number -Size 256MB -AssignDriveLetter
    Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel PMRDPTEST -Confirm:$false | Out-Null
    $letter = (Get-Partition -DiskNumber $d.Number -PartitionNumber $part.PartitionNumber).DriveLetter
    1..5 | ForEach-Object { $b = New-Object byte[] (1MB * $_); ([Random]::new($_)).NextBytes($b); [IO.File]::WriteAllBytes("${letter}:\sentinel-$_.bin", $b) }
    $hashes = Get-ChildItem "${letter}:\sentinel-*.bin" | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
    New-Item -Force $key | Out-Null
    Set-ItemProperty $key -Name WriteMode -Value 'VirtualDisksOnly' -Type String
    Set-ItemProperty $key -Name AllowedImageRoots -Value @($root) -Type MultiString
    [pscustomobject]@{ Created = (Get-Date).ToString('o'); Vhdx = $vhdx; Root = $root; Hashes = $hashes; PolicyCreated = $true } | ConvertTo-Json -Depth 4 | Set-Content $state -Encoding UTF8
    'SETUP DONE'; Show
}
if ($Reattach) { if (-not (Fixture-Disk)) { Attach }; Show }
if ($Verify) {
    Show
    $s = Get-Content -Raw $state | ConvertFrom-Json
    $d = Fixture-Disk
    if ($d) {
        $v = Get-Partition -DiskNumber $d.Number | Get-Volume -ErrorAction SilentlyContinue | Where-Object FileSystem -eq 'NTFS' | Select-Object -First 1
        $l = ($v | Get-Partition).DriveLetter
        foreach ($h in $s.Hashes) { $now = (Get-FileHash "${l}:\$($h.Name)" -Algorithm SHA256).Hash; "{0} {1}" -f $(if ($now -eq $h.Sha256) { 'HASH-OK  ' } else { 'HASH-DIFF' }), $h.Name }
    }
    $j = 'C:\ProgramData\PartitionManager\Audit'
    if (Test-Path $j) { Get-ChildItem $j | Sort-Object LastWriteTime | Select-Object -Last 8 | ForEach-Object { "audit: $($_.Name) $($_.Length) $($_.LastWriteTime.ToString('o'))" } }
}
if ($Teardown) {
    $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
    if ($p -and $p.WriteMode -eq 'VirtualDisksOnly' -and (@($p.AllowedImageRoots) -join ';') -eq $root) { Remove-Item $key -Recurse -Force; 'policy key removed' }
    elseif ($p) { 'policy key NOT removed: it no longer holds this fixture''s values' }
    $d = Fixture-Disk
    if ($d) { $f = "$env:TEMP\pm-dp-$([guid]::NewGuid().ToString('N')).txt"; Set-Content $f "select vdisk file=`"$vhdx`"`r`ndetach vdisk" -Encoding ascii; diskpart /s $f | Out-Null; Remove-Item $f; 'fixture detached' }
    if (Test-Path $vhdx) { Remove-Item $vhdx -Force; 'fixture VHDX deleted' }
    if ((Test-Path $root) -and -not (Get-ChildItem $root -Force)) { Remove-Item $root -Force; 'image root removed' }
    if (Test-Path $state) { Remove-Item $state -Force }
    Show
}
