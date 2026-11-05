<#
  GitHub Actions runners cannot run an Android emulator or an iOS Simulator reliably (no nested virtualisation on
  the macOS arm64 runners, no devices anywhere), so no test-plan entry may run a journey that needs one. Those journeys
  (AndroidDebuggingIntegrationTests, IosSimulatorDebuggingIntegrationTests, ...) are run by hand on a developer machine.

  The plan selects integration tests by class (`-class Namespace.Class`) or by method (`-method Namespace.Class.Method`),
  so this scans the integration test sources for classes that use device tooling (simctl, avdmanager, the emulator binary,
  adb) and fails when an entry would run any test of one of them. A class that mixes device and other journeys (AddInTests)
  therefore cannot be listed, not even by method, until the device journeys are moved into a class of their own. An entry
  for the integration test project must select with -class or -method (no selector, -namespace and -trait would run every
  journey of the project), and a plan without entries is an error. The
  hermetic unit tests that only parse adb/simctl text or drive a fake adb script are in OpenDevelop.Base.Tests and are fine.
#>
param(
    [string] $PlanPath = (Join-Path $PSScriptRoot '../test-plan.json'),
    [string] $TestsRoot = (Join-Path $PSScriptRoot '../../../tests/OpenDevelop.IntegrationTests')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TestsRoot -PathType Container)) { throw "Integration test sources not found: $TestsRoot" }
$deviceTooling = 'simctl|avdmanager|[/\\]emulator\b|\badb\b'
$classPattern = '(?m)^\s*(?:public|internal)\s+(?:(?:sealed|static|partial|abstract)\s+)*class\s+(\w+)'
$deviceClasses = @{}
foreach ($file in Get-ChildItem -LiteralPath $TestsRoot -Filter '*.cs' -File -Recurse) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    if ($text -notmatch $deviceTooling) { continue }
    foreach ($match in [regex]::Matches($text, $classPattern)) { $deviceClasses[$match.Groups[1].Value] = $file.Name }
}
if ($deviceClasses.Count -eq 0) { throw 'No device-journey classes were found; the scan pattern no longer matches the sources.' }

$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
$entries = @($plan.entries)
if ($entries.Count -eq 0) { throw "The test plan has no entries ($PlanPath); the guard would pass without checking anything." }
$violations = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $entries) {
    $arguments = @($entry.args)
    $selectors = 0
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        $name = [string]$arguments[$i]
        # Positive filters other than -class/-method (or none at all) would run every journey of the project.
        if ($name -in '-namespace', '-trait') { $violations.Add("entry '$($entry.id)' filters the integration tests with $name, which can run a device journey") }
        if ($name -notin '-class', '-method' -or $i -ge $arguments.Count - 1) { continue }
        $selectors++
        # '-class Namespace.Class' selects a class, '-method Namespace.Class.Method' one method of it.
        $parts = ([string]$arguments[$i + 1]).Split('.')
        $short = if ($name -eq '-class') { $parts[-1] } elseif ($parts.Count -ge 2) { $parts[-2] } else { $null }
        if ($short -and $deviceClasses.ContainsKey($short)) { $violations.Add("entry '$($entry.id)' runs $short ($($deviceClasses[$short]))") }
    }
    if (([string]$entry.project) -match 'OpenDevelop\.IntegrationTests' -and $selectors -eq 0) {
        $violations.Add("entry '$($entry.id)' runs the whole integration test project (no -class or -method), which includes the device journeys")
    }
}
if ($violations.Count -gt 0) {
    throw ("The CI test plan must not run emulator/simulator journeys:`n  " + ($violations -join "`n  "))
}
Write-Host "No test-plan entry runs a device journey ($($entries.Count) entries, $($deviceClasses.Count) device classes checked: $((($deviceClasses.Keys | Sort-Object) -join ', ')))."
