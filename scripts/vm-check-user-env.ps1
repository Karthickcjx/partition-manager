<# Read-only: reports whether DOTNET_*/COMPlus_*/CORECLR_*/COR_* names exist in the loaded HKCU\Environment of the
   manual-test accounts (names only, values never printed). #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
foreach ($n in 'pmrdp-std', 'pmrdp-adm') {
    $u = Get-LocalUser -Name $n -ErrorAction SilentlyContinue
    if (-not $u) { "$n : no account"; continue }
    $k = "Registry::HKEY_USERS\$($u.SID.Value)\Environment"
    if (-not (Test-Path $k)) { "$n : profile hive not loaded (not signed in)"; continue }
    $names = (Get-Item $k).GetValueNames() | Where-Object { $_ -match '^(DOTNET_|COMPLUS_|CORECLR_|COR_)' }
    "$n : runtime variables in HKCU\Environment = $(if ($names) { $names -join ', ' } else { 'none' })"
}
"helper processes now: $(@(Get-Process PartitionManager.Helper -ErrorAction SilentlyContinue).Count)"
"signed-in sessions:"; (quser 2>&1) | ForEach-Object { "  $_" }
$m = [Environment]::GetEnvironmentVariables('Machine').Keys | Where-Object { $_ -match '^(DOTNET_|COMPLUS_|CORECLR_|COR_)' }
"machine-level runtime variables = $(if ($m) { $m -join ', ' } else { 'none' })"
