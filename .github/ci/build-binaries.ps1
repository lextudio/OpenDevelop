# Compiles, once, everything the enabled plan entries of one platform need.
#
#   pwsh .github/ci/build-binaries.ps1 -Platform windows-x64 [-ThroughBatch 3]
#
# Every distinct 'project' of an enabled entry is built (Debug).  Test jobs later run the compiled assemblies without
# restoring or building, so anything an entry runs must be produced here.  Per-project durations and the size of the
# biggest output folders go to the job summary: they are what the packaging step and the lane budgets are judged on.
param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [int]$ThroughBatch = -1,
    [string]$PlanPath = (Join-Path $PSScriptRoot 'test-plan.json')
)

$ErrorActionPreference = 'Stop'
$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
if ($ThroughBatch -lt 0) { $ThroughBatch = [int]$plan.throughBatch }

$projects = @($plan.entries |
    Where-Object { $_.batch -le $ThroughBatch -and $_.PSObject.Properties['project'] -and $_.platforms.PSObject.Properties[$Platform] -and $_.platforms.$Platform -in @('observe', 'required') } |
    ForEach-Object { $_.project } | Select-Object -Unique)
if ($projects.Count -eq 0) { throw "No project to build for $Platform up to batch $ThroughBatch." }

# The integration project's own Build deploys the add-ins, fixtures and out-of-process hosts, and builds
# many of the same libraries the unit test projects need, so build it first and let the others reuse its output.
$projects = @($projects | Sort-Object { if ($_ -like '*OpenDevelop.IntegrationTests*') { 0 } else { 1 } }, { $_ })

$rows = @()
$failed = @()
foreach ($project in $projects) {
    Write-Host "::group::build $project"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet build $project -c Debug
    $exit = $LASTEXITCODE
    $watch.Stop()
    Write-Host '::endgroup::'
    $seconds = [int][math]::Round($watch.Elapsed.TotalSeconds)
    $rows += [pscustomobject]@{ Project = $project; Seconds = $seconds; Outcome = $(if ($exit -eq 0) { 'passed' } else { 'failed' }) }
    if ($exit -ne 0) {
        $failed += $project
        Write-Host "::error title=build failed::$project exited with $exit after ${seconds}s."
    }
}

function Get-Size-Megabytes([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $bytes = (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    return [int][math]::Round($bytes / 1MB)
}

$lines = @("### Build on $Platform (through batch $ThroughBatch)", '', '| Project | Result | Seconds |', '|---|---|---|')
foreach ($r in $rows) { $lines += "| $($r.Project) | $($r.Outcome) | $($r.Seconds) |" }
$lines += @('', '| Output folder | MB |', '|---|---|')
foreach ($folder in @('AddIns', 'src/Main/SharpDevelop/bin', 'obj') + @($projects | ForEach-Object { Join-Path (Split-Path $_ -Parent) 'bin' })) {
    $size = Get-Size-Megabytes $folder
    if ($null -ne $size) { $lines += "| $folder | $size |" }
}
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY }

if ($failed.Count -gt 0) { exit 1 }
exit 0
