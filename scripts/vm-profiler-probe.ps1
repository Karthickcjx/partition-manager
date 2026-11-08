<# Disposable EC2 VM only, run as SYSTEM via SSM. Tests whether the installed elevated helper honours CoreCLR profiler
   environment variables (CORECLR_ENABLE_PROFILING / CORECLR_PROFILER_PATH). A copy of a benign signed Windows DLL is
   placed in a user-writable folder; if the runtime calls LoadLibrary on it, any DLL there would run DllMain inside the
   elevated helper. Evidence comes from the .NET Runtime entries in the Application event log and the helper's
   module list. Nothing is installed; the probe folder is removed afterwards. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId, [switch]$DisposableVmConfirmed,
      [string]$Label = 'profiler', [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\Security',
      [string]$TargetPath = '', [string]$TargetArguments = '--probe-invalid-arguments', [switch]$FrameworkVariables)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
# Default target is the installed elevated helper; -TargetPath runs a positive control to prove the detector works.
$helper = if ($TargetPath) { $TargetPath } else { Join-Path $env:ProgramFiles 'PartitionManager\PartitionManager.Helper.exe' }
$ver = (Get-Item $helper).VersionInfo.ProductVersion
$dir = Join-Path $env:PUBLIC "pm-profiler-$Label"
New-Item -ItemType Directory -Force $dir | Out-Null
$dll = Join-Path $dir 'probe-profiler.dll'
Copy-Item "$env:windir\System32\version.dll" $dll
# CoreCLR does not report profiler failures to the event log and unloads a failed profiler at once, so detection uses
# temporary object-access auditing on the probe DLL (restored afterwards). 0x8007007F from .NET Framework also proves
# LoadLibrary succeeded (only the DllGetClassObject export was missing).
$fsAudit = '{0CCE921D-69AE-11D9-BED3-505054503030}'
$prevAudit = ((auditpol /get /subcategory:$fsAudit /r | ConvertFrom-Csv) | Select-Object -First 1).'Inclusion Setting'
$clsid = '{' + [guid]::NewGuid() + '}'
$rows = [Collections.Generic.List[object]]::new()
$start = Get-Date
try {
    auditpol /set /subcategory:$fsAudit /success:enable | Out-Null
    $acl = Get-Acl -LiteralPath $dll -Audit
    $acl.AddAuditRule([Security.AccessControl.FileSystemAuditRule]::new([Security.Principal.SecurityIdentifier]'S-1-1-0', 'ReadData, ExecuteFile', 'Success'))
    Set-Acl -LiteralPath $dll -AclObject $acl
    $variants = if ($FrameworkVariables) { @('COR') } else { @('CORECLR', 'DOTNET') }
    foreach ($prefix in $variants) {
        $psi = [Diagnostics.ProcessStartInfo]::new($helper, $TargetArguments)
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        if ($prefix -eq 'CORECLR') {
            $psi.Environment['CORECLR_ENABLE_PROFILING'] = '1'; $psi.Environment['CORECLR_PROFILER'] = $clsid; $psi.Environment['CORECLR_PROFILER_PATH'] = $dll
        } elseif ($prefix -eq 'COR') {
            $psi.Environment['COR_ENABLE_PROFILING'] = '1'; $psi.Environment['COR_PROFILER'] = $clsid; $psi.Environment['COR_PROFILER_PATH'] = $dll
        } else {
            $psi.Environment['DOTNET_EnableDiagnostics'] = '1'; $psi.Environment['CORECLR_ENABLE_PROFILING'] = '1'; $psi.Environment['CORECLR_PROFILER'] = $clsid; $psi.Environment['CORECLR_PROFILER_PATH_64'] = $dll
        }
        $p = [Diagnostics.Process]::Start($psi)
        $mods = @()
        for ($i = 0; $i -lt 40 -and -not $p.HasExited; $i++) { try { $mods += $p.Modules | ForEach-Object FileName } catch {}; Start-Sleep -Milliseconds 25 }
        if (-not $p.WaitForExit(8000)) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
        $loaded = @($mods | Where-Object { $_ -like "$dir*" } | Select-Object -Unique)
        $rows.Add([pscustomobject]@{ Variant = $prefix; Pid = $p.Id; Exit = $p.ExitCode; ProbeInModuleList = ($loaded.Count -gt 0) })
    }
    Start-Sleep -Seconds 3
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -like '.NET Runtime*' -and ($_.Message -match [regex]::Escape($clsid.Trim('{}')) -or $_.Message -match 'probe-profiler') })
    $evText = @($events | ForEach-Object { $m = $_.Message -replace '\s+', ' '; "id=$($_.Id) " + $m.Substring(0, [Math]::Min(400, $m.Length)) })
    $loadLibraryReached = [bool]($events | Where-Object { $_.Message -match '0x8007007f|0x80040111|DllGetClassObject' })
    $opens = @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4656, 4663; StartTime = $start } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'probe-profiler\.dll' -and $_.Message -match [regex]::Escape($helper) })
    $openText = @($opens | ForEach-Object { $m = $_.Message -replace '\s+', ' '; $a = if ($m -match 'Accesses:(.*?)(Access Mask|Access Reasons|$)') { $Matches[1].Trim() } else { '?' }; "id=$($_.Id) accesses=$a" } | Select-Object -Unique)
    $inModules = [bool]($rows | Where-Object ProbeInModuleList)
    $status = if ($loadLibraryReached -or $inModules -or $opens.Count) { 'FAIL' } else { 'PASS' }
    $result = [pscustomobject]@{ Label = $Label; Target = $helper; TargetVersion = $ver; Status = $status; Runs = $rows; ProfilerEvents = $evText; TargetOpenedProbeDll = $openText
        Interpretation = 'FAIL = the target opened/loaded a DLL from a user-writable path named only by environment variables (DllMain runs in that process).' }
    $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory "profiler-$Label.json") -Encoding UTF8
    "RESULT $status target=$(Split-Path -Leaf $helper) $ver"
    $rows | ForEach-Object { "run $($_.Variant): exit $($_.Exit), probe in module list: $($_.ProbeInModuleList)" }
    $evText; $openText
}
finally {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $helper -and $_.StartTime -ge $start } catch { $false } } | Stop-Process -Force
    if ($prevAudit -notmatch 'Success') { auditpol /set /subcategory:$fsAudit /success:disable | Out-Null }
    $now = ((auditpol /get /subcategory:$fsAudit /r | ConvertFrom-Csv) | Select-Object -First 1).'Inclusion Setting'
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    "cleanup: probe folder removed = $(-not (Test-Path $dir)); file-system audit policy before='$prevAudit' after='$now'"
}
