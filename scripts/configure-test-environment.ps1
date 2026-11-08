<#
.SYNOPSIS
  Enables (or disables) real writes for disposable virtual disks ONLY. Must be run as administrator.
.DESCRIPTION
  Writes HKLM\SOFTWARE\PartitionManager\Helper:
    WriteMode                = VirtualDisksOnly | Disabled
    AllowedImageRoots        = the folder that will hold throwaway VHDX files
  Physical disks cannot be enabled by any setting. The helper requires a protected installation.
  The obsolete -DevBuild argument is retained only to fail with an explanatory error.
.EXAMPLE
  .\configure-test-environment.ps1 -ImageRoot D:\PartitionManager-TestDisks
  .\configure-test-environment.ps1 -Disable
#>
param(
    [string]$ImageRoot,
    [switch]$DevBuild,
    [switch]$Disable
)
$ErrorActionPreference = 'Stop'
if ($DevBuild) { throw 'User-writable helper installation overrides were removed. Use a protected installation in an isolated VM.' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated PowerShell.' }

$key = 'HKLM:\SOFTWARE\PartitionManager\Helper'
if ($Disable) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force }
    'Real writes disabled (policy key removed).'
    return
}
if (-not $ImageRoot) { throw 'Specify -ImageRoot <folder for throwaway VHDX files>, or -Disable.' }
$full = [IO.Path]::GetFullPath($ImageRoot)
if ($full.Length -le 3) { throw 'Use a dedicated folder, not a drive root.' }
if ($full.TrimEnd('\') -ne 'D:\PartitionManager-TestDisks') { throw 'Milestone 3 permits only D:\PartitionManager-TestDisks.' }
for ($ancestor = [IO.DirectoryInfo]::new($full); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Image root has a reparse ancestor.' }
}
New-Item -ItemType Directory -Force $full | Out-Null
New-Item -Force $key | Out-Null
Set-ItemProperty $key -Name WriteMode -Value 'VirtualDisksOnly' -Type String
Set-ItemProperty $key -Name AllowedImageRoots -Value @($full) -Type MultiString
Remove-ItemProperty $key -Name AllowUnprotectedLocation -ErrorAction SilentlyContinue
"Real writes enabled for VHD/VHDX files under $full only."
"Run scripts\run-vhd-integration.ps1 for the disposable test suite."
