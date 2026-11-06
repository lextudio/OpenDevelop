<#
  A device journey (a real Android emulator or an iOS Simulator) may run in CI only where a runner hosts the device:
  an iOS Simulator is a native macOS process and needs no virtualisation, while an Android emulator needs KVM, which
  the hosted Linux x86_64 runners provide and the macOS arm64 and ubuntu-24.04-arm images do not. So a lane declares
  the device classes it may run together with the OS family whose runner hosts the device:

      "deviceClasses": { "AndroidDebuggingIntegrationTests": "linux" }

  and this guard fails the plan job when an entry runs a device class from a lane that does not declare it, when the
  runner the lane's job would use (the lane's own 'runner', else the platform's) is not of the declared family, or when
  a declared class name does not exist in the sources.

  The plan selects integration tests by class (`-class Namespace.Class`) or by method (`-method Namespace.Class.Method`),
  so this scans the integration test sources for classes that use device tooling (simctl, avdmanager, the emulator binary,
  adb) and fails when an entry would run any test of one of them without a declaration. A class that mixes device and
  other journeys (AddInTests) therefore cannot be listed, not even by method, until the device journeys are moved into a
  class of their own. An entry for the integration test project must select with -class or -method (no selector,
  -namespace and -trait would run every journey of the project), and a plan without entries is an error. The
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

# The OS family of a runner image label: macos-26 and xcode-27 are macOS, ubuntu-24.04 is Linux, windows-2025 is Windows.
function Get-RunnerFamily([string]$runner) {
    if ($runner -match '^(macos|xcode)') { return 'macos' }
    if ($runner -match '^(ubuntu|linux)') { return 'linux' }
    if ($runner -match '^windows') { return 'windows' }
    throw "Cannot tell which OS family the runner '$runner' belongs to; teach this guard about it."
}

$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
$entries = @($plan.entries)
if ($entries.Count -eq 0) { throw "The test plan has no entries ($PlanPath); the guard would pass without checking anything." }
$violations = [System.Collections.Generic.List[string]]::new()

# A lane may only declare device classes the scan found, so a rename cannot silently allow a different class.
foreach ($laneProperty in $plan.lanes.PSObject.Properties) {
    if (-not $laneProperty.Value.PSObject.Properties['deviceClasses']) { continue }
    foreach ($declaredProperty in $laneProperty.Value.deviceClasses.PSObject.Properties) {
        if (-not $deviceClasses.ContainsKey($declaredProperty.Name)) {
            $violations.Add("lane '$($laneProperty.Name)' declares device class '$($declaredProperty.Name)', which the scan of the integration test sources did not find")
        }
    }
}

$deviceLanes = @()
foreach ($entry in $entries) {
    $laneProperty = $plan.lanes.PSObject.Properties[[string]$entry.lane]
    if ($null -eq $laneProperty) {
        $violations.Add("entry '$($entry.id)' names lane '$($entry.lane)', which the plan does not define")
        continue
    }
    $declared = @{}
    if ($laneProperty.Value.PSObject.Properties['deviceClasses']) {
        foreach ($property in $laneProperty.Value.deviceClasses.PSObject.Properties) { $declared[$property.Name] = [string]$property.Value }
    }
    if ($declared.Count -gt 0 -and $deviceLanes -notcontains $entry.lane) { $deviceLanes += $entry.lane }
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
        if (-not $short -or -not $deviceClasses.ContainsKey($short)) { continue }
        if (-not $declared.ContainsKey($short)) {
            $violations.Add("entry '$($entry.id)' runs $short ($($deviceClasses[$short])) in lane '$($entry.lane)', whose deviceClasses does not declare it")
            continue
        }
        # The job runs on the lane's runner when it names one, else on the platform's. Both must host the device.
        $family = $declared[$short]
        if ($family -notin 'macos', 'linux', 'windows') {
            $violations.Add("lane '$($entry.lane)' declares $short for unknown OS family '$family' (use macos, linux or windows)")
            continue
        }
        foreach ($platformProperty in $entry.platforms.PSObject.Properties) {
            $platform = $plan.platforms.PSObject.Properties[$platformProperty.Name]
            if ($null -eq $platform) {
                $violations.Add("entry '$($entry.id)' enables undefined platform '$($platformProperty.Name)'")
                continue
            }
            $runner = if ($laneProperty.Value.PSObject.Properties['runner'] -and $laneProperty.Value.runner) { [string]$laneProperty.Value.runner } else { [string]$platform.Value.runner }
            $actual = Get-RunnerFamily $runner
            if ($actual -ne $family) {
                $violations.Add("entry '$($entry.id)' runs $short on $($platformProperty.Name), whose job runs on '$runner' ($actual), but the lane declares $family")
            }
        }
    }
    if (([string]$entry.project) -match 'OpenDevelop\.IntegrationTests' -and $selectors -eq 0) {
        $violations.Add("entry '$($entry.id)' runs the whole integration test project (no -class or -method), which includes the device journeys")
    }
}
if ($violations.Count -gt 0) {
    throw ("The CI test plan runs a device journey where no runner hosts its device:`n  " + ($violations -join "`n  "))
}
$laneList = if ($deviceLanes.Count -gt 0) { $deviceLanes -join ', ' } else { 'none' }
Write-Host "Device journeys stay in their declared lanes ($laneList); $($entries.Count) entries checked, $($deviceClasses.Count) device classes found: $((($deviceClasses.Keys | Sort-Object) -join ', '))."
