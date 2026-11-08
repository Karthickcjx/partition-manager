<# Disposable EC2 VM only, run as SYSTEM via SSM. READ-ONLY state record used before/after restart and interruption
   tests: installed hashes, data-folder ACL, journal summaries and file hashes, boot-disk layout (never modified),
   fixture VHDX layout and sentinel hashes. -CompareWith <earlier label> diffs against an earlier record. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [Parameter(Mandatory = $true)][string]$Label,
      [string]$CompareWith = '', [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\State')
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$vhdx = 'C:\PMCloudTest-20261107\Disks\pm-rdp-test.vhdx'
$audit = 'C:\ProgramData\PartitionManager\Audit'
function Layout($n) { Get-Partition -DiskNumber $n | Sort-Object Offset | ForEach-Object { $v = $_ | Get-Volume -ErrorAction SilentlyContinue; '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.PartitionNumber, $_.Offset, $_.Size, $_.GptType, $v.FileSystem, $v.FileSystemLabel } }

$boot = Get-Disk | Where-Object { $_.IsBoot -or $_.IsSystem } | Select-Object -First 1
$img = Get-DiskImage -ImagePath $vhdx -ErrorAction SilentlyContinue
$fixture = $null; $sentinels = @()
if ($img -and $img.Attached) {
    $d = Get-Disk -Number $img.Number
    if ($d.BusType -ne 'File Backed Virtual' -or $d.IsBoot -or $d.IsSystem) { throw 'fixture disk check failed' }
    $fixture = [pscustomobject]@{ Number = $d.Number; Style = "$($d.PartitionStyle)"; Size = $d.Size; Layout = @(Layout $d.Number) }
    foreach ($p in Get-Partition -DiskNumber $d.Number | Where-Object DriveLetter) {
        $sentinels += Get-ChildItem "$($p.DriveLetter):\sentinel-*.bin" -ErrorAction SilentlyContinue | ForEach-Object { "$($p.DriveLetter):$($_.Name)|" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
}
$journals = @(Get-ChildItem $audit -Filter 'operation-*.jsonl' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
    $ev = @(); $problem = ''
    try { $ev = @(Get-Content -LiteralPath $_.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Event }) } catch { $problem = $_.Exception.Message }
    [pscustomobject]@{ File = $_.Name; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; Phases = ($ev.Phase -join '>'); Kind = "$($ev[0].Request.Kind)"; RequiresReview = ($problem -ne '' -or ($ev | Select-Object -Last 1).Phase -ne 'Verified'); Problem = $problem } })
$rec = [pscustomobject]@{
    Label = $Label; Utc = (Get-Date).ToUniversalTime().ToString('o'); LastBootUtc = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
    Installed = @(Get-ChildItem 'C:\Program Files\PartitionManager\PartitionManager*.exe' | ForEach-Object { "$($_.Name)|" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    DataAcl = (Get-Acl 'C:\ProgramData\PartitionManager').Sddl -replace '^O:[^D]*', ''
    DataOwner = (Get-Acl 'C:\ProgramData\PartitionManager').Owner
    Policy = (Get-ItemProperty 'HKLM:\SOFTWARE\PartitionManager\Helper' -ErrorAction SilentlyContinue | Select-Object WriteMode, AllowedImageRoots | ConvertTo-Json -Compress)
    BootDisk = [pscustomobject]@{ Number = $boot.Number; Style = "$($boot.PartitionStyle)"; Size = $boot.Size; Layout = @(Layout $boot.Number) }
    Fixture = $fixture; Sentinels = $sentinels; Journals = $journals
    HelperRunning = [bool](Get-Process PartitionManager.Helper -ErrorAction SilentlyContinue)
}
$rec | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDirectory "state-$Label.json") -Encoding UTF8
"RECORD $Label boot=$($rec.LastBootUtc) installed=$($rec.Installed.Count) journals=$($journals.Count) review=$(@($journals | Where-Object RequiresReview).Count) fixture=$(if ($fixture) { "disk $($fixture.Number)" } else { 'not attached' }) sentinels=$($sentinels.Count)"
$journals | ForEach-Object { "journal $($_.File) $($_.Kind) $($_.Phases) review=$($_.RequiresReview) $($_.Problem)" }
if ($CompareWith) {
    $old = Get-Content -Raw (Join-Path $EvidenceDirectory "state-$CompareWith.json") | ConvertFrom-Json
    function Cmp($name, $a, $b) { $sa = ($a | ConvertTo-Json -Depth 6 -Compress); $sb = ($b | ConvertTo-Json -Depth 6 -Compress); "{0} | {1}" -f $(if ($sa -eq $sb) { 'SAME' } else { 'DIFF' }), $name; if ($sa -ne $sb) { "   before: $sa"; "   after:  $sb" } }
    "COMPARE $CompareWith -> $Label (rebooted: $($old.LastBootUtc -ne $rec.LastBootUtc))"
    Cmp 'Installed hashes' $old.Installed $rec.Installed
    Cmp 'Data folder owner + DACL' @($old.DataOwner, $old.DataAcl) @($rec.DataOwner, $rec.DataAcl)
    Cmp 'Policy' $old.Policy $rec.Policy
    Cmp 'Boot disk layout (protected; never modified)' $old.BootDisk $rec.BootDisk
    Cmp 'Fixture layout' $old.Fixture.Layout $rec.Fixture.Layout
    Cmp 'Sentinel hashes' $old.Sentinels $rec.Sentinels
    Cmp 'Pre-existing journals (name+hash)' @($old.Journals | ForEach-Object { "$($_.File)|$($_.Sha256)" }) @($rec.Journals | Where-Object { $_.File -in $old.Journals.File } | ForEach-Object { "$($_.File)|$($_.Sha256)" })
}
