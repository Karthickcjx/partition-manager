<#
.SYNOPSIS
  Runs a local PowerShell script on one explicitly identified EC2 instance through SSM (AWS-RunPowerShellScript),
  waits for it, and stores the command ID and output under artifacts\cloud-test-control. Never sends credentials.
#>
param(
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [Parameter(Mandatory = $true)][string]$Arguments,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Region = 'ap-south-1',
    [int]$TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'
if ($InstanceId -notmatch '^i-[0-9a-f]{17}$') { throw 'Invalid instance ID.' }
$evidence = Join-Path $PSScriptRoot '..\artifacts\cloud-test-control'
$body = Get-Content -Raw -LiteralPath $ScriptPath
$command = "& {`n$body`n} $Arguments"
$input = @{
    DocumentName = 'AWS-RunPowerShellScript'
    InstanceIds = @($InstanceId)
    Comment = "PartitionManager $Name"
    Parameters = @{ commands = @($command); executionTimeout = @([string]$TimeoutSeconds) }
}
$tmp = Join-Path $env:TEMP "pm-ssm-$([guid]::NewGuid().ToString('N')).json"
$input | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $tmp -Encoding ascii
try { $id = aws ssm send-command --region $Region --cli-input-json "file://$tmp" --query 'Command.CommandId' --output text }
finally { Remove-Item -LiteralPath $tmp -Force }
if (-not $id) { throw 'send-command failed' }
@{ Name = $Name; CommandId = $id; InstanceId = $InstanceId; Sent = (Get-Date).ToString('o'); Script = (Split-Path -Leaf $ScriptPath) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "$Name-command.json") -Encoding utf8
$deadline = (Get-Date).AddSeconds($TimeoutSeconds + 120)
do {
    Start-Sleep -Seconds 10
    $status = aws ssm get-command-invocation --region $Region --command-id $id --instance-id $InstanceId --query Status --output text 2>$null
} while ($status -in 'Pending', 'InProgress', 'Delayed', '' -and (Get-Date) -lt $deadline)
$result = aws ssm get-command-invocation --region $Region --command-id $id --instance-id $InstanceId --output json | ConvertFrom-Json
[pscustomobject]@{ Name = $Name; CommandId = $id; Status = $result.Status; ResponseCode = $result.ResponseCode; Stdout = $result.StandardOutputContent; Stderr = $result.StandardErrorContent } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "$Name-result.json") -Encoding utf8
"STATUS $($result.Status) (code $($result.ResponseCode)) command $id"
$result.StandardOutputContent
if ($result.StandardErrorContent) { "STDERR: " + $result.StandardErrorContent.Substring(0, [Math]::Min(2000, $result.StandardErrorContent.Length)) }
