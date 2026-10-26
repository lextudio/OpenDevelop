[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$NoticeDirectory
)

$ErrorActionPreference = 'Stop'
$packages = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$extractedLicenses = [ordered]@{}
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
        if ($license -and $license.type -eq 'file') {
            $reference = "LicenseRef-$($id -replace '[^A-Za-z0-9.-]', '-')-$($version -replace '[^A-Za-z0-9.-]', '-')"
            $licensePath = Join-Path $directory $license.InnerText.Trim()
            if (Test-Path -LiteralPath $licensePath -PathType Leaf) {
                $extractedLicenses[$reference] = [ordered]@{
                    licenseId = $reference
                    name = "$id $version package license"
                    extractedText = Get-Content -LiteralPath $licensePath -Raw
                }
            }
            return $reference
        }
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
if ($extractedLicenses.Count -gt 0) { $document.hasExtractedLicensingInfos = @($extractedLicenses.Values) }

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Wrote SPDX package manifest with $($spdxPackages.Count) packages: $OutputPath"

if ($NoticeDirectory) {
    New-Item -ItemType Directory -Force -Path $NoticeDirectory | Out-Null
    $catalog = [Collections.Generic.List[string]]::new()
    $declaredLicenses = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $catalog.Add('# NuGet package notices')
    $catalog.Add('')
    $catalog.Add('| Package | Declared license | Copyright |')
    $catalog.Add('| --- | --- | --- |')
    foreach ($entry in $packages | Sort-Object) {
        $id, $version = $entry -split '/', 2
        $directory = Join-Path (Join-Path $PackageRoot $id.ToLowerInvariant()) $version.ToLowerInvariant()
        $license = Get-LicenseExpression $id $version
        [void]$declaredLicenses.Add($license)
        $copyright = ''
        $nuspec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($nuspec) {
            [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
            $copyright = [string]$xml.SelectSingleNode("//*[local-name()='copyright']")?.InnerText
            if ([string]::IsNullOrWhiteSpace($copyright)) { $copyright = [string]$xml.SelectSingleNode("//*[local-name()='authors']")?.InnerText }
        }
        $catalog.Add("| $id $version | $license | $($copyright -replace '\|', '\\|') |")
        $destination = Join-Path (Join-Path $NoticeDirectory $id) $version
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\.|$)' }) {
            $relative = $file.FullName.Substring($directory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $target = Join-Path $destination $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
    # SPDX expressions identify standard terms, but a distributed native binary must carry the
    # corresponding text as well. The package graph currently contains a Zlib-only package.
    if ($declaredLicenses.Contains('Zlib')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../legal/Zlib.txt') -Destination (Join-Path $NoticeDirectory 'SPDX-Zlib.txt') -Force
    }
    $catalog | Set-Content -LiteralPath (Join-Path $NoticeDirectory 'NOTICE.md') -Encoding utf8
}
