# Compiles everything the enabled plan entries need, once per platform (the 'build' job) or once per in-place lane
# (a lane with 'buildsInLane', which has its own checkout and no build archive).
#
#   pwsh .github/ci/build-binaries.ps1 -Platform windows-x64 [-ThroughBatch 3] [-Lane unit] [-SkipHostBuilds] [-List]
#
# -List prints the build commands in order and exits, which is how to check a plan edit without building.
# -Lane restricts the build to the entries of one lane; -ArchiveLanesOnly leaves out the entries of lanes that build in
# place (they compile in their own job); -SkipHostBuilds leaves out both the 'buildFirst' and 'buildLast'
# projects, which an in-place lane does not need: nothing in it builds an add-in against the host manifest or starts the IDE.
#
# The plan's 'buildFirst' projects (the host) are built first, then every distinct 'project' of an enabled entry (Debug,
# with the entry's 'buildArgs'), then the 'buildLast' projects (the host once more, as the Terminal workflow builds it; see the plan).  Test jobs later run the compiled assemblies without
# restoring or building, so anything an entry runs must be produced here.  Per-project durations and the size of the
# biggest output folders go to the job summary: they are what the packaging step and the lane budgets are judged on.
param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [int]$ThroughBatch = -1,
    [string]$Lane = '',
    [switch]$ArchiveLanesOnly,
    [switch]$SkipHostBuilds,
    [switch]$List,
    [string]$PlanPath = (Join-Path $PSScriptRoot 'test-plan.json')
)

$ErrorActionPreference = 'Stop'

# GitVersion's GitHub Actions integration appends every version variable to the runner's env command file, and a build
# that compiles several projects in parallel runs several GitVersion processes against that one file. On 2026-10-25 that
# failed a lane outright: "IOException: The process cannot access the file D:\a\_temp\_runner_file_commands\set_env_..."
# from GitVersion.Agents.GitHubActions.WriteIntegration in UnitTesting.csproj. Nothing here reads GitVersion_BuildNumber,
# so the integration is switched off for the build: with GITHUB_ACTIONS gone the CLI picks no build agent.
$env:GITHUB_ACTIONS = $null

$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
if ($ThroughBatch -lt 0) { $ThroughBatch = [int]$plan.throughBatch }

# A lane with 'buildsInLane' compiles its own entries in its own job, so the archive build leaves them out.
$inLaneNames = @($plan.lanes.PSObject.Properties |
    Where-Object { $_.Value.PSObject.Properties['buildsInLane'] -and $_.Value.buildsInLane } |
    ForEach-Object { $_.Name })
$entries = @($plan.entries | Where-Object {
    $_.batch -le $ThroughBatch -and $_.PSObject.Properties['project'] -and $_.platforms.PSObject.Properties[$Platform] -and $_.platforms.$Platform -in @('observe', 'required') -and (-not $Lane -or $_.lane -eq $Lane) -and (-not $ArchiveLanesOnly -or $_.lane -notin $inLaneNames)
})
if ($entries.Count -eq 0) {
    $scope = if ($Lane) { "lane '$Lane'" } elseif ($ArchiveLanesOnly) { 'the archive lanes' } else { 'any lane' }
    throw "No project to build for $Platform in $scope up to batch $ThroughBatch."
}

# One build per distinct project.  Entries sharing a project must agree on its build arguments.
$builds = @()
foreach ($group in ($entries | Group-Object { $_.project })) {
    $argumentSets = @($group.Group | ForEach-Object { if ($_.PSObject.Properties['buildArgs']) { ($_.buildArgs -join ' ') } else { '' } } | Select-Object -Unique)
    if ($argumentSets.Count -ne 1) { throw "Entries of project '$($group.Name)' disagree on buildArgs: $($argumentSets -join ' | ')" }
    [string[]]$extra = if ($argumentSets[0]) { $argumentSets[0] -split ' ' } else { @() }
    $builds += [pscustomobject]@{ Project = $group.Name; Args = $extra; First = $false }
}
# The integration project's own Build deploys the add-ins, so it goes before the unit test projects, which then reuse its output.
$builds = @($builds | Sort-Object { if ($_.Project -like '*OpenDevelop.IntegrationTests*') { 0 } else { 1 } }, { $_.Project })

