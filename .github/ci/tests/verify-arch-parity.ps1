# Both architectures of one OS must run the same entries, focus mode included, apart from entries 'excluded' (with a reason)
# on one of them. Checks Test-EntryRuns (run-statuses.ps1) on a small plan, then the checked-in plan itself.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../run-statuses.ps1')

function Get-Runs($plan, [string]$platform, $runStatuses) {
    @($plan.entries | Where-Object { Test-EntryRuns $plan $_ $platform $runStatuses } | ForEach-Object { $_.id })
}

# 1. The rule on a small plan, in focus mode.
$plan = @'
{
  "focus": true,
  "entries": [
    { "id": "mixed", "platforms": { "windows-x64": "required", "windows-arm64": "observe" } },
    { "id": "required-both", "platforms": { "windows-x64": "required", "windows-arm64": "required" } },
    { "id": "other-os-observes", "platforms": { "windows-x64": "required", "windows-arm64": "required", "linux-x64": "observe" } },
    { "id": "excluded-arm", "platforms": { "windows-x64": "observe", "windows-arm64": "excluded" } },
    { "id": "unlisted-arm", "platforms": { "windows-x64": "observe" } }
  ]
}
'@ | ConvertFrom-Json
$focus = Get-RunStatuses $plan
$x64 = Get-Runs $plan 'windows-x64' $focus
$arm = Get-Runs $plan 'windows-arm64' $focus
$expectedX64 = @('mixed', 'excluded-arm', 'unlisted-arm')
$expectedArm = @('mixed')
if (Compare-Object $x64 $expectedX64) { throw "windows-x64 runs [$($x64 -join ', ')], expected [$($expectedX64 -join ', ')]." }
if (Compare-Object $arm $expectedArm) { throw "windows-arm64 runs [$($arm -join ', ')], expected [$($expectedArm -join ', ')]." }

# 2. The checked-in plan: every architecture pair of one OS, in focus mode and in a full run.
$real = Get-Content (Join-Path $PSScriptRoot '../test-plan.json') -Raw | ConvertFrom-Json
$families = $real.platforms.PSObject.Properties.Name | Group-Object { $_.Split('-')[0] } | Where-Object Count -gt 1
$failures = @()
foreach ($statuses in @((Get-RunStatuses $real), @('observe', 'required'))) {
    foreach ($family in $families) {
        $platforms = @($family.Group)
        foreach ($a in $platforms) {
            foreach ($b in $platforms) {
                if ($a -eq $b) { continue }
                foreach ($entry in $real.entries) {
                    if ($entry.batch -gt $real.throughBatch) { continue }
                    if (-not (Test-EntryRuns $real $entry $a $statuses) -or (Test-EntryRuns $real $entry $b $statuses)) { continue }
                    $other = $entry.platforms.PSObject.Properties[$b]
                    if ($other -and [string]$other.Value -eq 'excluded' -and $entry.PSObject.Properties['reason'] -and $entry.reason) { continue }
                    $failures += "$($entry.id) runs on $a but not on $b (statuses: $($statuses -join ','); give $b a status, or 'excluded' with a reason)"
                }
            }
        }
    }
}
if ($failures.Count -gt 0) { throw "Architecture coverage differs:`n" + ($failures -join "`n") }
'Architecture parity holds.'
