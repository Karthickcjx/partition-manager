<#
.SYNOPSIS
  Measures process startup of the app (OS caches are not cleared): process start → main window, and → first disk shown on the disk map.
  Uses UI Automation polling only (no input). Also reads the app's own discovery timing from its log.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\PartitionManager.exe'),
    [int]$Runs = 5
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$rows = @()
for ($i = 1; $i -le $Runs; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process (Resolve-Path $Exe) -ArgumentList '--light' -PassThru
    $window = $null; $disk = $null
    while ($sw.Elapsed.TotalSeconds -lt 30 -and -not $disk) {
        $p.Refresh()
        if (-not $window -and $p.MainWindowHandle -ne 0) { $window = $sw.ElapsedMilliseconds }
        if ($window) {
            $root = $A::FromHandle($p.MainWindowHandle)
            $cond = New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
            if ($root.FindAll($TS::Descendants, $cond) | Where-Object { $_.Current.Name -like '*(C:)*' }) { $disk = $sw.ElapsedMilliseconds }
        }
        Start-Sleep -Milliseconds 25
    }
    $mem = [math]::Round($p.WorkingSet64 / 1MB, 0)
    $p.CloseMainWindow() | Out-Null; Start-Sleep 1; if (-not $p.HasExited) { $p.Kill() }
    $rows += [pscustomobject]@{ Run = $i; WindowMs = $window; DiskMapMs = $disk; WorkingSetMB = $mem }
    Start-Sleep 1
}
$rows | Format-Table -AutoSize
$log = Get-ChildItem "$env:LOCALAPPDATA\PartitionManager\Logs\*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
"Discovery timings from log (last $Runs):"
Select-String -Path $log.FullName -Pattern 'partition\(s\) in (\d+) ms' | Select-Object -Last $Runs | ForEach-Object { '  ' + $_.Matches[0].Groups[1].Value + ' ms' }
