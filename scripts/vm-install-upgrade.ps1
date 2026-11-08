<# Upgrade test on an explicitly identified disposable EC2 VM (run as SYSTEM via SSM). Downloads the installer from a
   short-lived URL (never logged), verifies its SHA-256 before running it, upgrades silently, and records the
   installed versions/hashes and the data-folder ACL. The URL is passed only as a parameter and not written anywhere. #>
param(
    [Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId,
    [switch]$DisposableVmConfirmed,
    [Parameter(Mandatory = $true)][string]$InstallerUrl,
    [Parameter(Mandatory = $true)][string]$ExpectedInstallerSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedAppSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedHelperSha256,
    [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\Upgrade'
)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$install = Join-Path $env:ProgramFiles 'PartitionManager'
$rows = [Collections.Generic.List[object]]::new()
function Record($check, $status, $detail) { $rows.Add([pscustomobject]@{ Check = $check; Status = $status; Detail = $detail }) }
function Installed { Get-ChildItem -LiteralPath $install -Filter *.exe | Where-Object Name -like 'PartitionManager*' | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Version = $_.VersionInfo.ProductVersion; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } }

$before = Installed
Record 'Installed before' $(if ($before) { 'INFO' } else { 'FAIL' }) (($before | ForEach-Object { "$($_.Name) $($_.Version)" }) -join '; ')
$setup = Join-Path $EvidenceDirectory 'PartitionManager-0.5.1-preview-x64-setup.exe'
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $InstallerUrl -OutFile $setup -UseBasicParsing
$InstallerUrl = $null
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
if ($hash -ne $ExpectedInstallerSha256.ToUpperInvariant()) { Remove-Item -LiteralPath $setup -Force; throw "Installer hash mismatch: $hash" }
Record 'Installer transfer integrity' 'PASS' $hash
$log = Join-Path $EvidenceDirectory 'upgrade-0.5.1.log'
$p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -WindowStyle Hidden -PassThru -Wait
Record 'Upgrade 0.5.0-preview -> 0.5.1-preview' $(if ($p.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) "exit $($p.ExitCode)"
$after = Installed
foreach ($e in @(@{ N = 'PartitionManager.exe'; H = $ExpectedAppSha256 }, @{ N = 'PartitionManager.Helper.exe'; H = $ExpectedHelperSha256 })) {
    $i = $after | Where-Object Name -eq $e.N
    Record "Installed hash $($e.N)" $(if ($i.Sha256 -eq $e.H.ToUpperInvariant()) { 'PASS' } else { 'FAIL' }) "$($i.Version) $($i.Sha256)"
}
$data = Join-Path $env:ProgramData 'PartitionManager'
if (Test-Path -LiteralPath $data) { $a = Get-Acl -LiteralPath $data; Record 'Data folder secured by installer' $(if ($a.Sddl -match 'D:P' -and $a.Owner -match 'Administrators') { 'PASS' } else { 'FAIL' }) "$($a.Owner) $($a.Sddl)" }
else { Record 'Data folder secured by installer' 'FAIL' 'absent' }
$sec = Select-String -LiteralPath $log -Pattern 'icacls' | ForEach-Object { $_.Line.Trim() }
Record 'Installer ACL steps (log)' 'INFO' ($sec -join ' || ')
$rows | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'upgrade-result.json') -Encoding UTF8
$rows | ForEach-Object { "{0} | {1} | {2}" -f $_.Status, $_.Check, $_.Detail }
