<#
.SYNOPSIS
  End-to-end UI smoke test for the desktop app, driven through UI Automation.
.DESCRIPTION
  Launches PartitionManager.exe, then selects and invokes controls by their accessible names (no mouse, no keystrokes,
  no focus stealing) and captures each window with PrintWindow, so it can run while you use the PC.
  Everything happens in simulation mode: the app cannot write to disks.

  Exit code 0 = all checks passed. Screenshots go to -OutDir.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\ui-smoke.ps1
  powershell -ExecutionPolicy Bypass -File scripts\ui-smoke.ps1 -Demo
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\Desktop\bin\Debug\net8.0-windows\win-x64\PartitionManager.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\docs\screenshots'),
    [switch]$Demo
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SmokeNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[SmokeNative]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force $OutDir | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$results = [System.Collections.Generic.List[object]]::new()

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Check = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function Capture($element, [string]$file) {
    $h = [IntPtr]$element.Current.NativeWindowHandle
    $r = New-Object SmokeNative+RECT
    [SmokeNative]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [SmokeNative]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT: works for occluded DirectX windows
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save((Join-Path $OutDir $file), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

function WaitFor([scriptblock]$probe, [int]$timeoutMs = 10000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $v = & $probe
        if ($v) { return $v }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function TopWindow([string]$title) {
    WaitFor {
        $cond = New-Object System.Windows.Automation.PropertyCondition ($A::ProcessIdProperty), $script:proc.Id
        $tops = @($A::RootElement.FindAll($TS::Children, $cond))
        # Owned windows (our dialogs) are exposed as children of their owner window.
        $winCond = New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
        $owned = @($tops | ForEach-Object { $_.FindAll($TS::Children, $winCond) })
        ($owned + $tops) | Where-Object { $_.Current.Name -like $title } | Select-Object -First 1
    }
}

function Find($root, [string]$name, $type = $null) {
    WaitFor {
        $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $all | Where-Object { $_.Current.Name -like $name -and ($null -eq $type -or $_.Current.ControlType -eq $type) } | Select-Object -First 1
    } 5000
}

# Every action is scoped to the process this script launched. Elements from any other process are refused, so the
# script can never act on an unrelated window even if titles collide.
function AssertOurs($el, [string]$what) {
    if ($el.Current.ProcessId -ne $script:proc.Id) { throw "refusing to act on '$what': it belongs to process $($el.Current.ProcessId), not $($script:proc.Id)" }
}

function Invoke($root, [string]$name) {
    $el = Find $root $name ([System.Windows.Automation.ControlType]::Button)
    if (-not $el) { $el = Find $root $name }
    if (-not $el) { throw "control '$name' not found" }
    AssertOurs $el $name
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
}

function SelectItem($root, [string]$name) {
    $el = Find $root $name ([System.Windows.Automation.ControlType]::ListItem)
    if (-not $el) { $el = Find $root $name ([System.Windows.Automation.ControlType]::DataItem) }
    if (-not $el) { throw "item '$name' not found" }
    AssertOurs $el $name
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

function SetText($root, [string]$name, [string]$value) {
    $el = Find $root $name ([System.Windows.Automation.ControlType]::Edit)
    if (-not $el) { throw "edit '$name' not found" }
    AssertOurs $el $name
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}

# Framework-dependent dev builds need the .NET 8 Desktop Runtime; use a user-local SDK install if that's where it lives.
$userDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $userDotnet 'shared\Microsoft.WindowsDesktop.App'))) { $env:DOTNET_ROOT = $userDotnet }

$args = @('--light') + $(if ($Demo) { @('--demo') } else { @() })
$script:proc = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList $args -PassThru
try {
    $main = TopWindow 'Partition Manager'
    Check 'Main window opens' ($null -ne $main)
    $first = Find $main '*(C:)*' ([System.Windows.Automation.ControlType]::ListItem)
    Check 'Disk discovery shows the boot volume' ($null -ne $first)
    Start-Sleep -Milliseconds 600
    Capture $main '01-partition-management-light.png'

    # Safety: deleting C: must be refused.
    SelectItem $main '*(C:)*'
    Invoke $main 'Delete'
    $msg = TopWindow 'Delete partition'
    Check 'Deleting C: is blocked by the safety policy' ($null -ne $msg)
    if ($msg) { Capture $msg '02-safety-block.png'; Invoke $msg 'OK' }

    # Shrink D: to 200 GB.
    SelectItem $main '*(D:)*'
    Invoke $main 'Resize/Move'
    $dlg = TopWindow 'Resize/Move partition'
    Check 'Resize dialog opens' ($null -ne $dlg)
    SetText $dlg 'Partition size' '204800'
    Start-Sleep -Milliseconds 300
    Capture $dlg '03-resize-dialog.png'
    Invoke $dlg 'Add to task list'
    Check 'Shrink task queued' ($null -ne (Find $main '*Shrink*(D:)*'))
    $free = Find $main 'Unallocated*' ([System.Windows.Automation.ControlType]::ListItem)
    Check 'Freed space appears as unallocated' ($null -ne $free)

    # Create a partition in the freed space.
    SelectItem $main 'Unallocated*'
    Invoke $main 'Create'
    $dlg = TopWindow 'Create partition'
    Check 'Create dialog opens' ($null -ne $dlg)
    Capture $dlg '04-create-dialog.png'
    Invoke $dlg 'Add to task list'
    Check 'Create task queued' ($null -ne (Find $main 'Create*NTFS partition*'))
    Start-Sleep -Milliseconds 500
    Capture $main '05-pending-tasks.png'

    # Execute (simulation).
    Invoke $main 'Execute'
    $confirm = TopWindow 'Execute 2 task(s)'
    Check 'Execute asks for confirmation with target disk identity' ($null -ne $confirm -and $null -ne (Find $confirm 'Target: Disk*'))
    Check 'Physical-disk tasks are routed to simulation' ($null -ne $confirm -and $null -ne (Find $confirm 'Simulation:*'))
    if ($confirm) { Capture $confirm '06-execute-confirm.png'; Invoke $confirm 'Run simulation' }
    $progress = TopWindow 'Executing (simulation)'
    Check 'Progress dialog shows' ($null -ne $progress)
    if ($progress) { Start-Sleep -Milliseconds 1500; Capture $progress '07-progress.png' }
    $done = TopWindow 'Done'
    Check 'Simulation completes' ($null -ne $done)
    if ($done) { Capture $done '08-done.png'; Invoke $done 'OK' }
    Check 'Simulated-result banner is shown' ($null -ne (Find $main 'Showing the result of a simulation*'))
    Start-Sleep -Milliseconds 500
    Capture $main '09-simulated-result.png'

    # Theme and other sections.
    Invoke $main 'Switch theme'
    Start-Sleep -Milliseconds 600
    Capture $main '10-partition-management-dark.png'
    SelectItem $main 'Disk Information'
    Check 'Disk Information page loads' ($null -ne (Find $main 'Serial number'))
    Start-Sleep -Milliseconds 600
    Capture $main '11-disk-information-dark.png'
    Invoke $main 'Switch theme'
    SelectItem $main 'Clone Disk'
    Start-Sleep -Milliseconds 600
    Capture $main '12-clone-disk-planned.png'

    SelectItem $main 'Data Recovery'
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $scanButton = Find $main 'Scan' ([System.Windows.Automation.ControlType]::Button)
    Check 'Data Recovery page loads with a Scan action' ($null -ne $scanButton)
    if (-not $isAdmin) { Check 'Data Recovery offers elevation when not administrator' ($null -ne (Find $main 'Restart as administrator')) }
    Start-Sleep -Milliseconds 800
    Capture $main '13-data-recovery.png'
}
catch {
    Check 'Smoke run completed without exceptions' $false $_.Exception.Message
}
finally {
    if (-not $script:proc.HasExited) { $script:proc.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $script:proc.HasExited) { $script:proc.Kill() } }
}
$results | Format-Table -AutoSize | Out-String -Width 200
$results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'ui-smoke-results.json') -Encoding UTF8
if ($results | Where-Object Result -eq 'FAIL') { exit 1 }
