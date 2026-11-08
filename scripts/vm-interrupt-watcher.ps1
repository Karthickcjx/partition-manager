<# Disposable EC2 VM only, run as SYSTEM via SSM. Arms a deterministic interruption of a REAL helper operation that a
   person starts from the installed app over RDP (the helper only accepts its own PartitionManager.exe as client).
   It watches the protected journal; when a NEW operation records 'StepStarted' (intent flushed, native call about to
   run or running) it either kills the elevated helper (-Mode Kill) or restarts Windows (-Mode Restart).
   This is a process kill / orderly-forced OS restart, NOT a power-loss or device-failure test. Whether the native
   call had already begun is unknown; the resulting state is determined afterwards by layout and hash comparison. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [switch]$DisposableVmConfirmed,
      [Parameter(Mandatory = $true)][ValidateSet('Kill', 'Restart')][string]$Mode, [string]$Label = 'interrupt',
      [int]$TimeoutMinutes = 60, [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\State')
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$audit = 'C:\ProgramData\PartitionManager\Audit'
$known = @{}; foreach ($f in [IO.Directory]::GetFiles($audit, 'operation-*.jsonl')) { $known[$f] = $true }
$log = [Collections.Generic.List[string]]::new()
function Note($m) { $log.Add("$((Get-Date).ToUniversalTime().ToString('o')) $m"); $log | Set-Content (Join-Path $EvidenceDirectory "interrupt-$Label.log") -Encoding UTF8 }
function LastPhase($path) {
    try {
        $fs = [IO.FileStream]::new($path, 'Open', 'Read', 'ReadWrite'); $sr = [IO.StreamReader]::new($fs)
        $lines = $sr.ReadToEnd().Split("`n", [StringSplitOptions]::RemoveEmptyEntries); $sr.Dispose()
        if ($lines.Count -eq 0) { return '' }
        return (($lines[-1] | ConvertFrom-Json).Event.Phase)
    } catch { return '?' }
}
Note "ARMED mode=$Mode existing-journals=$($known.Count) timeout=${TimeoutMinutes}m"
"ARMED mode=$Mode (start the operation in the app now)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes); $seen = @{}
while ((Get-Date) -lt $deadline) {
    foreach ($f in [IO.Directory]::GetFiles($audit, 'operation-*.jsonl')) {
        if ($known.ContainsKey($f)) { continue }
        $phase = LastPhase $f
        if ($seen[$f] -ne $phase) { $seen[$f] = $phase; Note "journal $([IO.Path]::GetFileName($f)) phase=$phase" }
        if ($phase -eq 'StepStarted') {
            $helpers = @(Get-Process PartitionManager.Helper -ErrorAction SilentlyContinue)
            if ($Mode -eq 'Kill') {
                $helpers | Stop-Process -Force
                Note "KILLED helper pid(s) $($helpers.Id -join ',') at phase StepStarted of $([IO.Path]::GetFileName($f))"
                Start-Sleep -Milliseconds 500
                Note "after kill: journal last phase=$(LastPhase $f); helper running=$([bool](Get-Process PartitionManager.Helper -ErrorAction SilentlyContinue))"
                "RESULT KILLED at StepStarted ($([IO.Path]::GetFileName($f)))"; $log; return
            } else {
                Note "RESTARTING Windows at phase StepStarted of $([IO.Path]::GetFileName($f)); helper pid(s) $($helpers.Id -join ',')"
                Restart-Computer -Force
                "RESULT RESTART issued at StepStarted"; return
            }
        }
        if ($phase -in 'StepReturned', 'StepUncertain', 'Verified', 'Failed') { $known[$f] = $true; Note "MISSED: $([IO.Path]::GetFileName($f)) reached $phase before interruption; waiting for the next operation" }
    }
    Start-Sleep -Milliseconds 2
}
Note 'TIMEOUT: no operation reached StepStarted'
'RESULT TIMEOUT'; $log
