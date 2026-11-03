$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '../build-binaries.ps1'
$planner = Join-Path $PSScriptRoot '../plan-matrix.ps1'
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('od-build-selection-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $planPath = Join-Path $directory 'plan.json'
    @'
{
  "throughBatch": 4,
  "platforms": {
    "windows-x64": {},
    "windows-arm64": { "buildFrom": "windows-x64" },
    "consumer": { "buildFrom": "windows-arm64" },
    "macos-arm64": {}
  },
  "lanes": {
    "unit": { "job": "fast", "timeoutMinutes": 10 },
    "archive": { "job": "fast", "timeoutMinutes": 30, "needsNuGetCache": true },
    "long": { "job": "slow", "timeoutMinutes": 60, "needsGtk": true },
    "alone": { "timeoutMinutes": 5 }
  },
  "entries": [
    { "id": "early", "batch": 1, "lane": "archive", "project": "early.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "arm", "batch": 4, "lane": "archive", "project": "arm.csproj", "platforms": { "windows-arm64": "observe" }, "additionalProjects": [{ "project": "fixture.csproj", "platforms": ["windows-x64"] }] },
    { "id": "transitive", "batch": 4, "lane": "archive", "project": "transitive.csproj", "platforms": { "consumer": "required" } },
    { "id": "mac", "batch": 4, "lane": "archive", "project": "mac.csproj", "platforms": { "macos-arm64": "observe" } },
    { "id": "unit-x64", "batch": 1, "lane": "unit", "project": "unit-x64.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "unit-arm", "batch": 1, "lane": "unit", "project": "unit-arm.csproj", "platforms": { "windows-arm64": "observe" } },
    { "id": "unit-msbuild", "batch": 1, "lane": "unit", "kind": "msbuild", "target": "tests.proj", "platforms": { "macos-arm64": "observe" } },
    { "id": "mac-long", "batch": 4, "lane": "long", "project": "mac-long.csproj", "platforms": { "macos-arm64": "observe" } },
    { "id": "mac-alone", "batch": 4, "lane": "alone", "project": "mac-alone.csproj", "platforms": { "macos-arm64": "observe" } }
  ]
}
'@ | Set-Content -LiteralPath $planPath

    # One build per platform compiles every lane's projects, unit suites included, for itself and for each platform that
    # takes its archive through 'buildFrom' (transitively).
    $archive = & pwsh -NoProfile -File $builder -Platform windows-x64 -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive selection failed.' }
    foreach ($name in @('early.csproj', 'arm.csproj', 'fixture.csproj', 'transitive.csproj', 'unit-x64.csproj', 'unit-arm.csproj')) {
        if (-not ($archive | Where-Object { $_ -like "* $name *" })) { throw "Missing $name from the windows-x64 build." }
    }
    if ($archive | Where-Object { $_ -like '* mac.csproj *' }) { throw 'Unexpected mac.csproj in the windows-x64 build.' }
    $mac = & pwsh -NoProfile -File $builder -Platform macos-arm64 -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($mac | Where-Object { $_ -like '* mac.csproj *' }) -or ($mac | Where-Object { $_ -like '* arm.csproj *' -or $_ -like '* transitive.csproj *' -or $_ -like '* fixture.csproj *' -or $_ -like '* unit-*.csproj *' })) {
        throw 'The macOS build must include its own projects and exclude the Windows ones.'
    }
    $early = & pwsh -NoProfile -File $builder -Platform windows-x64 -ThroughBatch 3 -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($early | Where-Object { $_ -like '* early.csproj *' }) -or ($early | Where-Object { $_ -like '* arm.csproj *' })) {
        throw 'Build selection did not respect the batch limit.'
    }

    # Every (platform, lane) with an enabled entry is one test job on its build platform's archive: the unit lane of a
    # 'buildFrom' platform unpacks its provider's archive, and a lane of source-only entries (kind msbuild) still runs.
    $plannerOutput = (& pwsh -NoProfile -File $planner -PlanPath $planPath) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Planning failed.' }
    $lanes = [regex]::Match($plannerOutput, '(?m)^lane_matrix=(\{.*\})$').Groups[1].Value | ConvertFrom-Json
    $laneNames = @($lanes.include | ForEach-Object { $include = $_; $include.lanes | ForEach-Object { "$_/$($include.platform)<-$($include.build_platform)" } } | Sort-Object)
    foreach ($expected in @('unit/windows-x64<-windows-x64', 'unit/windows-arm64<-windows-x64', 'unit/macos-arm64<-macos-arm64')) {
        if ($expected -notin $laneNames) { throw "Missing lane $expected; planned: $($laneNames -join ', ')" }
    }
    if ($plannerOutput -match 'in_lane') { throw 'The planner must not emit in-place outputs any more.' }

    # Lanes that name the same 'job' run in ONE job (the archive is downloaded once), in plan order, with the sum of their
    # timeouts and the flags any of them asks for; a lane of another job, or with no job, is a job of its own.
    $mac = @($lanes.include | Where-Object { $_.platform -eq 'macos-arm64' } | Sort-Object job)
    if (($mac | ForEach-Object { $_.job }) -join ',' -ne 'alone,fast,slow') { throw "macOS must plan the jobs alone, fast and slow, got: $(($mac | ForEach-Object { $_.job }) -join ',')" }
    $fast = $mac | Where-Object { $_.job -eq 'fast' }
    if (($fast.lanes -join ',') -ne 'unit,archive') { throw "The fast job must run its lanes in plan order, got: $($fast.lanes -join ',')" }
    if ($fast.timeout -ne 70) { throw "A job's timeout is the sum of its lanes' timeouts plus 30 minutes of headroom (10+30+30), got $($fast.timeout)." }
    if (-not $fast.needs_nuget_cache -or $fast.needs_gtk) { throw 'The fast job takes needsNuGetCache from archive and has no GTK need.' }
    $slow = $mac | Where-Object { $_.job -eq 'slow' }
    if (-not $slow.needs_gtk -or $slow.needs_nuget_cache) { throw 'The slow job takes needsGtk from its lane only.' }
    $alone = $mac | Where-Object { $_.job -eq 'alone' }
    if (($alone.lanes -join ',') -ne 'alone' -or $alone.timeout -ne 35) { throw 'A lane without a job is a job of its own.' }
    # A platform's jobs never mix: Windows x64 has only the 'fast' job's lanes it enables.
    $armJobs = @($lanes.include | Where-Object { $_.platform -eq 'windows-arm64' } | ForEach-Object { $_.job })
    if ($armJobs.Count -ne 1) { throw "windows-arm64 enables only the unit lane, expected one job, got: $($armJobs -join ',')" }

    # A platform whose only enabled entry runs from source (kind msbuild, no 'project') still gets a successful build: the
    # host from 'buildFirst' and nothing for the entry itself. A platform with no enabled entry at all is an error.
    $sourcePlan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $sourcePlan.platforms | Add-Member -NotePropertyName solo -NotePropertyValue ([pscustomobject]@{})
    $sourcePlan.platforms | Add-Member -NotePropertyName empty -NotePropertyValue ([pscustomobject]@{})
    $sourcePlan | Add-Member -NotePropertyName buildFirst -NotePropertyValue @([pscustomobject]@{ project = 'host.csproj' })
    $sourcePlan.entries += [pscustomobject]@{ id = 'solo-msbuild'; batch = 1; lane = 'unit'; kind = 'msbuild'; target = 'solo.proj'; platforms = [pscustomobject]@{ solo = 'observe' } }
    $sourcePlanPath = Join-Path $directory 'source-plan.json'
    $sourcePlan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $sourcePlanPath
    $solo = @(& pwsh -NoProfile -File $builder -Platform solo -List -PlanPath $sourcePlanPath | Where-Object { $_ -like 'dotnet *' })
    if ($LASTEXITCODE -ne 0 -or $solo.Count -ne 1 -or $solo[0] -notlike '* host.csproj *') {
        throw "A platform with only source-only entries must build just the host, got: $($solo -join ' | ')"
    }
    & pwsh -NoProfile -File $builder -Platform empty -List -PlanPath $sourcePlanPath *> $null
    if ($LASTEXITCODE -eq 0) { throw 'A platform with no enabled entry must fail the build.' }

    # Focus mode: 'required' entries drop out of the planner and the build alike, and OD_CI_FULL=true brings them back.
    $focusPlan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $focusPlan | Add-Member -NotePropertyName focus -NotePropertyValue $true
    $focusPlanPath = Join-Path $directory 'focus-plan.json'
    $focusPlan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $focusPlanPath
    $previousFull = $env:OD_CI_FULL
    try {
        $env:OD_CI_FULL = 'false'
        $focus = & pwsh -NoProfile -File $builder -Platform windows-x64 -List -PlanPath $focusPlanPath
        if ($LASTEXITCODE -ne 0 -or -not ($focus | Where-Object { $_ -like '* arm.csproj *' }) -or ($focus | Where-Object { $_ -like '* transitive.csproj *' -or $_ -like '* early.csproj *' -or $_ -like '* unit-x64.csproj *' })) {
            throw 'Focus mode must build the observe entries and leave the required ones out.'
        }
        $focusOutput = (& pwsh -NoProfile -File $planner -PlanPath $focusPlanPath) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Planning in focus mode failed.' }
        $buildNames = @(([regex]::Match($focusOutput, '(?m)^build_matrix=(\{.*\})$').Groups[1].Value | ConvertFrom-Json).include | ForEach-Object { $_.name })
        if (-not ('windows-x64' -in $buildNames -and 'macos-arm64' -in $buildNames)) { throw 'Focus mode dropped a build that observe entries need.' }
        # -Platforms limits the run to the named platforms (a dispatch input), so an unavailable platform cannot hold the
        # others back, and rejects a name the plan does not define.
        $macOnly = (& pwsh -NoProfile -File $planner -PlanPath $focusPlanPath -Platforms 'macos-arm64') -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Planning for one platform failed.' }
        $macMatrix = [regex]::Match($macOnly, '(?m)^build_matrix=(\{.*\})$').Groups[1].Value | ConvertFrom-Json
        $macLanes = [regex]::Match($macOnly, '(?m)^lane_matrix=(\{.*\})$').Groups[1].Value | ConvertFrom-Json
        if (@($macMatrix.include | ForEach-Object { $_.name }) -join ',' -ne 'macos-arm64') { throw '-Platforms macos-arm64 must plan only the macOS build.' }
        if (@($macLanes.include | Where-Object { $_.platform -ne 'macos-arm64' }).Count -gt 0) { throw '-Platforms macos-arm64 must plan only macOS lanes.' }
        & pwsh -NoProfile -File $planner -PlanPath $focusPlanPath -Platforms 'no-such-platform' *> $null
        if ($LASTEXITCODE -eq 0) { throw '-Platforms must reject a platform the plan does not define.' }
        $env:OD_CI_FULL = 'true'
        $full = & pwsh -NoProfile -File $builder -Platform windows-x64 -List -PlanPath $focusPlanPath
        if ($LASTEXITCODE -ne 0 -or -not ($full | Where-Object { $_ -like '* transitive.csproj *' }) -or -not ($full | Where-Object { $_ -like '* unit-x64.csproj *' })) {
            throw 'OD_CI_FULL=true must bring the required entries back in focus mode.'
        }
    } finally {
        $env:OD_CI_FULL = $previousFull
    }
    Write-Host 'Build selection checks passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