# The host goes first (see 'buildFirst' in the plan): its build writes the manifest every add-in build trims against.
$first = @()
if (-not $SkipHostBuilds -and $plan.PSObject.Properties['buildFirst']) {
    foreach ($item in @($plan.buildFirst)) { $first += [pscustomobject]@{ Project = $item.project; Args = [string[]]@(); First = $true } }
}
$last = @()
if (-not $SkipHostBuilds -and $plan.PSObject.Properties['buildLast']) {
    $platformEntry = $plan.platforms.PSObject.Properties[$Platform]
    $rid = if ($platformEntry -and $platformEntry.Value.PSObject.Properties['rid']) { [string]$platformEntry.Value.rid } else { '' }
    foreach ($item in @($plan.buildLast)) {
        if ($item.PSObject.Properties['platforms'] -and $Platform -notin @($item.platforms)) { continue }
        [string[]]$itemArgs = if ($item.PSObject.Properties['args']) { @($item.args | ForEach-Object { $_ -replace '\{rid\}', $rid }) } else { @() }
        if (-not $rid -and ($item.args -join ' ') -match '\{rid\}') { throw "buildLast uses {rid} but platform '$Platform' defines no 'rid'." }
        $verb = if ($item.PSObject.Properties['verb']) { [string]$item.verb } else { 'build' }
        $last += [pscustomobject]@{ Project = $item.project; Args = $itemArgs; First = $false; Verb = $verb }
    }
}
$builds = @($first) + @($builds) + @($last)

if ($List) {
    foreach ($build in $builds) {
        $verb = if ($build.PSObject.Properties['Verb'] -and $build.Verb) { $build.Verb } else { 'build' }
        Write-Host "dotnet $verb $($build.Project) -c Debug $($build.Args -join ' ')".TrimEnd()
    }
    exit 0
}

# The host's OpenDevelop.deps.json and folder after a build. A second build of the host project deletes its mutable
# package outputs before copying and does not copy them again, so the folder can end up without PresentationFramework even
# though the manifest still lists its libraries; the per-step numbers are what caught that on the runner (193 dlls with
# the file present after the first build, 173 and missing after a rebuild).
$hostDeps = 'src/Main/SharpDevelop/bin/Debug/net10.0-windows/OpenDevelop.deps.json'
$hostDir = Split-Path $hostDeps -Parent
function Get-HostDepsState {
    if (-not (Test-Path $hostDeps)) { return 'absent' }
    # The folder matters as much as the manifest: a host whose deps.json lists 166 libraries but whose folder holds only
    # the app's own assemblies dies at start-up with FileNotFoundException, whichever build step emptied it.
    $dlls = @(Get-ChildItem -LiteralPath $hostDir -Filter *.dll -File -ErrorAction SilentlyContinue).Count
    $wpf = if (Test-Path (Join-Path $hostDir 'PresentationFramework.dll')) { 'present' } else { 'MISSING' }
    try {
        $json = Get-Content $hostDeps -Raw | ConvertFrom-Json
        $libraries = @($json.libraries.PSObject.Properties.Name)
        $hasReference = [bool]($libraries | Where-Object { $_ -like 'PresentationFramework/*' })
        return "libs=$($libraries.Count) presentationFrameworkRef=$hasReference folderDlls=$dlls presentationFramework=$wpf written=$((Get-Item $hostDeps).LastWriteTimeUtc.ToString('HH:mm:ss'))Z"
    } catch { return "unreadable ($($_.Exception.Message)) folderDlls=$dlls presentationFramework=$wpf" }
}

