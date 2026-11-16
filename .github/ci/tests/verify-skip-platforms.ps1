<#
  plan-matrix.ps1 -SkipPlatforms is the switch for a day on which a runner family (macOS) is unavailable: ci.yml passes it from the
  CI_SKIP_PLATFORMS repository variable and the skip_platforms dispatch input. This checks, on the checked-in plan, that skipping
  macos-arm64 removes every macOS build and lane job and nothing else, that an empty or comma-only value skips nothing, that an
  unknown platform is an error, and that skipping a platform another kept platform builds from is refused.
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../plan-matrix.ps1'
function Get-Matrices([string]$skip) {
    $text = (& $script -SkipPlatforms $skip *>&1 | Out-String)
    $build = ([regex]::Match($text, '(?m)^build_matrix=(.+)$').Groups[1].Value | ConvertFrom-Json).include
    $lanes = ([regex]::Match($text, '(?m)^lane_matrix=(.+)$').Groups[1].Value | ConvertFrom-Json).include
    [pscustomobject]@{ Build = @($build); Lanes = @($lanes) }
}
$all = Get-Matrices ''
if (@($all.Build | Where-Object { $_.name -eq 'macos-arm64' }).Count -eq 0) { throw 'The plan has no macOS build to skip; update this check.' }
$commaOnly = Get-Matrices ',  ,'
if ($commaOnly.Build.Count -ne $all.Build.Count -or $commaOnly.Lanes.Count -ne $all.Lanes.Count) { throw 'A blank skip value must skip nothing.' }
$skipped = Get-Matrices ',macos-arm64'
if (@($skipped.Build | Where-Object { $_.name -eq 'macos-arm64' }).Count -ne 0) { throw 'macos-arm64 still has a build job.' }
if (@($skipped.Lanes | Where-Object { $_.platform -eq 'macos-arm64' }).Count -ne 0) { throw 'macos-arm64 still has a lane job.' }
$expectedBuilds = @($all.Build | Where-Object { $_.name -ne 'macos-arm64' }).Count
$expectedLanes = @($all.Lanes | Where-Object { $_.platform -ne 'macos-arm64' }).Count
if ($skipped.Build.Count -ne $expectedBuilds -or $skipped.Lanes.Count -ne $expectedLanes) { throw 'Skipping macOS changed a job of another platform.' }
foreach ($case in @(@('no-such-platform', 'Unknown platform'), @('windows-x64', 'builds from'))) {
    $message = try { & $script -SkipPlatforms $case[0] *> $null; '' } catch { $_.Exception.Message }
    if ($message -notmatch $case[1]) { throw "Skipping '$($case[0])' should fail with '$($case[1])', got: '$message'" }
}
Write-Host "Skipping macos-arm64 removes its $($all.Build.Count - $expectedBuilds) build and $($all.Lanes.Count - $expectedLanes) lane jobs and nothing else."
