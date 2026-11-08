<# Read-only: what a running relocation validation is doing right now. #>
param([Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId)
$t = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
if ((Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $t } -TimeoutSec 10) -ne $ExpectedEc2InstanceId) { throw 'identity mismatch' }
"now: $((Get-Date).ToUniversalTime().ToString('o'))"
Get-Process RelocationProbe, diskpart -ErrorAction SilentlyContinue | ForEach-Object { "process $($_.Name) pid $($_.Id) started $($_.StartTime.ToUniversalTime().ToString('o')) cpu $([math]::Round($_.CPU,1))s" }
Get-DiskImage -ImagePath (Get-ChildItem 'C:\PMCloudTest-20261107\Disks\reloc\*.vhdx' -ErrorAction SilentlyContinue).FullName -ErrorAction SilentlyContinue | ForEach-Object { "image $($_.ImagePath) attached=$($_.Attached) disk=$($_.Number)" }
Get-ChildItem 'C:\PMCloudTest-20261107\Reloc' -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 6 | ForEach-Object { "file $($_.FullName) $($_.Length) $($_.LastWriteTime.ToUniversalTime().ToString('o'))" }
$j = Get-ChildItem 'C:\PMCloudTest-20261107\Reloc\journal-*\operation-*.jsonl' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($j) { $ph = @(Get-Content $j.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Event.Phase }); "journal $($j.Name): $($ph.Count) records, last=$($ph[-1]), chunks=$(@($ph | Where-Object { $_ -eq 'ChunkCopied' }).Count)" }
