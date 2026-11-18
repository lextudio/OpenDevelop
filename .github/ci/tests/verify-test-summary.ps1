<#
  run-test-plan.ps1 decides a pass from xUnit's printed summary, the one place a rule is written against runner text. This
  extracts its two parsing functions from the script (it is not importable) and checks them against the recorded real
  summary format - semicolons, as in tests/TEST-RUN-2026-09-19.md - and the comma form, so a format change cannot silently
  disable the "every test skipped is a failure" rule.
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../run-test-plan.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
foreach ($name in 'Get-TestTotal', 'Get-TestSkipped', 'Test-SkipsForbidden') {
    $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $function) { throw "$name not found in run-test-plan.ps1" }
    . ([scriptblock]::Create($function.Extent.Text))
}
$entry = [pscustomobject]@{ kind = 'xunit-exe' }
$cases = @(
    @{ Text = 'Total: 249; Errors: 0; Failed: 152; Skipped: 77; Not Run: 0; Time: 72.974s'; Total = 249; Skipped = 77 },
    @{ Text = '  OpenDevelop.IntegrationTests  Total: 36, Errors: 0, Failed: 0, Skipped: 27, Not Run: 0, Time: 1.2s'; Total = 36; Skipped = 27 },
    @{ Text = "Total: 17; Errors: 0; Failed: 0; Skipped: 17`nTotal: 5; Errors: 0; Failed: 0; Skipped: 0"; Total = 5; Skipped = 0 }
)
foreach ($case in $cases) {
    $output = $case.Text -split "`n"
    $total = Get-TestTotal $entry $output; $skipped = Get-TestSkipped $entry $output
    if ($total -ne $case.Total -or $skipped -ne $case.Skipped) { throw "Parsed '$($case.Text)' as Total=$total Skipped=$skipped, expected $($case.Total)/$($case.Skipped)" }
}
$all = 'Total: 17; Errors: 0; Failed: 0; Skipped: 17; Not Run: 0' -split "`n"
if ((Get-TestSkipped $entry $all) -ne (Get-TestTotal $entry $all)) { throw 'An all-skipped summary no longer has Skipped == Total, so the gate cannot fire.' }
if ((Get-TestSkipped $entry @('no summary here')) -ne -1) { throw 'A missing summary must read -1.' }
$strict = [pscustomobject]@{ kind = 'xunit-exe'; noSkips = $true }
if (-not (Test-SkipsForbidden $strict 1)) { throw 'A noSkips entry with a skipped case must be flagged.' }
if (Test-SkipsForbidden $strict 0) { throw 'A noSkips entry with no skipped case must pass.' }
if (Test-SkipsForbidden $entry 5) { throw 'An entry without noSkips must tolerate skips.' }
if (Test-SkipsForbidden $strict -1) { throw 'A missing summary (-1) is the no-total rule, not a skip.' }
Write-Host "The test summary parsing matches xUnit's real format ($($cases.Count) summaries checked)."
