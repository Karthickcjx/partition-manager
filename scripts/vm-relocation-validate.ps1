<# Disposable EC2 VM only, run as SYSTEM via SSM. Validates real block-level Move on throwaway VHDX images:
   each scenario creates its own VHDX under <ApprovedRoot>\reloc, partitions/formats/populates it, records file hashes,
   NTFS alternate streams and ACLs, volume serial and partition GUID, runs RelocationProbe (the production WriteEngine),
   then re-verifies everything plus a read-only Repair-Volume -Scan and the boot-sector start field, and deletes the
   image. Never touches any disk that Get-DiskImage does not map to the scenario's own new VHDX. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [switch]$DisposableVmConfirmed,
      [Parameter(Mandatory = $true)][string]$ProbePath, [string]$Only = '',
      [string]$ApprovedRoot = 'C:\PMCloudTest-20261107\Disks', [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\Reloc')
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
$policy = Get-ItemProperty 'HKLM:\SOFTWARE\PartitionManager\Helper'
if ($policy.WriteMode -ne 'VirtualDisksOnly' -or @($policy.AllowedImageRoots) -notcontains $ApprovedRoot) { throw "Helper policy does not approve $ApprovedRoot" }
$root = Join-Path $ApprovedRoot 'reloc'
New-Item -ItemType Directory -Force $root, $EvidenceDirectory | Out-Null
$bootDisk = Get-Disk | Where-Object { $_.IsBoot -or $_.IsSystem }
$bootBefore = ($bootDisk | Get-Partition | ForEach-Object { "$($_.PartitionNumber)|$($_.Offset)|$($_.Size)" }) -join ';'

function Invoke-DiskPartScript([string[]]$lines) { $f = Join-Path $env:TEMP "pm-dp-$([guid]::NewGuid().ToString('N')).txt"; Set-Content $f ($lines -join "`r`n") -Encoding ascii; $o = & "$env:windir\System32\diskpart.exe" /s $f; Remove-Item $f; $o }
function OwnDisk($vhdx) {
    $img = Get-DiskImage -ImagePath $vhdx
    $d = Get-Disk -Number $img.Number
    if ($d.BusType -ne 'File Backed Virtual' -or $d.IsBoot -or $d.IsSystem -or $d.Number -eq $bootDisk.Number) { throw "disk $($d.Number) is not the scenario VHDX" }
    $d
}
function Snapshot($letter, $fs) {
    $r = "${letter}:\"
    $files = Get-ChildItem $r -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\System Volume Information\\' } | Sort-Object FullName |
        ForEach-Object { "$($_.FullName.Substring(3))|$($_.Length)|$($_.Attributes)|" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $extra = @()
    if ($fs -eq 'NTFS') {
        $extra += 'ads|' + (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes((Get-Content -LiteralPath "${r}ads.txt" -Stream secret -Raw))))).Hash
        $extra += 'acl|' + (Get-Acl -LiteralPath "${r}acl.txt").Sddl
    }
    $serial = (Get-CimInstance Win32_Volume -Filter "DriveLetter='${letter}:'").SerialNumber
    [pscustomobject]@{ Files = $files; Extra = $extra; Serial = $serial }
}
function BootStart($letter, $fs) {
    $s = [IO.FileStream]::new("\\.\${letter}:", 'Open', 'Read', 'ReadWrite')
    try { $b = New-Object byte[] 512; [void]$s.Read($b, 0, 512) } finally { $s.Dispose() }
    if ($fs -eq 'exFAT') { [BitConverter]::ToUInt64($b, 0x40) } else { [BitConverter]::ToUInt32($b, 0x1C) }
}

$scenarios = @(
    @{ Name = 'gpt-ntfs-jump';  Style = 'GPT'; Fs = 'NTFS';  At = 32;  Size = 128; To = 256 }
    @{ Name = 'gpt-ntfs-right'; Style = 'GPT'; Fs = 'NTFS';  At = 32;  Size = 128; To = 48 }
    @{ Name = 'gpt-ntfs-left';  Style = 'GPT'; Fs = 'NTFS';  At = 128; Size = 128; To = 64 }
    @{ Name = 'gpt-ntfs-1mib';  Style = 'GPT'; Fs = 'NTFS';  At = 32;  Size = 128; To = 33 }
    @{ Name = 'mbr-ntfs-right'; Style = 'MBR'; Fs = 'NTFS';  At = 16;  Size = 128; To = 40 }
    @{ Name = 'gpt-fat32-right'; Style = 'GPT'; Fs = 'FAT32'; At = 32; Size = 128; To = 80 }
    @{ Name = 'gpt-exfat-left'; Style = 'GPT'; Fs = 'exFAT'; At = 160; Size = 128; To = 64 }
    @{ Name = 'gpt-ntfs-inuse'; Style = 'GPT'; Fs = 'NTFS';  At = 32;  Size = 128; To = 48; HoldOpen = $true }
)
if ($Only) { $scenarios = $scenarios | Where-Object { $_.Name -like $Only } }
$results = [Collections.Generic.List[object]]::new()

