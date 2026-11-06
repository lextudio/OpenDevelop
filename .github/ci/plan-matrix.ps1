# Turns .github/ci/test-plan.json into the job matrices of .github/workflows/ci.yml.
#
#   pwsh .github/ci/plan-matrix.ps1 [-ThroughBatch 3] [-Platforms windows-x64,windows-arm64]
#
# -Platforms limits the run to the named platforms (comma separated; empty = all). The jobs of every platform wait for
# one build matrix, so a platform whose runners are unavailable would otherwise hold the results of the others back.
#
# build_matrix : one include per platform that has an enabled entry (the job compiles once there).
# lane_matrix  : one include per (platform, job): the lanes of a platform that share a 'job' in the plan (the unit suites and
#                the stable and unstable integration lanes are 'fast') run one after another in ONE job, because every job
#                has to download and unpack the platform's whole archive (measured: 70 minutes of that against 30 minutes of
#                tests across the lane jobs of one run). A lane without a 'job' is its own job. Jobs of different groups
#                (fast, designer) run in parallel once the build is done. A platform may name 'singleJob': every lane of
#                that platform then runs in ONE job with that name, so a scarce runner image (macOS) is acquired once and
#                finishes all of its lanes instead of queueing once per group. Each include carries 'lanes' (in plan order)
#                and the sum of their timeouts plus 30 minutes of preparation headroom; a flag a lane asks for
#                (needsNuGetCache, needsGtk) holds for the whole job, and 'needs_ios_simulator' says the job hosts an iOS
#                Simulator. A lane may also name 'runner': the device lanes need an image their platform's default runner is
#                not (the iOS Simulator lane needs Xcode 27), so the job runs there instead. Lanes that share a job must
#                name the same runner, because a job runs on one machine.
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

# The job a lane runs in: its 'job' when the plan names one, else the lane itself.
function Get-LaneJob([string]$laneName) {
    $lane = $plan.lanes.PSObject.Properties[$laneName]
    if ($null -eq $lane) { throw "Lane '$laneName' is used by an entry but not defined under 'lanes'." }
    if ($lane.Value.PSObject.Properties['job'] -and $lane.Value.job) { return [string]$lane.Value.job }
    return $laneName
}
function Test-LaneFlag([string]$laneName, [string]$flag) {
    $lane = $plan.lanes.PSObject.Properties[$laneName].Value
    return [bool]($lane.PSObject.Properties[$flag] -and $lane.$flag)
}
# A lane declares the device classes it may run with the OS family that hosts the device; the job needs the iOS Simulator
# set up when one of its lanes declares that class (the workflow boots it before the lanes start).
function Test-LaneDeviceClass([string]$laneName, [string]$className) {
    $lane = $plan.lanes.PSObject.Properties[$laneName].Value
    if (-not $lane.PSObject.Properties['deviceClasses']) { return $false }
    return [bool]($lane.deviceClasses.PSObject.Properties[$className])
}
# A platform that names 'singleJob' runs every lane in one job of that name, on one runner: the macOS image is the scarce
# resource, so the unit, integration, designer and Simulator lanes share one acquisition instead of three queue entries.
function Get-PlatformSingleJob([string]$platformName) {
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    if ($null -eq $platform) { throw "Platform '$platformName' is used by an entry but not defined under 'platforms'." }
    if ($platform.Value.PSObject.Properties['singleJob'] -and $platform.Value.singleJob) { return [string]$platform.Value.singleJob }
    return $null
}
function Get-EffectiveJob([string]$platformName, [string]$laneName) {
    $single = Get-PlatformSingleJob $platformName
    if ($single) { return $single }
    return Get-LaneJob $laneName
}
$laneOrder = @($plan.lanes.PSObject.Properties.Name)

$lanes = @()
foreach ($group in @($enabled | Group-Object { "$($_.Platform)|$(Get-EffectiveJob $_.Platform $_.Lane)" } | Sort-Object Name)) {
    $platformName = $group.Group[0].Platform
    $jobName = Get-EffectiveJob $platformName $group.Group[0].Lane
    # The lanes this job runs, in the order the plan defines them (the quick unit suites first).
    [string[]]$laneNames = @($group.Group | ForEach-Object { $_.Lane } | Sort-Object -Unique | Sort-Object { $laneOrder.IndexOf($_) })
    $platform = $plan.platforms.PSObject.Properties[$platformName].Value
    # A lane may name the image its job runs on; the device lanes need one their platform's default is not. Lanes that
    # share a job must agree, because the job runs on one machine.
    $runnerOverrides = @($laneNames | ForEach-Object {
        $definition = $plan.lanes.PSObject.Properties[$_].Value
        if ($definition.PSObject.Properties['runner'] -and $definition.runner) { [string]$definition.runner }
    } | Sort-Object -Unique)
    if ($runnerOverrides.Count -gt 1) {
        throw "The lanes of job '$jobName' on '$platformName' name different runners ($($runnerOverrides -join ', ')); a job runs on one machine."
    }
    $item = [ordered]@{
        platform = $platformName; job = $jobName; lanes = $laneNames
        # The archive this job unpacks: the packaging unit it belongs to (see Resolve-BuildPlatform).
        build_platform = (Resolve-BuildPlatform $platformName @{})
        runner = if ($runnerOverrides.Count -eq 1) { $runnerOverrides[0] } else { $platform.runner }
        dotnet_architecture = $platform.dotnetArchitecture
        # The lanes' own budgets plus headroom for what sits outside every one of them: the archive download and unpack and
        # the installs (11-15 minutes on Windows), and the summary and upload steps that must still run after the last lane.
        timeout = [int](($laneNames | ForEach-Object { [int]$plan.lanes.PSObject.Properties[$_].Value.timeoutMinutes } | Measure-Object -Sum).Sum) + 30
        needs_cliclick = [bool]($platform.PSObject.Properties['needsCliclick'] -and $platform.needsCliclick)
        needs_nuget_cache = [bool]@($laneNames | Where-Object { Test-LaneFlag $_ 'needsNuGetCache' }).Count
        needs_gtk = [bool]@($laneNames | Where-Object { Test-LaneFlag $_ 'needsGtk' }).Count
        needs_ios_simulator = [bool]@($laneNames | Where-Object { Test-LaneDeviceClass $_ 'IosSimulatorDebuggingIntegrationTests' }).Count
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
