# Runs the entries of .github/ci/test-plan.json that are enabled for one platform.
#
#   pwsh .github/ci/run-test-plan.ps1 -Platform windows-x64 [-ThroughBatch 2]
#
# An entry runs when its batch is <= the through-batch and its status for the platform is 'observe' or
# 'required'.  A failing 'required' entry fails the script (exit 1); a failing 'observe' entry is only
# reported (a ::warning:: annotation and a row in the job summary).  Every entry's duration is recorded
# in the job summary so the plan's measuredSeconds can be kept honest.
param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [int]$ThroughBatch = -1,
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
        'dotnet-test' { return @('test', '--project', $entry.target, '-c', 'Debug') }
        'dotnet-run'  { return @('run', '--project', $entry.target, '-c', 'Debug') }
        'msbuild'     { return @('msbuild', $entry.target, '-t:Build') }
        default       { throw "Unknown kind '$($entry.kind)' for entry '$($entry.id)'" }
    }
}

$selected = @($plan.entries | Where-Object { $_.batch -le $ThroughBatch -and (Get-Status $_ $Platform) -in @('observe', 'required') })
Write-Host "Platform=$Platform ThroughBatch=$ThroughBatch -> $($selected.Count) entries: $(($selected | ForEach-Object { $_.id }) -join ', ')"
if ($selected.Count -eq 0) { throw "The plan enables nothing for $Platform up to batch $ThroughBatch." }

$results = @()
foreach ($entry in $selected) {
    $status = Get-Status $entry $Platform
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
