# Turns .github/ci/test-plan.json into the job matrices of .github/workflows/ci.yml.
#
#   pwsh .github/ci/plan-matrix.ps1 [-ThroughBatch 3]
#
# build_matrix : one include per platform that has an enabled entry (the job compiles once there).
# lane_matrix  : one include per (platform, lane) that consumes the build archive (one test job each).
# in_lane_*    : the single lane whose definition says "buildsInLane"; its test job has its own
#                checkout and builds the lane's entries in place instead of downloading the archive.
# has_in_lane  : 'true' when such a lane is enabled, which is what gates that job.
#
# The outputs are written as JSON ('{"include":[...]}') to $GITHUB_OUTPUT. A plan that enables nothing fails,
# and so does a plan whose every lane builds in place (nothing would consume the build job).
param(
    [int]$ThroughBatch = -1,
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

# Only archive lanes consume the build job; a platform whose entries all build in place (more likely in focus mode,
# where 'required' entries drop out) would otherwise get a build job with nothing to compile.
$inLaneNames = @($plan.lanes.PSObject.Properties |
    Where-Object { $_.Value.PSObject.Properties['buildsInLane'] -and $_.Value.buildsInLane } |
    ForEach-Object { $_.Name })
$build = @()
foreach ($platformName in @($enabled | Where-Object { $_.Lane -notin $inLaneNames } | ForEach-Object { Resolve-BuildPlatform $_.Platform @{} } | Sort-Object -Unique)) {
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    if ($null -eq $platform) { throw "Build platform '$platformName' is not defined under 'platforms'." }
    $build += [ordered]@{ name = $platformName; runner = $platform.Value.runner; dotnet_architecture = $platform.Value.dotnetArchitecture }
}

$lanes = @()
$inLane = @()
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
    $buildsInLane = [bool]($lane.Value.PSObject.Properties['buildsInLane'] -and $lane.Value.buildsInLane)
    if ($buildsInLane) { $inLane += $item } else { $lanes += $item }
}

if ($lanes.Count -eq 0) { throw "Every enabled lane builds in place, so nothing would consume the build job; enable one archive lane." }
if ($inLane.Count -gt 1) { throw "Only one in-place build lane is supported, but $($inLane.Count) are enabled: $(($inLane | ForEach-Object { $_.lane }) -join ', ')." }

$buildJson = (@{ include = $build } | ConvertTo-Json -Compress -Depth 5)
$laneJson = (@{ include = $lanes } | ConvertTo-Json -Compress -Depth 5)
$single = if ($inLane.Count -eq 1) { $inLane[0] } else { $null }

Write-Host "ThroughBatch=$ThroughBatch RunStatuses=$($runStatuses -join ',')"
Write-Host "build_matrix=$buildJson"
Write-Host "lane_matrix=$laneJson"
Write-Host "in_lane=$(if ($single) { "$($single.lane) ($($single.platform))" } else { 'none' })"
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "build_matrix=$buildJson"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "lane_matrix=$laneJson"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "has_in_lane=$([bool]$single ? 'true' : 'false')"
    if ($single) {
        Add-Content -Path $env:GITHUB_OUTPUT -Value "in_lane_name=$($single.lane)"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "in_lane_platform=$($single.platform)"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "in_lane_runner=$($single.runner)"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "in_lane_architecture=$($single.dotnet_architecture)"
        Add-Content -Path $env:GITHUB_OUTPUT -Value "in_lane_timeout=$($single.timeout)"
    }
}
