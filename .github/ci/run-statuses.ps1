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
