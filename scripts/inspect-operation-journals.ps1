<# Read-only journal summary. Never clears records, retries commands, or authorizes writes.
   The helper's C# reader performs full sequence/hash-chain validation before new operations. #>
param([string]$Directory = "$env:ProgramData\PartitionManager\Audit")
$ErrorActionPreference='Stop'
Get-ChildItem -LiteralPath $Directory -Filter 'operation-*.jsonl' | ForEach-Object {
    $file=$_; $events=@(); $problem=''
    try { $events=@(Get-Content -LiteralPath $file.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Event }) }
    catch { $problem=$_.Exception.Message }
    $last=$events | Select-Object -Last 1
    [pscustomobject]@{File=$file.FullName;OperationId=$last.OperationId;LastRecordedPhase=$last.Phase;
        RequiresReview=($problem -ne '' -or $last.Phase -ne 'Verified');Problem=$problem;
        Recovery='Read-only summary, not integrity certification. Compare actual layout and file hashes; never automatically retry or delete the journal.'}
}