foreach ($sc in $scenarios) {
    $vhdx = Join-Path $root "reloc-$($sc.Name).vhdx"
    $row = [ordered]@{ Scenario = $sc.Name; Status = 'FAIL'; Checks = [Collections.Generic.List[string]]::new() }
    $hold = $null
    try {
        if (Test-Path $vhdx) { Invoke-DiskPartScript @("select vdisk file=`"$vhdx`"", 'detach vdisk') | Out-Null; Remove-Item $vhdx -Force }
        Invoke-DiskPartScript @("create vdisk file=`"$vhdx`" maximum=512 type=expandable", "select vdisk file=`"$vhdx`"", 'attach vdisk') | Out-Null
        $d = OwnDisk $vhdx
        if ($d.PartitionStyle -ne 'RAW') { throw 'new VHDX not RAW' }
        Initialize-Disk -Number $d.Number -PartitionStyle $sc.Style
        $p = New-Partition -DiskNumber $d.Number -Offset ($sc.At * 1MB) -Size ($sc.Size * 1MB) -AssignDriveLetter
        Format-Volume -Partition $p -FileSystem $sc.Fs -NewFileSystemLabel ('RL' + ($sc.Name -replace '[^a-z0-9]', '').Substring(0, 9)) -Confirm:$false | Out-Null
        $p = Get-Partition -DiskNumber $d.Number -PartitionNumber $p.PartitionNumber
        $L = $p.DriveLetter; $r = "${L}:\"
        $rng = [Random]::new($sc.Name.Length * 7919)
        foreach ($i in 1..24) {
            $dir = Join-Path $r ("dir{0}\sub{1}" -f ($i % 4), ($i % 3)); New-Item -ItemType Directory -Force $dir | Out-Null
            $b = New-Object byte[] ($rng.Next(1, 2MB)); $rng.NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $dir "file$i.bin"), $b)
        }
        $uni = "$([char]0xFC)n$([char]0xEF)c$([char]0xF8)d$([char]0xE9)-$([char]0x540D)$([char]0x524D).txt"   # non-ASCII name (script travels as ASCII)
        [IO.File]::WriteAllText((Join-Path $r $uni), 'unicode name')
        [IO.File]::WriteAllText((Join-Path $r 'readonly.txt'), 'ro'); (Get-Item (Join-Path $r 'readonly.txt')).IsReadOnly = $true
        if ($sc.Fs -eq 'NTFS') {
            [IO.File]::WriteAllText((Join-Path $r 'ads.txt'), 'main'); Set-Content -LiteralPath (Join-Path $r 'ads.txt') -Stream secret -Value 'alternate stream payload'
            [IO.File]::WriteAllText((Join-Path $r 'acl.txt'), 'acl'); icacls (Join-Path $r 'acl.txt') /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' '*S-1-5-32-545:R' | Out-Null
        }
        $before = Snapshot $L $sc.Fs
        $guidBefore = $p.Guid
        $row.Checks.Add("populated $($before.Files.Count) files, serial $($before.Serial), guid $guidBefore")

        if ($sc.HoldOpen) { $hold = [IO.File]::Open((Join-Path $r 'dir1\sub1\file1.bin'), 'Open', 'Read', 'Read') }
        $journal = Join-Path $EvidenceDirectory "journal-$($sc.Name)"
        if (Test-Path $journal) { Remove-Item $journal -Recurse -Force }
        $out = & $ProbePath --disk $d.Number --partition $p.PartitionNumber --offset ($sc.To * 1MB) --image-root $root --journal $journal 2>&1
        $exit = $LASTEXITCODE
        $out | Set-Content (Join-Path $EvidenceDirectory "probe-$($sc.Name).json") -Encoding UTF8
        if ($hold) { $hold.Dispose(); $hold = $null }
        $resp = ($out -join "`n") | ConvertFrom-Json -ErrorAction SilentlyContinue
        $row.Checks.Add("probe exit $exit code $($resp.Code) in $($resp.Seconds)s")

        $after = Get-Partition -DiskNumber $d.Number | Where-Object { $_.Type -notin 'Reserved' -and $_.Size -eq ($sc.Size * 1MB) }
        if ($sc.HoldOpen) {
            $ok = $exit -ne 0 -and $resp.Message -match 'in use' -and $resp.Message -match 'Nothing was changed' -and $after.Offset -eq ($sc.At * 1MB)
            $row.Checks.Add("in-use refusal: $ok ($($resp.Message))")
            $now = Snapshot $after.DriveLetter $sc.Fs
            $same = ($now.Files -join "`n") -eq ($before.Files -join "`n")
            $row.Checks.Add("files unchanged after refusal: $same")
            if ($ok -and $same) { $row.Status = 'PASS' }
        } else {
            $checks = [ordered]@{}
            $checks['engine OK'] = ($exit -eq 0 -and $resp.Code -eq 'OK')
            $checks['new offset'] = ($after.Offset -eq ($sc.To * 1MB))
            $checks['same size'] = ($after.Size -eq ($sc.Size * 1MB))
            $checks['drive letter kept'] = ($after.DriveLetter -eq $L)
            if ($sc.Style -eq 'GPT') { $checks['partition GUID kept'] = ($after.Guid -eq $guidBefore) }
            $now = Snapshot $after.DriveLetter $sc.Fs
            $checks['file hashes, sizes, attributes'] = (($now.Files -join "`n") -eq ($before.Files -join "`n"))
            if ($sc.Fs -eq 'NTFS') { $checks['ADS and ACL'] = (($now.Extra -join "`n") -eq ($before.Extra -join "`n")) }
            $checks['volume serial kept'] = ($now.Serial -eq $before.Serial)
            if ($sc.Fs -eq 'NTFS') {
                $scan = Repair-Volume -DriveLetter $after.DriveLetter -Scan
                $checks["file system scan ($scan)"] = ("$scan" -eq 'NoErrorsFound')
            } else {
                # Repair-Volume supports only NTFS/ReFS; chkdsk without /f is read-only and exits 0 when no errors are found.
                $chk = & "$env:windir\System32\chkdsk.exe" "$($after.DriveLetter):" 2>&1
                $chk | Set-Content (Join-Path $EvidenceDirectory "chkdsk-$($sc.Name).txt") -Encoding UTF8
                $checks["chkdsk read-only (exit $LASTEXITCODE)"] = ($LASTEXITCODE -eq 0)
            }
            $bs = BootStart $after.DriveLetter $sc.Fs
            $checks["boot sector start = $bs"] = ($bs -eq ($sc.To * 1MB / 512))
            $jr = Get-ChildItem $journal -Filter 'operation-*.jsonl' | Select-Object -First 1
            $phases = @(Get-Content $jr.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Event.Phase })
            $checks["journal ($($phases.Count) records, $(@($phases | Where-Object { $_ -eq 'ChunkCopied' }).Count) chunk checkpoints) ends Verified"] = ($phases[-1] -eq 'Verified')
            foreach ($k in $checks.Keys) { $row.Checks.Add("$(if ($checks[$k]) { 'ok  ' } else { 'FAIL' }) $k") }
            if (-not ($checks.Values -contains $false)) { $row.Status = 'PASS' }
        }
    }
    catch { $row.Checks.Add("ERROR $($_.Exception.Message)") }
    finally {
        if ($hold) { $hold.Dispose() }
        try { Invoke-DiskPartScript @("select vdisk file=`"$vhdx`"", 'detach vdisk') | Out-Null } catch {}
        if (Test-Path $vhdx) { Remove-Item $vhdx -Force -ErrorAction SilentlyContinue }
        $row.Checks.Add("cleanup: image removed = $(-not (Test-Path $vhdx))")
    }
    $results.Add([pscustomobject]$row)
    "{0} | {1}" -f $row.Status, $row.Scenario; $row.Checks | ForEach-Object { "    $_" }
}
$bootAfter = ($bootDisk | Get-Partition | ForEach-Object { "$($_.PartitionNumber)|$($_.Offset)|$($_.Size)" }) -join ';'
"boot disk layout unchanged: $($bootAfter -eq $bootBefore)"
if ((Test-Path $root) -and -not (Get-ChildItem $root -Force)) { Remove-Item $root -Force }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory 'relocation-results.json') -Encoding UTF8
"SUMMARY pass=$(@($results | Where-Object Status -eq 'PASS').Count) fail=$(@($results | Where-Object Status -ne 'PASS').Count)"
