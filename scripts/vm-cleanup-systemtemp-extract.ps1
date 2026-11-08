<# Disposable EC2 VM only. Removes the WPF native-library extraction folder that the profiler positive control created
   when it ran the installed PartitionManager.exe as SYSTEM. Removes it only if every file is one of the known WPF
   *_cor3.dll files and the folder is under SystemTemp\.net\PartitionManager. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$ErrorActionPreference = 'Stop'
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
$root = 'C:\Windows\SystemTemp\.net'
$known = 'D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll'
Get-ChildItem -LiteralPath $root -Recurse -Force | ForEach-Object { "{0} | {1} | {2}" -f $_.FullName, $_.CreationTime.ToString('o'), $(if ($_.PSIsContainer) { 'dir' } else { $_.Length }) }
$top = @(Get-ChildItem -LiteralPath $root -Force)
$files = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)
$unexpected = @($files | Where-Object { $_.Name -notin $known })
if ($top.Count -eq 1 -and $top[0].Name -eq 'PartitionManager' -and $files.Count -gt 0 -and $unexpected.Count -eq 0) {
    Remove-Item -LiteralPath (Join-Path $root 'PartitionManager') -Recurse -Force
    if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { Remove-Item -LiteralPath $root -Force }
    "REMOVED test-created extraction folder; exists now: $(Test-Path $root)"
} else { "NOT REMOVED: unexpected layout (top=$($top.Name -join ','), unexpected files=$($unexpected.Name -join ','))" }
