$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '../build-binaries.ps1'
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
  "lanes": { "archive": {}, "unit": { "buildsInLane": true } },
  "entries": [
    { "id": "early", "batch": 1, "lane": "archive", "project": "early.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "arm", "batch": 4, "lane": "archive", "project": "arm.csproj", "platforms": { "windows-arm64": "observe" }, "additionalProjects": [{ "project": "fixture.csproj", "platforms": ["windows-x64"] }] },
    { "id": "transitive", "batch": 4, "lane": "archive", "project": "transitive.csproj", "platforms": { "consumer": "required" } },
    { "id": "mac", "batch": 4, "lane": "archive", "project": "mac.csproj", "platforms": { "macos-arm64": "observe" } },
    { "id": "unit-x64", "batch": 1, "lane": "unit", "project": "unit-x64.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "unit-arm", "batch": 1, "lane": "unit", "project": "unit-arm.csproj", "platforms": { "windows-arm64": "observe" } }
  ]
}
'@ | Set-Content -LiteralPath $planPath
    $archive = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive selection failed.' }
    foreach ($name in @('arm.csproj', 'fixture.csproj', 'transitive.csproj')) {
        if (-not ($archive | Where-Object { $_ -like "*$name*" })) { throw "Missing $name from archive build." }
    }
    foreach ($name in @('mac.csproj', 'unit-x64.csproj', 'unit-arm.csproj')) {
        if ($archive | Where-Object { $_ -like "*$name*" }) { throw "Unexpected $name in archive build." }
    }
    $mac = & pwsh -NoProfile -File $builder -Platform macos-arm64 -ArchiveLanesOnly -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($mac | Where-Object { $_ -like '*mac.csproj*' }) -or ($mac | Where-Object { $_ -like '*arm.csproj*' -or $_ -like '*transitive.csproj*' -or $_ -like '*fixture.csproj*' })) {
        throw 'macOS archive must include its own projects and exclude Windows-only projects.'
    }
    $early = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -ThroughBatch 3 -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($early | Where-Object { $_ -like '*early.csproj*' }) -or ($early | Where-Object { $_ -like '*arm.csproj*' })) {
        throw 'Archive selection did not respect the batch limit.'
    }
    $unit = & pwsh -NoProfile -File $builder -Platform windows-x64 -Lane unit -SkipHostBuilds -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($unit | Where-Object { $_ -like '*unit-x64.csproj*' }) -or ($unit | Where-Object { $_ -like '*unit-arm.csproj*' })) {
        throw 'In-place lane must build only its own platform entries.'
    }

    # Focus mode: 'required' entries drop out of the planner, the archive build and the in-place lane alike, and
    # OD_CI_FULL=true brings them back. 'solo' has only an in-place entry, so it must get no build job.
    $focusPlan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $focusPlan | Add-Member -NotePropertyName focus -NotePropertyValue $true
    $focusPlan.platforms | Add-Member -NotePropertyName solo -NotePropertyValue ([pscustomobject]@{})
    $focusPlan.entries += [pscustomobject]@{ id = 'unit-solo'; batch = 1; lane = 'unit'; project = 'unit-solo.csproj'; platforms = [pscustomobject]@{ solo = 'observe' } }
    # The planner allows a single in-place lane job, so leave 'solo' as the only platform with an in-place entry.
    ($focusPlan.entries | Where-Object { $_.id -eq 'unit-arm' }).platforms.'windows-arm64' = 'excluded'
    $focusPlanPath = Join-Path $directory 'focus-plan.json'
    $focusPlan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $focusPlanPath
    $planner = Join-Path $PSScriptRoot '../plan-matrix.ps1'
    $previousFull = $env:OD_CI_FULL
    try {
        $env:OD_CI_FULL = 'false'
        $focus = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -List -PlanPath $focusPlanPath
        if ($LASTEXITCODE -ne 0 -or -not ($focus | Where-Object { $_ -like '*arm.csproj*' }) -or ($focus | Where-Object { $_ -like '*transitive.csproj*' -or $_ -like '*early.csproj*' })) {
            throw 'Focus mode must build the observe entries and leave the required ones out.'
        }
        $plannerOutput = (& pwsh -NoProfile -File $planner -PlanPath $focusPlanPath) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Planning in focus mode failed.' }
        $buildMatrix = [regex]::Match($plannerOutput, 'build_matrix=(\{.*\})').Groups[1].Value | ConvertFrom-Json
        $buildNames = @($buildMatrix.include | ForEach-Object { $_.name })
        if ('solo' -in $buildNames) { throw 'A platform with only in-place entries must get no build job.' }
        if ($plannerOutput -notmatch 'in_lane=unit \(solo\)') { throw 'Focus mode must keep the observe in-place entry.' }
        if (-not ('windows-x64' -in $buildNames -and 'macos-arm64' -in $buildNames)) { throw 'Focus mode dropped a build that observe entries need.' }
        $env:OD_CI_FULL = 'true'
        $full = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -List -PlanPath $focusPlanPath
        if ($LASTEXITCODE -ne 0 -or -not ($full | Where-Object { $_ -like '*transitive.csproj*' })) {
            throw 'OD_CI_FULL=true must bring the required entries back in focus mode.'
        }
        $unitFull = & pwsh -NoProfile -File $builder -Platform windows-x64 -Lane unit -SkipHostBuilds -List -PlanPath $focusPlanPath
        if ($LASTEXITCODE -ne 0 -or -not ($unitFull | Where-Object { $_ -like '*unit-x64.csproj*' })) {
            throw 'OD_CI_FULL=true must bring the required in-place entries back.'
        }
    } finally {
        $env:OD_CI_FULL = $previousFull
    }
    Write-Host 'Build selection checks passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
