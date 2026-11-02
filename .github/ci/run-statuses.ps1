# Dot-sourced by plan-matrix.ps1, build-binaries.ps1 and run-test-plan.ps1 so the three agree on which entries run and
# where they are compiled.
#
# The plan's top-level "focus": true runs only 'observe' entries: 'required' ones have already passed twice in a row on
# that platform, so while many entries still fail they are skipped to save runner time. OD_CI_FULL=true (the workflow's
# 'full' dispatch input) runs them anyway for one run; set "focus": false to go back to full runs for good.
function Get-RunStatuses($plan) {
    $focus = $plan.PSObject.Properties['focus'] -and $plan.focus
    if ($focus -and $env:OD_CI_FULL -ne 'true') { return @('observe') }
    return @('observe', 'required')
}

# A platform that builds for itself runs a 'buildsInLane' lane in place (its own checkout and compile, so the result
# arrives early). A platform with 'buildFrom' ships its provider's build (Windows ARM64 ships the payload compiled on
# Windows x64), so it runs that lane from the provider's archive instead: every lane tests the build its platform ships.
function Test-BuildsInPlace($plan, [string]$laneName, [string]$platformName) {
    $lane = $plan.lanes.PSObject.Properties[$laneName]
    if ($null -eq $lane -or -not ($lane.Value.PSObject.Properties['buildsInLane'] -and $lane.Value.buildsInLane)) { return $false }
    $platform = $plan.platforms.PSObject.Properties[$platformName]
    return -not ($platform -and $platform.Value.PSObject.Properties['buildFrom'] -and $platform.Value.buildFrom)
}
