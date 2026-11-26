# Dot-sourced by plan-matrix.ps1, build-binaries.ps1 and run-test-plan.ps1 so the three agree on which entries run.
#
# The plan's top-level "focus": true runs only 'observe' entries: 'required' ones have already passed twice in a row on
# that platform, so while many entries still fail they are skipped to save runner time. OD_CI_FULL=true (the workflow's
# 'full' dispatch input) runs them anyway for one run; set "focus": false to go back to full runs for good.
function Get-RunStatuses($plan) {
    $focus = $plan.PSObject.Properties['focus'] -and $plan.focus
    if ($focus -and $env:OD_CI_FULL -ne 'true') { return @('observe') }
    return @('observe', 'required')
}

# Whether $entry runs on $platform in this run. Focus mode would otherwise cover different tests on the two architectures of
# one OS: an entry 'required' on windows-x64 (skipped) but still 'observe' on windows-arm64 (run) ran on ARM64 only, so the
# x64 lane skipped debugger-journeys while ARM64 ran it. A 'required' entry therefore still runs in focus mode while another
# architecture of the same OS (the part of the platform name before '-') has it as 'observe'; other OSes do not count, or
# focus mode would save nothing. 'excluded' or a platform the entry does not list never runs.
function Test-EntryRuns($plan, $entry, [string]$platform, $runStatuses) {
    $property = $entry.platforms.PSObject.Properties[$platform]
    if ($null -eq $property) { return $false }
    $status = [string]$property.Value
    if ($status -in $runStatuses) { return $true }
    if ($status -ne 'required') { return $false }
    $os = $platform.Split('-')[0]
    return @($entry.platforms.PSObject.Properties | Where-Object { $_.Name -ne $platform -and $_.Name.Split('-')[0] -eq $os -and [string]$_.Value -eq 'observe' }).Count -gt 0
}
