<#
.SYNOPSIS
  Reproducible release build: tests, self-contained single-file publish of the app and the helper, installer
  (Inno Setup 6, if installed), NuGet vulnerability audit, dependency SBOM and SHA-256 checksums.
.OUTPUTS
  artifacts\publish\PartitionManager.exe, PartitionManager.Helper.exe
  artifacts\installer\PartitionManager-<version>-x64-setup.exe
  artifacts\checksums\SHA256SUMS.txt
  artifacts\reports\sbom.json, vulnerabilities.txt, test-results.trx
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$userDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $userDotnet 'sdk')) { $env:PATH = "$userDotnet;$env:PATH"; $env:DOTNET_ROOT = $userDotnet }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# Deterministic, path-independent binaries.
$common = @('-c', 'Release', '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true')

Push-Location $root
try {
    foreach ($d in 'artifacts\publish', 'artifacts\checksums', 'artifacts\reports') { New-Item -ItemType Directory -Force $d | Out-Null }

    dotnet test PartitionManager.sln @common --filter 'Category!=VirtualDisk&Category!=RealVolumeRecovery' --logger "trx;LogFileName=$root\artifacts\reports\test-results.trx"
    if ($LASTEXITCODE) { throw 'tests failed' }

    $publish = @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                 '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', 'artifacts\publish')
    dotnet publish src\Desktop\PartitionManager.Desktop.csproj @common @publish
    if ($LASTEXITCODE) { throw 'app publish failed' }
    # The elevated helper must not self-extract native libraries into a user-writable temp directory.
    $helperPublish = $publish | ForEach-Object { if ($_ -eq '-p:IncludeNativeLibrariesForSelfExtract=true') { '-p:IncludeNativeLibrariesForSelfExtract=false' } else { $_ } }
    dotnet publish src\PrivilegedHelper\PartitionManager.PrivilegedHelper.csproj @common @helperPublish
    if ($LASTEXITCODE) { throw 'helper publish failed' }

    dotnet list PartitionManager.sln package --vulnerable --include-transitive 2>&1 | Out-File artifacts\reports\vulnerabilities.txt -Encoding utf8
    if ($LASTEXITCODE) { throw 'vulnerability audit failed' }
    dotnet list PartitionManager.sln package --include-transitive --format json | Out-File artifacts\reports\sbom.json -Encoding utf8
    if ($LASTEXITCODE) { throw 'dependency inventory failed' }
    & (Join-Path $PSScriptRoot 'write-sbom.ps1')
    Copy-Item docs\reports\*.md artifacts\reports -Force

    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc /Q installer\PartitionManager.iss
        if ($LASTEXITCODE) { throw 'installer build failed' }
    } else {
        Write-Warning 'Inno Setup 6 not found; skipping installer. Install it from https://jrsoftware.org/isinfo.php'
    }

    $files = Get-Item artifacts\publish\PartitionManager.exe, artifacts\publish\PartitionManager.Helper.exe, artifacts\installer\PartitionManager-0.5.1-preview-x64-setup.exe -ErrorAction SilentlyContinue
    $files | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
        Set-Content artifacts\checksums\SHA256SUMS.txt -Encoding ascii
    $files | ForEach-Object {
        $sig = Get-AuthenticodeSignature $_.FullName
        [pscustomobject]@{ File = $_.Name; MB = [math]::Round($_.Length / 1MB, 1); Signature = $sig.Status }
    } | Format-Table -AutoSize
}
finally { Pop-Location }
