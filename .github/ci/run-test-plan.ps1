# Runs the entries of one lane of .github/ci/test-plan.json on one platform, from already compiled binaries.
#
#   pwsh .github/ci/run-test-plan.ps1 -Platform windows-x64 -Lane unit [-ThroughBatch 3]
#
# An entry runs when its batch is <= the through-batch and its status for the platform is 'observe' or 'required'.
# A failing 'required' entry fails the script (exit 1); a failing 'observe' entry is only reported (a ::warning::
# annotation and a row in the job summary). Nothing is restored or built here: the build job compiled everything once and
# the test job unpacked that workspace, or, for a lane with 'buildsInLane', the job built it before calling this script.
#
# Three rules keep a green lane meaningful:
#   * an 'xunit-exe' entry that exits 0 but reports no test total, or a total of 0, is a failure - a filter that matches
#     nothing, or a test assembly that never ran, is not a pass;
#   * a lane in which every entry failed fails the lane even when every entry is only 'observe' - the job would otherwise
#     be green while it exercised nothing;
#   * an entry whose test assembly was never produced fails, so a missing build step shows up as itself.
# Every entry's duration and test count land in the job summary so the plan's measuredSeconds can be kept honest.
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

# xUnit v3 prints one "Total: N, Errors: ..." line per assembly; take the last one so a per-class summary cannot win.
function Get-TestTotal($entry, [string[]]$output) {
    if ($entry.kind -ne 'xunit-exe') { return -1 }
    $text = (@($output) | Select-Object -Last 400) -join "`n"
    $matches = [regex]::Matches($text, 'Total:\s*(\d+)')
    if ($matches.Count -eq 0) { return -1 }
    return [int]$matches[$matches.Count - 1].Groups[1].Value
}

# An entry's budget: its own timeoutMinutes, else the lane's entryTimeoutMinutes, else none (0).
function Get-EntryTimeoutSeconds($entry, $laneDefinition) {
    if ($entry.PSObject.Properties['timeoutMinutes']) { return [int]([double]$entry.timeoutMinutes * 60) }
    if ($laneDefinition -and $laneDefinition.PSObject.Properties['entryTimeoutMinutes']) { return [int]([double]$laneDefinition.entryTimeoutMinutes * 60) }
    return 0
}

# Prints complete lines appended to a log file since the last call; keeps a trailing partial line for the next call.
function Read-NewLines($state, [System.Collections.Generic.List[string]]$lines, [switch]$Final) {
    $state.Text += $state.Reader.ReadToEnd()
    $parts = $state.Text -split "`r?`n"
    $complete = if ($Final) { $parts.Count } else { $parts.Count - 1 }
    for ($i = 0; $i -lt $complete; $i++) {
        if ($Final -and $i -eq $parts.Count - 1 -and $parts[$i] -eq '') { break }
        Write-Host $parts[$i]
        $lines.Add($parts[$i])
    }
    $state.Text = if ($Final) { '' } else { $parts[$parts.Count - 1] }
}

# Runs 'dotnet <arguments>' printing its output as it arrives and, past the budget, kills the whole process tree (the
# fixture's IDE included). '& dotnet' printed nothing until exit, so a hang ended in a lane timeout that cancelled the job
# and lost every result; this way a hung entry costs only its budget and the next entry still runs. .NET copies the two
# streams to files itself (no PowerShell callback runs off the pipeline thread), and this loop tails them.
function Invoke-Entry([string[]]$arguments, [int]$timeoutSeconds, [string]$logBase) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $sinks = @(); $copies = @(); $states = @()
    foreach ($pair in @(@($process.StandardOutput, "$logBase.out.log"), @($process.StandardError, "$logBase.err.log"))) {
        $sink = [System.IO.FileStream]::new($pair[1], 'Create', 'Write', 'ReadWrite')
        $sinks += $sink
        $copies += $pair[0].BaseStream.CopyToAsync($sink)
        $states += [pscustomobject]@{ Reader = [System.IO.StreamReader]::new([System.IO.FileStream]::new($pair[1], 'Open', 'Read', 'ReadWrite')); Text = '' }
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    $deadline = if ($timeoutSeconds -gt 0) { [DateTime]::UtcNow.AddSeconds($timeoutSeconds) } else { [DateTime]::MaxValue }
    $timedOut = $false
    while (-not $process.WaitForExit(2000)) {
        foreach ($sink in $sinks) { $sink.Flush() }
        foreach ($state in $states) { Read-NewLines $state $lines }
        if ([DateTime]::UtcNow -gt $deadline) {
            $timedOut = $true
            Write-Host "::warning::Budget of ${timeoutSeconds}s exceeded; killing the process tree."
            try { $process.Kill($true) } catch { Write-Host "Kill failed: $($_.Exception.Message)" }
            $null = $process.WaitForExit(30000)
            break
        }
    }
    $null = [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$copies, 30000)
    foreach ($sink in $sinks) { $sink.Flush(); $sink.Dispose() }
    foreach ($state in $states) { Read-NewLines $state $lines -Final; $state.Reader.Dispose() }
    $exitCode = if ($timedOut -or -not $process.HasExited) { -1 } else { $process.ExitCode }
    return [pscustomobject]@{ ExitCode = $exitCode; TimedOut = $timedOut; Output = $lines.ToArray() }
}

