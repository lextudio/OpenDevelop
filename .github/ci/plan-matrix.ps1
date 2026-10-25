# Turns .github/ci/test-plan.json into the two job matrices of .github/workflows/ci.yml.
#
#   pwsh .github/ci/plan-matrix.ps1 [-ThroughBatch 3]
#
# build_matrix: one include per platform that has an enabled entry (the job compiles once there).
# lane_matrix : one include per (platform, lane) that has an enabled entry (one test job each).
# Both are written to $GITHUB_OUTPUT as JSON ('{"include":[...]}'); a plan that enables nothing fails.
param(
    [int]$ThroughBatch = -1,
    [string]$PlanPath = (Join-Path $PSScriptRoot 'test-plan.json')
)

$ErrorActionPreference = 'Stop'
$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
if ($ThroughBatch -lt 0) { $ThroughBatch = [int]$plan.throughBatch }

$enabled = @()
foreach ($entry in $plan.entries) {
    if ($entry.batch -gt $ThroughBatch) { continue }
    foreach ($property in $entry.platforms.PSObject.Properties) {
        if ($property.Value -in @('observe', 'required')) {
            $enabled += [pscustomobject]@{ Platform = $property.Name; Lane = [string]$entry.lane; Id = $entry.id }
        }
    }
}
if ($enabled.Count -eq 0) { throw "The plan enables nothing up to batch $ThroughBatch." }

$build = @()
foreach ($platformName in @($enabled | ForEach-Object { $_.Platform } | Sort-Object -Unique)) {
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    if ($null -eq $platform) { throw "Platform '$platformName' is used by an entry but not defined under 'platforms'." }
    $build += [ordered]@{ name = $platformName; runner = $platform.Value.runner; dotnet_architecture = $platform.Value.dotnetArchitecture }
}

$lanes = @()
foreach ($group in @($enabled | Group-Object Platform, Lane | Sort-Object Name)) {
    $platformName = $group.Group[0].Platform
    $laneName = $group.Group[0].Lane
    $lane = $plan.lanes.PSObject.Properties[$laneName]
    if ($null -eq $lane) { throw "Lane '$laneName' is used by an entry but not defined under 'lanes'." }
    $platform = $plan.platforms.PSObject.Properties[$platformName].Value
    $lanes += [ordered]@{
        platform = $platformName; lane = $laneName; runner = $platform.runner
        dotnet_architecture = $platform.dotnetArchitecture; timeout = [int]$lane.Value.timeoutMinutes
        needs_nuget_cache = [bool]($lane.Value.PSObject.Properties['needsNuGetCache'] -and $lane.Value.needsNuGetCache)
        entries = $group.Count
    }
}

$buildJson = (@{ include = $build } | ConvertTo-Json -Compress -Depth 5)
$laneJson = (@{ include = $lanes } | ConvertTo-Json -Compress -Depth 5)
Write-Host "ThroughBatch=$ThroughBatch"
Write-Host "build_matrix=$buildJson"
Write-Host "lane_matrix=$laneJson"
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "build_matrix=$buildJson"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "lane_matrix=$laneJson"
}
