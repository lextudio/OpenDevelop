# Turns .github/ci/test-plan.json into the job matrices of .github/workflows/ci.yml.
#
#   pwsh .github/ci/plan-matrix.ps1 [-ThroughBatch 3] [-Platforms windows-x64,windows-arm64]
#
# -Platforms limits the run to the named platforms (comma separated; empty = all). The jobs of every platform wait for
# one build matrix, so a platform whose runners are unavailable would otherwise hold the results of the others back.
#
# build_matrix : one include per platform that has an enabled entry (the job compiles once there).
# lane_matrix  : one include per (platform, lane) with an enabled entry; every lane, unit suites included, unpacks the
#                archive of its platform's build and runs in parallel with the others once that build is done.
#
# The outputs are written as JSON ('{"include":[...]}') to $GITHUB_OUTPUT. A plan that enables nothing fails.
param(
    [int]$ThroughBatch = -1,
    [string]$Platforms = '',
    [string]$PlanPath = (Join-Path $PSScriptRoot 'test-plan.json')
)

$ErrorActionPreference = 'Stop'
$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
if ($ThroughBatch -lt 0) { $ThroughBatch = [int]$plan.throughBatch }
. (Join-Path $PSScriptRoot 'run-statuses.ps1')
$runStatuses = Get-RunStatuses $plan

$enabled = @()
foreach ($entry in $plan.entries) {
    if ($entry.batch -gt $ThroughBatch) { continue }
    foreach ($property in $entry.platforms.PSObject.Properties) {
        if ($property.Value -in $runStatuses) {
            $enabled += [pscustomobject]@{ Platform = $property.Name; Lane = [string]$entry.lane; Id = $entry.id }
        }
    }
}
if ($enabled.Count -eq 0) { throw "The plan enables nothing up to batch $ThroughBatch." }
$onlyPlatforms = @($Platforms -split '[,\s]+' | Where-Object { $_ })
if ($onlyPlatforms.Count -gt 0) {
    $unknown = @($onlyPlatforms | Where-Object { $null -eq $plan.platforms.PSObject.Properties[$_] })
    if ($unknown.Count -gt 0) { throw "Unknown platform(s) in -Platforms: $($unknown -join ', '). Defined: $(($plan.platforms.PSObject.Properties.Name) -join ', ')." }
    $enabled = @($enabled | Where-Object { $_.Platform -in $onlyPlatforms })
    if ($enabled.Count -eq 0) { throw "The plan enables nothing on $($onlyPlatforms -join ', ') up to batch $ThroughBatch." }
}

# One build per *packaging* unit, not per runner. dist.windows.bat ships one Windows payload for x64 and ARM64
# (runtimes/ plus per-RID win-x64/win-arm64 folders and both launchers), so both Windows lanes consume the same archive;
# a platform names its provider with 'buildFrom' and the default is itself. macOS is a single osx-arm64 payload
# (dist.macos.sh) and builds for itself.
function Resolve-BuildPlatform([string]$platformName, [hashtable]$seen) {
    if ($seen.ContainsKey($platformName)) { throw "buildFrom chain loops at '$platformName'." }
    $seen[$platformName] = $true
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    if ($null -eq $platform) { throw "Platform '$platformName' is used by an entry but not defined under 'platforms'." }
    $from = if ($platform.Value.PSObject.Properties['buildFrom']) { [string]$platform.Value.buildFrom } else { $platformName }
    if ($from -eq $platformName) { return $platformName }
    return Resolve-BuildPlatform $from $seen
}

$build = @()
foreach ($platformName in @($enabled | ForEach-Object { Resolve-BuildPlatform $_.Platform @{} } | Sort-Object -Unique)) {
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    if ($null -eq $platform) { throw "Build platform '$platformName' is not defined under 'platforms'." }
    $build += [ordered]@{ name = $platformName; runner = $platform.Value.runner; dotnet_architecture = $platform.Value.dotnetArchitecture }
}

$lanes = @()
foreach ($group in @($enabled | Group-Object Platform, Lane | Sort-Object Name)) {
    $platformName = $group.Group[0].Platform
    $laneName = $group.Group[0].Lane
    $lane = $plan.lanes.PSObject.Properties[$laneName]
    if ($null -eq $lane) { throw "Lane '$laneName' is used by an entry but not defined under 'lanes'." }
    $platform = $plan.platforms.PSObject.Properties[$platformName].Value
    $item = [ordered]@{
        platform = $platformName; lane = $laneName
        # The archive this lane unpacks: the packaging unit it belongs to (see Resolve-BuildPlatform).
        build_platform = (Resolve-BuildPlatform $platformName @{})
        runner = $platform.runner
        dotnet_architecture = $platform.dotnetArchitecture; timeout = [int]$lane.Value.timeoutMinutes
        needs_cliclick = [bool]($platform.PSObject.Properties['needsCliclick'] -and $platform.needsCliclick)
        needs_nuget_cache = [bool]($lane.Value.PSObject.Properties['needsNuGetCache'] -and $lane.Value.needsNuGetCache)
        needs_gtk = [bool]($lane.Value.PSObject.Properties['needsGtk'] -and $lane.Value.needsGtk)
        entries = $group.Count
    }
    $lanes += $item
}

$buildJson = (@{ include = $build } | ConvertTo-Json -Compress -Depth 5)
$laneJson = (@{ include = $lanes } | ConvertTo-Json -Compress -Depth 5)

Write-Host "ThroughBatch=$ThroughBatch RunStatuses=$($runStatuses -join ',') Platforms=$(if ($onlyPlatforms.Count -gt 0) { $onlyPlatforms -join ',' } else { 'all' })"
Write-Host "build_matrix=$buildJson"
Write-Host "lane_matrix=$laneJson"
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "build_matrix=$buildJson"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "lane_matrix=$laneJson"
}
