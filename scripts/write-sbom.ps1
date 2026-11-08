# CycloneDX inventory of resolved shipping dependencies, including runtime packs.
# License review and compiler/toolchain inventory remain separate release gates.
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\reports\bom.cdx.json'))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$components = @{}
foreach ($project in 'Desktop', 'PrivilegedHelper') {
    $assets = Get-Content -Raw -LiteralPath (Join-Path $repo "src\$project\obj\project.assets.json") | ConvertFrom-Json
    foreach ($entry in $assets.libraries.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        $index = $entry.Name.LastIndexOf('/')
        $name = $entry.Name.Substring(0,$index)
        $version = $entry.Name.Substring($index+1)
        $purl = "pkg:nuget/$name@$version"
        $component = @{ type='library'; name=$name; version=$version; purl=$purl; 'bom-ref'=$purl; scope='required' }
        if ($name -eq 'Microsoft.NET.ILLink.Tasks') { $component.scope='excluded'; $component.properties=@(@{name='partitionmanager:usage';value='build tool, not shipped'}) }
        if ($entry.Value.sha512) {
            $hex = -join ([Convert]::FromBase64String($entry.Value.sha512) | ForEach-Object { $_.ToString('x2') })
            $component.hashes = @(@{ alg='SHA-512'; content=$hex })
        }
        $components[$purl] = $component
    }
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($pack in $framework.Value.downloadDependencies) {
            $frameworkName = $pack.name -replace '\.Runtime\.win-x64$',''
            $references = @($framework.Value.frameworkReferences.PSObject.Properties.Name | ForEach-Object { $_ -replace '\.WPF$','' })
            if ($frameworkName -notin $references) { continue }
            $version = $pack.version.Trim('[',']','(',')').Split(',')[0].Trim()
            $purl = "pkg:nuget/$($pack.name)@$version"
            $components[$purl] = @{ type='framework'; name=$pack.name; version=$version; purl=$purl; 'bom-ref'=$purl; scope='required' }
        }
    }
}
$bom = @{ bomFormat='CycloneDX'; specVersion='1.5'; serialNumber="urn:uuid:$([guid]::NewGuid())"; version=1
    metadata=@{ timestamp=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); component=@{ type='application'; name='PartitionManager'; version='0.5.0-preview' }
        properties=@(@{name='partitionmanager:status';value='Unsigned engineering prerelease; release blocked'}) }
    components=@($components.GetEnumerator() | Sort-Object Key | ForEach-Object {$_.Value}) }
$bom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
