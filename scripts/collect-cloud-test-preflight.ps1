<# Read-only cloud test preflight. Writes evidence files only beside this script.
   Does not install software, change policy, or mutate disks. #>
$ErrorActionPreference='Stop'
$folder=Join-Path $PSScriptRoot 'Evidence'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$installer=Join-Path $PSScriptRoot 'PartitionManager-0.5.0-preview-x64-setup.exe'
$expected=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer.sha256') -Raw).Trim()
$actual=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if($actual -ne $expected) { throw 'Installer hash mismatch. Do not run it.' }
$report=[ordered]@{
    CapturedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    InstallerHashVerified=$true
    InstallerSHA256=$actual
    InstallerSignature=(Get-AuthenticodeSignature -LiteralPath $installer).Status.ToString()
    Computer=Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,TotalPhysicalMemory,HypervisorPresent
    OS=Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture
    Administrator=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Disks=@(Get-Disk | Select-Object Number,UniqueId,SerialNumber,FriendlyName,BusType,Size,PartitionStyle,IsBoot,IsSystem)
    Partitions=@(Get-Partition | Select-Object DiskNumber,PartitionNumber,Guid,DriveLetter,Offset,Size,IsBoot,IsSystem)
    Volumes=@(Get-Volume | Select-Object DriveLetter,FileSystem,Size,SizeRemaining,HealthStatus)
    ExistingInstall=Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'PartitionManager')
    PhysicalWritesEnabled=$false
}
$output=Join-Path $folder 'cloud-preflight.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output "Preflight complete. Return this file for review: $output"
Write-Output 'No installation or disk mutation performed. Installer hash matches the supplied package manifest.'
