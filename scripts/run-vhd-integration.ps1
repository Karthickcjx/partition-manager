<#
.SYNOPSIS
  Runs the real virtual-disk integration test (Category=VirtualDisk) elevated. Disposable VHDX images are created,
  attached, used, detached and deleted inside -ImageRoot only. The system helper policy (HKLM) is NOT changed: the
  test drives the write engine in-process with its own fixed virtual-disk-only policy.
  Call unelevated; it asks for one UAC approval and waits for the elevated run to finish.
#>
param([string]$ImageRoot = 'D:\PartitionManager-TestDisks', [string]$RecoverySource = '', [string]$RecoveryOutput = '',
      [switch]$IncludeRecovery, [switch]$Elevated, [string]$Filter = 'Category=VirtualDisk')
# -IncludeRecovery also runs the real-volume recovery test, which CREATES AND DELETES FILES on -RecoverySource. Off by default.
$ErrorActionPreference = 'Stop'
if ($IncludeRecovery) { throw 'Legacy real-volume recovery is excluded from Milestone 3. Use disposable VHDX tests only.' }
if ([IO.Path]::GetFullPath($ImageRoot).TrimEnd('\') -ne 'D:\PartitionManager-TestDisks') { throw 'Only D:\PartitionManager-TestDisks is approved.' }
for ($parent = [IO.DirectoryInfo]::new($ImageRoot); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Test directory has a reparse ancestor.' }
}
if ([IO.DriveInfo]::new('D').AvailableFreeSpace -lt 8GB) { throw 'At least 8 GiB free space is required.' }
Write-Output 'Preflight: D:\PartitionManager-TestDisks only; unique dynamic VHDX files, 64 MiB to 16 GiB plus a sparse layout replica; 8 GiB free-space reserve.'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$log = Join-Path $root 'artifacts\reports\vhd-run.log'

if (-not $Elevated) {
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-ImageRoot', "`"$ImageRoot`"",
                   '-Elevated', '-Filter', "`"$Filter`"")
    if ($IncludeRecovery) { $childArgs += '-IncludeRecovery' }
    $p = Start-Process powershell -Verb RunAs -PassThru -WindowStyle Hidden -ArgumentList $childArgs
    # Wait for the runner itself; -Wait also waits for reusable MSBuild/compiler descendants.
    $p.WaitForExit()
    Get-Content $log -ErrorAction SilentlyContinue
    exit $p.ExitCode
}

Start-Transcript -Path $log -Force | Out-Null
try {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet'
    if (Test-Path (Join-Path $userDotnet 'sdk')) { $env:PATH = "$userDotnet;$env:PATH"; $env:DOTNET_ROOT = $userDotnet }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:PM_VHD_TESTS = '1'
    $env:PM_VHD_TEST_DIR = $ImageRoot
    if ($IncludeRecovery) { $env:PM_RECOVERY_TESTS = '1' } else { Remove-Item Env:PM_RECOVERY_TESTS -ErrorAction SilentlyContinue }
    $env:PM_RECOVERY_SOURCE = $RecoverySource
    $env:PM_RECOVERY_OUTPUT = $RecoveryOutput
    $env:PM_VHD_REPORT_DIR = Join-Path $root 'artifacts\reports'
    $existed = Test-Path $ImageRoot
    Set-Location $root
    dotnet test src\Tests\PartitionManager.Tests.csproj --filter "Category=VirtualDisk&($Filter)" --logger "console;verbosity=normal" --logger "trx;LogFileName=$root\artifacts\reports\vhd-run.trx" -- xUnit.ParallelizeTestCollections=false
    $code = $LASTEXITCODE
    # Remove the folder only if this run created it and it is now empty.
    if (-not $existed -and (Test-Path $ImageRoot) -and -not (Get-ChildItem $ImageRoot -Force)) { Remove-Item $ImageRoot; "Removed empty $ImageRoot" }
    exit $code
}
finally { Stop-Transcript | Out-Null }