# xUnit v3 writes a TRX per assembly with '-trx <file>'; the workflow uploads the folder and renders it as a check.
$resultsDirectory = if ($env:OD_TEST_RESULTS) { $env:OD_TEST_RESULTS } else { 'TestResults' }
New-Item -ItemType Directory -Force -Path $resultsDirectory | Out-Null
$laneDefinition = $plan.lanes.PSObject.Properties[$Lane]
$laneDefinition = if ($laneDefinition) { $laneDefinition.Value } else { $null }

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
    if ($entry.kind -eq 'xunit-exe') { $arguments += @('-trx', (Join-Path $resultsDirectory "$($entry.id).trx")) }
    $target = if ($entry.kind -eq 'xunit-exe') { $entry.target } else { $null }
    if ($target -and -not (Test-Path $target)) {
        # The build job should have produced it; say so plainly instead of letting dotnet print a vague error.
        Write-Host "::error title=$($entry.id)::The test assembly '$target' does not exist; the build job did not produce it."
        $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Outcome = 'failed'; Seconds = 0; Tests = 0; Planned = $entry.measuredSeconds; Note = 'assembly missing' }
        continue
    }
    $timeoutSeconds = Get-EntryTimeoutSeconds $entry $laneDefinition
    $budget = if ($timeoutSeconds -gt 0) { " (budget ${timeoutSeconds}s)" } else { '' }
    Write-Host "::group::$($entry.id) [$status]$budget dotnet $($arguments -join ' ')"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $run = Invoke-Entry $arguments $timeoutSeconds (Join-Path $resultsDirectory $entry.id)
    $exit = $run.ExitCode
    $output = $run.Output
    $watch.Stop()
    Write-Host '::endgroup::'

    $seconds = [int][math]::Round($watch.Elapsed.TotalSeconds)
    $tests = Get-TestTotal $entry $output
    $note = ''
    $outcome = if ($exit -eq 0) { 'passed' } else { 'failed' }
    if ($run.TimedOut) {
        $outcome = 'timeout'
        $note = "killed after ${timeoutSeconds}s"
        $level = if ($status -eq 'required') { 'error' } else { 'warning' }
        Write-Host "::$($level) title=$($entry.id) (${status})::exceeded its ${timeoutSeconds}s budget and was killed; its output up to then is above and in $($entry.id).out.log."
    }
    elseif ($exit -eq 0 -and $entry.kind -eq 'xunit-exe' -and $tests -le 0) {
        $outcome = 'failed'
        $note = if ($tests -lt 0) { 'no test total' } else { '0 tests' }
        $level = if ($status -eq 'required') { 'error' } else { 'warning' }
        Write-Host "::$($level) title=$($entry.id) (${status})::The run exited 0 but exercised nothing ($(if ($tests -lt 0) { 'it printed no test total' } else { 'it ran 0 tests' }))."
    }
    elseif ($exit -ne 0 -and $status -eq 'observe') {
        Write-Host "::warning title=$($entry.id) (observe)::exit code $exit after ${seconds}s; not failing the job because the entry is only observed on $Platform."
    }
    elseif ($exit -ne 0) {
        Write-Host "::error title=$($entry.id)::exit code $exit after ${seconds}s."
    }
    $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Outcome = $outcome; Seconds = $seconds; Tests = $tests; Planned = $entry.measuredSeconds; Note = $note }
}

function Format-Tests($tests) { if ($tests -lt 0) { '-' } else { $tests } }

$lines = @("### $Lane on $Platform (through batch $ThroughBatch)", '', '| Entry | Mode | Result | Seconds | Tests | Planned | Note |', '|---|---|---|---|---|---|---|')
foreach ($r in $results) { $lines += "| $($r.Id) | $($r.Status) | $($r.Outcome) | $($r.Seconds) | $(Format-Tests $r.Tests) | $($r.Planned) | $($r.Note) |" }
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

$requiredFailures = @($results | Where-Object { $_.Status -eq 'required' -and $_.Outcome -ne 'passed' })
if ($requiredFailures.Count -gt 0) {
    Write-Host "Required entries failed: $(($requiredFailures | ForEach-Object { $_.Id }) -join ', ')"
    exit 1
}
$passed = @($results | Where-Object { $_.Outcome -eq 'passed' })
# A lane whose definition says 'allowAllFailures' is known broken for one tracked reason (see the plan); its failures stay
# visible as warnings and in the summary, but they do not paint master red while the fix is not ours.
$allowAllFailures = [bool]($plan.lanes.PSObject.Properties[$Lane].Value.PSObject.Properties['allowAllFailures'] -and $plan.lanes.PSObject.Properties[$Lane].Value.allowAllFailures)
if ($results.Count -gt 0 -and $passed.Count -eq 0 -and -not $allowAllFailures) {
    Write-Host "::error title=$Lane::$($results.Count) entries ran and none passed; a lane that exercises nothing is not a pass, whatever its modes say."
    exit 1
}
exit 0
