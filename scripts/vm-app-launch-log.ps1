<# Read-only: for each user profile, the recent "start; elevated=..." lines from the app log, plus signed-in sessions and UAC settings. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
Get-ChildItem 'C:\Users\*\AppData\Local\PartitionManager' -Recurse -File -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object {
    $user = $_.FullName.Split('\')[2]
    Select-String -LiteralPath $_.FullName -Pattern 'start; elevated' | Select-Object -Last 4 | ForEach-Object { "$user | $($_.Line.Trim())" }
}
"log files:"; Get-ChildItem 'C:\Users' -Directory | ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'AppData\Local\PartitionManager') -Recurse -File -ErrorAction SilentlyContinue } | Sort-Object LastWriteTime | Select-Object -Last 6 | ForEach-Object { "  $($_.FullName) $($_.Length) $($_.LastWriteTime.ToString('o'))"; Get-Content -LiteralPath $_.FullName -Tail 400 | Where-Object { $_ -match 'start;' } | Select-Object -Last 3 | ForEach-Object { "     $_" } }
"sessions:"; (quser 2>&1) | ForEach-Object { "  $_" }
$p = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
"UAC: EnableLUA=$($p.EnableLUA) ConsentPromptBehaviorAdmin=$($p.ConsentPromptBehaviorAdmin) FilterAdministratorToken=$($p.FilterAdministratorToken)"