$rows = @()
$failed = @()
$requiresHostPayloadValidation = $false
foreach ($build in $builds) {
    $project = $build.Project
    Write-Host "::group::$verb $project $($build.Args -join ' ')"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $verb = if ($build.PSObject.Properties['Verb'] -and $build.Verb) { $build.Verb } else { 'build' }
    # On Windows a RID here silently turns IL-only assemblies architecture-specific and writes them under
    # bin/<tfm>/<rid>/, which breaks the one-payload model that serves both x64 and ARM64 lanes. macOS ships a single
    # osx-arm64 payload (dist.macos.sh), so a RID there cannot cost the other architecture anything.
    if ($Platform -like 'windows-*') {
        foreach ($argument in $build.Args) {
            if ($argument -in @('--runtime', '-r') -or $argument -match '^-p:RuntimeIdentifier=') {
                throw "'$project' is built with '$argument'; a planned Windows build must stay RID-less so the one payload serves both architectures."
            }
        }
    }
    [string[]]$arguments = @($verb, $project, '-c', 'Debug') + $build.Args
    & dotnet @arguments
    $exit = $LASTEXITCODE
    $watch.Stop()
    Write-Host '::endgroup::'
    $seconds = [int][math]::Round($watch.Elapsed.TotalSeconds)
    $depsState = Get-HostDepsState
    Write-Host "host deps.json after '$project': $depsState"
    if ($project -like '*Terminal.csproj' -or ($project -like '*SharpDevelop.csproj' -and $verb -eq 'publish')) { $requiresHostPayloadValidation = $true }
    $rows += [pscustomobject]@{ Project = $project; Seconds = $seconds; Outcome = $(if ($exit -eq 0) { 'passed' } else { 'failed' }); HostDeps = $depsState }
    if ($exit -ne 0) {
        $failed += $project
        Write-Host "::error title=build failed::$project exited with $exit after ${seconds}s."
        # Building the rest against a host that did not build would only produce untrimmed, misleading output.
        if ($build.First) { break }
    }
}

# Terminal's RID build can temporarily clear the mutable package payload.  The final host publish is
# intentionally ordered after it to restore that payload, so validate only the completed build plan.
if ($failed.Count -eq 0 -and $requiresHostPayloadValidation -and $Platform -like 'windows-*') {
    $depsState = Get-HostDepsState
    if ($depsState -match 'absent|presentationFrameworkRef=False|presentationFramework=MISSING') {
        throw "The final host payload is invalid on ${Platform}: $depsState"
    }
}

function Get-Size-Megabytes([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $bytes = (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    return [int][math]::Round($bytes / 1MB)
}

$lines = @("### Build on $Platform (through batch $ThroughBatch)", '', '| Project | Result | Seconds | Host deps.json afterwards |', '|---|---|---|---|')
foreach ($r in $rows) { $lines += "| $($r.Project) | $($r.Outcome) | $($r.Seconds) | $($r.HostDeps) |" }
$lines += @('', '| Output folder | MB |', '|---|---|')
foreach ($folder in (@('AddIns', 'src/Main/SharpDevelop/bin', 'obj') + @($builds | ForEach-Object { (Join-Path (Split-Path $_.Project -Parent) 'bin') -replace '\\', '/' }) | Select-Object -Unique)) {
    $size = Get-Size-Megabytes $folder
    if ($null -ne $size) { $lines += "| $folder | $size |" }
}
# What the deployed add-ins are made of: the packaging budget depends on it (the archive carries all of AddIns).
if (Test-Path 'AddIns') {
    $lines += @('', '| AddIns subfolder (largest 15) | MB |', '|---|---|')
    $folders = @(Get-ChildItem -Path 'AddIns' -Directory | ForEach-Object { Get-ChildItem -Path $_.FullName -Directory })
    $sized = @($folders | ForEach-Object { [pscustomobject]@{ Path = $_.FullName.Substring((Get-Location).Path.Length + 1); MB = (Get-Size-Megabytes $_.FullName) } } |
        Sort-Object MB -Descending | Select-Object -First 15)
    foreach ($f in $sized) { $lines += "| $($f.Path) | $($f.MB) |" }
    $byExtension = @(Get-ChildItem -Path 'AddIns' -Recurse -File -Force -ErrorAction SilentlyContinue | Group-Object { $_.Extension.ToLowerInvariant() } |
        ForEach-Object { [pscustomobject]@{ Extension = $_.Name; MB = [int][math]::Round(($_.Group | Measure-Object -Property Length -Sum).Sum / 1MB) } } |
        Sort-Object MB -Descending | Select-Object -First 8)
    $lines += @('', '| AddIns by extension | MB |', '|---|---|')
    foreach ($e in $byExtension) { $lines += "| $($e.Extension) | $($e.MB) |" }
}
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

if ($failed.Count -gt 0) { exit 1 }
exit 0
