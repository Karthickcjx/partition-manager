<# Isolated-VM lifecycle harness. Never run on the host. Interactive UAC, accessibility
   and reboot/recovery checks remain manual; compilation is not installation validation. #>
param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [switch]$DisposableVmConfirmed,
    [switch]$RunLifecycle,
    [string]$ExpectedEc2InstanceId = ''
)
$ErrorActionPreference='Stop'
$machine=Get-CimInstance Win32_ComputerSystem
$recognizedVm=$machine.Model -match 'Virtual|VMware|VirtualBox|KVM|QEMU|Parallels'
if($ExpectedEc2InstanceId) {
    if($ExpectedEc2InstanceId -notmatch '^i-[0-9a-f]{17}$' -or $machine.Manufacturer -ne 'Amazon EC2') { throw 'Expected an explicitly identified EC2 VM.' }
    $token=Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{'X-aws-ec2-metadata-token-ttl-seconds'='60'} -TimeoutSec 10
    $actualId=Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{'X-aws-ec2-metadata-token'=$token} -TimeoutSec 10
    if($actualId -ne $ExpectedEc2InstanceId) { throw 'EC2 instance identity mismatch.' }
    $recognizedVm=$true
}
if (-not $DisposableVmConfirmed -or -not $recognizedVm) {
    throw 'Refused: an explicitly confirmed disposable Windows VM is required. VM detection alone is not authorization.'
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run inside the VM as administrator.' }
$setup=(Resolve-Path -LiteralPath $Installer).Path
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$evidence=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
$rows=[Collections.Generic.List[object]]::new()
function Record($check,$status,$detail) { $rows.Add([pscustomobject]@{Check=$check;Status=$status;Detail=$detail}) }
Record 'VM preflight' 'PASS' $machine.Model
Record 'Installer SHA256' 'RECORDED' (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
if ($RunLifecycle) {
    $installDir=Join-Path $env:ProgramFiles 'PartitionManager'
    if(Test-Path -LiteralPath $installDir) { throw 'Fresh VM required: install directory already exists. Restore the VM snapshot for upgrade testing.' }
    function Install($stage) {
        $log=Join-Path $evidence "$stage.log"
        $p=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$log`"") -WindowStyle Hidden -PassThru -Wait
        if($p.ExitCode -ne 0) { throw "Installer $stage failed: $($p.ExitCode)" }
        Record $stage 'PASS' "Exit 0; log $log"
    }
    Install 'Fresh-install'
    foreach($name in 'PartitionManager.exe','PartitionManager.Helper.exe') {
        $path=Join-Path $installDir $name
        if(-not (Test-Path -LiteralPath $path)) { throw "Missing $path" }
        Get-Acl -LiteralPath $path | Format-List * | Out-File -LiteralPath (Join-Path $evidence "$name-acl.txt")
    }
    Record 'Program Files placement' 'PASS' $installDir
    $sentinel=Join-Path $installDir 'unrelated-sentinel.txt'
    $value=[guid]::NewGuid().ToString(); Set-Content -LiteralPath $sentinel -Value $value
    Install 'Repair-rerun'
    $uninstaller=Join-Path $installDir 'unins000.exe'
    if(-not (Test-Path -LiteralPath $uninstaller)) { throw 'Uninstaller not found' }
    $p=Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait -PassThru
    if($p.ExitCode -ne 0 -or (Get-Content -Raw -LiteralPath $sentinel).Trim() -ne $value) { throw 'Uninstall failed or removed unrelated data' }
    Record 'Uninstall preserves sentinel' 'PASS' $sentinel
    Install 'Reinstall'
    Record 'Standard-user replacement resistance' 'PENDING' 'Use a separate standard account; attempt replacing helper and dependencies; retain denied results.'
}
foreach($check in 'Upgrade from v0.4','Standard-user launch','Administrator launch','UAC approval','UAC rejection','Elevated helper IPC authentication','DLL search/extraction','125/150/175/200% DPI','Narrator and keyboard navigation') {
    Record $check 'PENDING' 'Requires interactive isolated-VM validation; do not infer PASS from this harness.'
}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'installer-validation.json') -Encoding utf8
$rows | Format-Table -Wrap
