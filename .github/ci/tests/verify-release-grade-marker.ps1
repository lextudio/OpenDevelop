<#
  The release gate reads the marker release-grade.ps1 writes. This checks both halves of that contract and that the workflow files
  are valid YAML (a stray quote in a workflow only shows when a tag is pushed):
    - release-grade.ps1 writes exactly two lines, "release-grade <sha> run <id>" and "skipped: <sorted unique list>" (a second line even when empty);
    - release-grade-covers.sh (the predicate release.yml runs per candidate CI run) lets a macOS-free marker gate only a release that skips macOS,
      and lets an empty or legacy one-line marker gate any release.
#>
$ErrorActionPreference = 'Stop'
$ci = Join-Path $PSScriptRoot '..'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('od-marker-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $env:NEEDS = '{"plan":{"result":"success"},"build":{"result":"success"},"test":{"result":"success"}}'
    $env:NARROWED = 'false'; $env:OD_CI_FULL = 'true'; $env:GITHUB_SHA = 'abc'; $env:GITHUB_RUN_ID = '7'
    $cases = @(@($null, ''), @(',', ''), @(',macos-arm64,', 'macos-arm64'), @('macos-arm64 linux-x64,macos-arm64', 'linux-x64,macos-arm64'))
    foreach ($case in $cases) {
        if ($null -eq $case[0]) { Remove-Item Env:SKIPPED -ErrorAction SilentlyContinue } else { $env:SKIPPED = $case[0] }
        $marker = Join-Path $tmp 'marker.txt'
        & (Join-Path $ci 'release-grade.ps1') -Output $marker | Out-Null
        $lines = @(Get-Content $marker)
        if ($lines.Count -ne 2 -or $lines[0] -ne 'release-grade abc run 7' -or $lines[1] -ne "skipped: $($case[1])") {
            throw "SKIPPED='$($case[0])' wrote: $($lines -join ' | '); expected a second line 'skipped: $($case[1])'"
        }
    }
    $covers = Join-Path $ci 'release-grade-covers.sh'
    function Test-Covers([string[]]$markerLines, [string]$releaseSkipped) {
        $file = Join-Path $tmp 'candidate.txt'
        Set-Content -LiteralPath $file -Value $markerLines
        & bash $covers $file $releaseSkipped | Out-Null
        return ($LASTEXITCODE -eq 0)
    }
    $checks = @(
        @('macOS-free marker, release ships macOS', @('release-grade a run 1', 'skipped: macos-arm64'), '', $false),
        @('macOS-free marker, release skips macOS', @('release-grade a run 1', 'skipped: macos-arm64'), 'macos-arm64', $true),
        @('full marker, release ships macOS', @('release-grade a run 1', 'skipped: '), '', $true),
        @('full marker, release skips macOS', @('release-grade a run 1', 'skipped: '), 'macos-arm64', $true),
        @('legacy one-line marker', @('release-grade a run 1'), '', $true),
        @('two skipped, release skips one', @('release-grade a run 1', 'skipped: linux-x64,macos-arm64'), 'macos-arm64', $false)
    )
    foreach ($check in $checks) {
        if ((Test-Covers $check[1] $check[2]) -ne $check[3]) { throw "release-grade-covers.sh: '$($check[0])' should be $($check[3])" }
    }
    # The workflows must be valid YAML. PyYAML ships on the ubuntu runner images; skip loudly where it is missing.
    $python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1
    $yamlChecked = $false
    if ($python) {
        & $python.Source -c 'import yaml' 2>$null
        if ($LASTEXITCODE -eq 0) {
            foreach ($workflow in 'release.yml', 'ci.yml') {
                & $python.Source -c "import sys, yaml; yaml.safe_load(open(sys.argv[1], encoding='utf-8'))" (Join-Path $ci "../workflows/$workflow")
                if ($LASTEXITCODE -ne 0) { throw "$workflow is not valid YAML" }
            }
            $yamlChecked = $true
        }
    }
    if (-not $yamlChecked) { Write-Host '::warning::PyYAML is not available here; the workflow files were not parsed.' }
    Write-Host "The release-grade marker contract holds ($($cases.Count) writer cases, $($checks.Count) gate cases$(if ($yamlChecked) { ', workflows parse' }))."
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
