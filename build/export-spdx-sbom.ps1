[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$packages = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($assetPath in $AssetsFile) {
    $assets = Get-Content -LiteralPath $assetPath -Raw | ConvertFrom-Json
    foreach ($target in $assets.targets.PSObject.Properties.Value) {
        foreach ($library in $target.PSObject.Properties) {
            if ($library.Value.type -eq 'package') { [void]$packages.Add($library.Name) }
        }
    }
}

function Get-LicenseExpression([string]$id, [string]$version) {
    $directory = Join-Path (Join-Path $PackageRoot $id.ToLowerInvariant()) $version.ToLowerInvariant()
    $nuspec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nuspec) { return 'NOASSERTION' }
    try {
        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
        $license = $xml.SelectSingleNode("//*[local-name()='license']")
        if ($license -and $license.type -eq 'expression' -and $license.InnerText) { return $license.InnerText.Trim() }
        if ($license -and $license.type -eq 'file') { return "LicenseRef-$($id -replace '[^A-Za-z0-9.-]', '-')" }
    }
    catch { }
    return 'NOASSERTION'
}

$spdxPackages = foreach ($entry in $packages | Sort-Object) {
    $id, $version = $entry -split '/', 2
    [ordered]@{
        SPDXID = "SPDXRef-Package-$($id -replace '[^A-Za-z0-9.-]', '-')-$($version -replace '[^A-Za-z0-9.-]', '-')"
        name = $id
        versionInfo = $version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = Get-LicenseExpression $id $version
        copyrightText = 'NOASSERTION'
        externalRefs = @(@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = "pkg:nuget/$id@$version" })
    }
}

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = 'OpenDevelop third-party package manifest'
    documentNamespace = "https://github.com/lextudio/OpenDevelop/sbom/$([Guid]::NewGuid())"
    creationInfo = @{ created = [DateTime]::UtcNow.ToString('o'); creators = @('Tool: OpenDevelop build/export-spdx-sbom.ps1') }
    packages = @($spdxPackages)
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Wrote SPDX package manifest with $($spdxPackages.Count) packages: $OutputPath"
