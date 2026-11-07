$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('android-agent-spdx-' + [Guid]::NewGuid().ToString('N'))
try {
    $package = Join-Path $scratch 'fallback/example.agent/1.0.0'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    # NuGet's pack task strips every plain .nuspec from a package, so the metadata bundle ships
    # them as <id>.nuspec.xml; this test must exercise that shape, not the one that cannot exist.
    Set-Content -LiteralPath (Join-Path $package 'example.agent.nuspec.xml') -Value '<package><metadata><license type="file">LICENSE.txt</license></metadata></package>'
    Set-Content -LiteralPath (Join-Path $package 'LICENSE.txt') -Value 'Example license text'
    $assets = Join-Path $scratch 'project.assets.json'
    Set-Content -LiteralPath $assets -Value '{"targets":{"native":{"Example.Agent/1.0.0":{"type":"package"}}}}'
    $output = Join-Path $scratch 'manifest.json'
    & (Join-Path $root 'build/export-spdx-sbom.ps1') -AssetsFile $assets -PackageRoot (Join-Path $scratch 'empty') -FallbackPackageRoot (Join-Path $scratch 'fallback') -OutputPath $output
    $manifest = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    $entry = @($manifest.packages | Where-Object name -eq 'Example.Agent')
    if ($entry.Count -ne 1 -or $entry[0].licenseDeclared -ne 'LicenseRef-Example.Agent-1.0.0') { throw 'The fallback package license was not reported.' }
    if (@($manifest.hasExtractedLicensingInfos | Where-Object extractedText -match 'Example license text').Count -ne 1) { throw 'The fallback license text was not extracted.' }
    Write-Host 'Android agent SPDX fallback test passed.'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
