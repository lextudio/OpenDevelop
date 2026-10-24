# Runs the entries of one lane of .github/ci/test-plan.json on one platform, from already compiled binaries.
#
#   pwsh .github/ci/run-test-plan.ps1 -Platform windows-x64 -Lane unit [-ThroughBatch 3]
#
# An entry runs when its batch is <= the through-batch and its status for the platform is 'observe' or
# 'required'.  A failing 'required' entry fails the script (exit 1); a failing 'observe' entry is only reported
# (a ::warning:: annotation and a row in the job summary).  Nothing is restored or built here: the build job compiled
# everything once and the test job unpacked that workspace, so an entry runs its test assembly directly
# (xunit-exe: dotnet <target> [args]) or, for the AddIn SDK assertion projects, the MSBuild project itself (msbuild).
# Every entry's duration lands in the job summary so the plan's measuredSeconds can be kept honest.
param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [Parameter(Mandatory = $true)][string]$Lane,
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
        'xunit-exe' {
            $arguments = @($entry.target)
            if ($entry.PSObject.Properties['args']) { $arguments += @($entry.args) }
            return [string[]]$arguments
        }
        'msbuild' { return [string[]]@('msbuild', $entry.target, '-t:Build') }
        default   { throw "Unknown kind '$($entry.kind)' for entry '$($entry.id)'" }
    }
}

$selected = @($plan.entries | Where-Object {
    $_.batch -le $ThroughBatch -and $_.lane -eq $Lane -and (Get-Status $_ $Platform) -in @('observe', 'required')
})
Write-Host "Platform=$Platform Lane=$Lane ThroughBatch=$ThroughBatch -> $($selected.Count) entries: $(($selected | ForEach-Object { $_.id }) -join ', ')"
if ($selected.Count -eq 0) { throw "The plan enables nothing in lane '$Lane' for $Platform up to batch $ThroughBatch." }

$results = @()
foreach ($entry in $selected) {
    $status = Get-Status $entry $Platform
    # Typed, because a function's one-element array result is unrolled to a string and would be splatted as characters.
    [string[]]$arguments = Get-Command-Arguments $entry
    $target = if ($entry.kind -eq 'xunit-exe') { $entry.target } else { $null }
    if ($target -and -not (Test-Path $target)) {
        # The build job should have produced it; say so plainly instead of letting dotnet print a vague error.
        Write-Host "::error title=$($entry.id)::The test assembly '$target' does not exist; the build job did not produce it."
        $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Outcome = 'failed'; Seconds = 0; Planned = $entry.measuredSeconds }
        continue
    }
    Write-Host "::group::$($entry.id) [$status] dotnet $($arguments -join ' ')"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Out-Host
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

$lines = @("### $Lane on $Platform (through batch $ThroughBatch)", '', '| Entry | Mode | Result | Seconds | Planned |', '|---|---|---|---|---|')
foreach ($r in $results) { $lines += "| $($r.Id) | $($r.Status) | $($r.Outcome) | $($r.Seconds) | $($r.Planned) |" }
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

$requiredFailures = @($results | Where-Object { $_.Status -eq 'required' -and $_.Outcome -eq 'failed' })
if ($requiredFailures.Count -gt 0) {
    Write-Host "Required entries failed: $(($requiredFailures | ForEach-Object { $_.Id }) -join ', ')"
    exit 1
}
exit 0
