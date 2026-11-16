# Decides whether a ci.yml run may gate a release, and writes the marker file that the release-grade job uploads.
# release.yml accepts a commit only when one of its CI runs carries that artifact.  A green run alone proves too little:
# the plan's "focus" mode skips every 'required' entry, and a dispatch narrowed by through_batch/platforms runs a subset. Skipping platforms (CI_SKIP_PLATFORMS, skip_platforms) does not disqualify the run, but the marker records them.
#
# Reads $env:NEEDS (toJSON(needs) of the release-grade job) and $env:NARROWED ('true' for a narrowed dispatch);
# $env:OD_CI_FULL is the workflow's 'full' input.  Writes the file only when the run qualifies, and always explains why in
# the job summary.  It never fails the job: the run's own conclusion already carries the failures.
param([Parameter(Mandatory)][string]$Output)

$ErrorActionPreference = 'Stop'
$needs = $env:NEEDS | ConvertFrom-Json
$plan = Get-Content (Join-Path $PSScriptRoot 'test-plan.json') -Raw | ConvertFrom-Json

$problems = @()
if ($env:NARROWED -eq 'true') { $problems += 'a dispatch narrowed by through_batch or platforms runs only a subset' }
$focus = $plan.PSObject.Properties['focus'] -and $plan.focus
if ($focus -and $env:OD_CI_FULL -ne 'true') { $problems += 'the plan is in focus mode and this run did not set full=true, so the required entries were skipped' }

# plan and build must have run and passed. The lane jobs may be skipped (no lane scheduled), never failed.
foreach ($job in 'plan', 'build') {
    $result = $needs.$job.result
    if ($result -ne 'success') { $problems += "$job finished as '$result'" }
}
foreach ($job in 'test') {
    $result = $needs.$job.result
    if ($result -notin 'success', 'skipped') { $problems += "$job finished as '$result'" }
}

$summary = $env:GITHUB_STEP_SUMMARY
if ($problems.Count -eq 0) {
    # The second line names the platforms this run skipped (CI_SKIP_PLATFORMS / skip_platforms); release.yml accepts the run only
    # for a release that skips at least those, so a macOS-free CI run cannot vouch for a release that ships a macOS build.
    $skipped = @($env:SKIPPED -split '[,\s]+' | Where-Object { $_ } | Sort-Object -Unique) -join ','
    @("release-grade $env:GITHUB_SHA run $env:GITHUB_RUN_ID", "skipped: $skipped") | Set-Content $Output
    $executed = if ($skipped) { "the full plan except the skipped platform(s) $skipped (it can gate only a release that skips them too)" } else { 'the full plan' }
    if ($summary) { "### Release grade: yes`nThis run executed $executed on this commit." | Add-Content $summary }
    'release grade: yes'
} else {
    if ($summary) { "### Release grade: no`n$(($problems | ForEach-Object { "- $_" }) -join "`n")" | Add-Content $summary }
    "release grade: no`n$($problems -join "`n")"
}
