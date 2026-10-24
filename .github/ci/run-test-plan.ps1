# Builds and runs the entries of .github/ci/test-plan.json that are enabled for one platform.
#
#   pwsh .github/ci/run-test-plan.ps1 -Platform windows-x64 -Stage All [-ThroughBatch 2]
#
# An entry runs when its batch is <= the through-batch and its status for the platform is 'observe' or
# 'required'.  A failing 'required' entry fails the script (exit 1); a failing 'observe' entry is only
# reported (a ::warning:: annotation and a row in the job summary).  Every entry's duration is recorded
# in the job summary so the plan's measuredSeconds can be kept honest.  CI invokes Build and Test as
# separate stages: all ordinary test binaries are compiled once before any test area starts, and the
# test stage uses --no-build.  The AddIn SDK entries are MSBuild assertion projects, so their Build target
# is the test itself and they remain in the Test stage.  Build failures retain the entry's observe/required
# semantics: the Test stage records them, skips only the unbuildable entry, and continues with the others.
param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [int]$ThroughBatch = -1,
    [ValidateSet('All', 'Build', 'Test')][string]$Stage = 'All',
    [string]$BuildStatePath = (Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-test-plan-$Platform.json"),
    [string]$PlanPath = (Join-Path $PSScriptRoot 'test-plan.json')
)

$ErrorActionPreference = 'Stop'
$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
if ($ThroughBatch -lt 0) { $ThroughBatch = [int]$plan.throughBatch }

function Get-Status($entry, [string]$platform) {
    $property = $entry.platforms.PSObject.Properties[$platform]
    if ($null -eq $property) { return 'excluded' }
    return [string]$property.Value
}

function Get-Command-Arguments($entry) {
    switch ($entry.kind) {
        'dotnet-test' { return @('test', '--project', $entry.target, '-c', 'Debug', '--no-build') }
        'dotnet-run'  { return @('run', '--project', $entry.target, '-c', 'Debug', '--no-build') }
        'msbuild'     { return @('msbuild', $entry.target, '-t:Build') }
        default       { throw "Unknown kind '$($entry.kind)' for entry '$($entry.id)'" }
    }
}

function Get-Build-Arguments($entry) {
    switch ($entry.kind) {
        'dotnet-test' { return @('build', $entry.target, '-c', 'Debug') }
        'dotnet-run'  { return @('build', $entry.target, '-c', 'Debug') }
        # These projects assert the SDK's targets during Build, so running them here would run the test twice.
        'msbuild'     { return $null }
        default       { throw "Unknown kind '$($entry.kind)' for entry '$($entry.id)'" }
    }
}

$selected = @($plan.entries | Where-Object { $_.batch -le $ThroughBatch -and (Get-Status $_ $Platform) -in @('observe', 'required') })
Write-Host "Platform=$Platform ThroughBatch=$ThroughBatch Stage=$Stage -> $($selected.Count) entries: $(($selected | ForEach-Object { $_.id }) -join ', ')"
if ($selected.Count -eq 0) { throw "The plan enables nothing for $Platform up to batch $ThroughBatch." }

$buildResults = @{}
if ($Stage -eq 'Test') {
    if (-not (Test-Path $BuildStatePath)) {
        throw "The Build stage state '$BuildStatePath' was not found. Run -Stage Build before -Stage Test."
    }
    foreach ($result in @(Get-Content $BuildStatePath -Raw | ConvertFrom-Json)) {
        $buildResults[$result.Id] = $result
    }
}

if ($Stage -in @('All', 'Build')) {
    foreach ($entry in $selected) {
        $arguments = Get-Build-Arguments $entry
        if ($null -eq $arguments) {
            Write-Host "::notice title=$($entry.id)::Build is the assertion for this MSBuild test project; it will run in the Test stage."
            $buildResults[$entry.id] = [pscustomobject]@{ Id = $entry.id; Outcome = 'not-applicable' }
            continue
        }
        Write-Host "::group::$($entry.id) [build] dotnet $($arguments -join ' ')"
        & dotnet @arguments
        $exit = $LASTEXITCODE
        Write-Host '::endgroup::'
        $outcome = if ($exit -eq 0) { 'passed' } else { 'failed' }
        $buildResults[$entry.id] = [pscustomobject]@{ Id = $entry.id; Outcome = $outcome }
        if ($exit -ne 0) {
            $status = Get-Status $entry $Platform
            $annotation = if ($status -eq 'observe') { 'warning' } else { 'error' }
            Write-Host "::$annotation title=$($entry.id) (build)::$status entry failed to build with exit code $exit; its test command will be skipped, while other areas continue."
        }
    }

    $directory = Split-Path -Parent $BuildStatePath
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    @($buildResults.Values) | ConvertTo-Json | Set-Content -Path $BuildStatePath
}

if ($Stage -eq 'Build') { exit 0 }

$results = @()
foreach ($entry in $selected) {
    $status = Get-Status $entry $Platform
    $buildResult = $buildResults[$entry.id]
    if ($null -eq $buildResult) {
        throw "The Build stage did not record an outcome for '$($entry.id)'."
    }
    if ($buildResult.Outcome -eq 'failed') {
        $annotation = if ($status -eq 'observe') { 'warning' } else { 'error' }
        Write-Host "::$annotation title=$($entry.id) (test)::$status entry was not started because its preparation failed."
        $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Outcome = 'failed'; Seconds = 0; Planned = $entry.measuredSeconds }
        continue
    }
    $arguments = Get-Command-Arguments $entry
    Write-Host "::group::$($entry.id) [$status] dotnet $($arguments -join ' ')"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Tee-Object -Variable output | Out-Host
    $exit = $LASTEXITCODE
    $watch.Stop()
    Write-Host '::endgroup::'
    $seconds = [int][math]::Round($watch.Elapsed.TotalSeconds)
    $outcome = if ($exit -eq 0) { 'passed' } else { 'failed' }
    if ($exit -ne 0 -and $status -eq 'observe') {
        Write-Host "::warning title=$($entry.id) (observe)::exit code $exit after ${seconds}s; not failing the job because the entry is only observed on $Platform."
    }
    elseif ($exit -ne 0) {
        Write-Host "::error title=$($entry.id)::exit code $exit after ${seconds}s."
    }
    $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Outcome = $outcome; Seconds = $seconds; Planned = $entry.measuredSeconds }
}

$lines = @("### Test plan on $Platform (through batch $ThroughBatch)", '', '| Entry | Mode | Result | Seconds | Planned |', '|---|---|---|---|---|')
foreach ($r in $results) { $lines += "| $($r.Id) | $($r.Status) | $($r.Outcome) | $($r.Seconds) | $($r.Planned) |" }
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

$requiredFailures = @($results | Where-Object { $_.Status -eq 'required' -and $_.Outcome -eq 'failed' })
if ($requiredFailures.Count -gt 0) {
    Write-Host "Required entries failed: $(($requiredFailures | ForEach-Object { $_.Id }) -join ', ')"
    exit 1
}
exit 0
