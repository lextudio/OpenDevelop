#
# launch.ps1 — build the latest OpenDevelop and run it. All real logic lives here so
# Windows and macOS can share it (launch.sh is a thin wrapper that execs this file;
# a future launch.cmd can do the same on Windows).
#
# Usage:
#   ./launch.ps1                    build OpenDevelop.Mvp.sln, then run OpenDevelop
#   ./launch.ps1 -NoBuild           skip the build, just (re)run the last build output
#   ./launch.ps1 -BuildOnly         build but do NOT launch (used by rebuild-all.sh
#                                   --build-only and by the integration tests, which
#                                   start their own app instance)
#   $env:DEVFLOW_DISABLE = '1'      run without the DevFlow debugging agent
#

param(
    [switch]$NoBuild,
    [switch]$BuildOnly,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force

$sln = Join-Path $repoRoot 'OpenDevelop.Mvp.slnx'
$exeProject = Join-Path $repoRoot 'src/Main/SharpDevelop/SharpDevelop.csproj'

# OpenDevelop and LibreWPF both target net10.0/net10.0-windows now, so the system
# .NET 10 SDK builds and runs the app.
$dotnet = Find-DotNetHost

if (-not $NoBuild) {
    Clear-RepoAddIns -RepoRoot $repoRoot

    Restore-Solution -DotNet $dotnet -Solution $sln

    # Build the app project first so OpenDevelop.base.manifest exists before any addin's
    # post-Build trim runs (doc/technotes/addin-sdk.md); otherwise the very first build after
    # wiping AddIns/ fails open and re-emits every base-provided assembly.
    Write-Host '==> Building host app (base manifest source)...'
    Invoke-Native $dotnet build $exeProject --no-restore -v minimal

    Build-Solution -DotNet $dotnet -Solution $sln

    Remove-StaleMsBuildAssets -RepoRoot $repoRoot -Configuration $Configuration
}
else {
    Write-Host '==> Skipping build (-NoBuild).'
}

if ($BuildOnly) {
    Write-Host '==> Build only (-BuildOnly); not launching.'
    exit 0
}

# MSBuildSDKsPath and related overrides are only needed for SharpDevelop's in-process
# MSBuild hosting - they interfere with `dotnet build` (which respects global.json), so
# apply them only right before launching.
Set-DotNetEnv -DotNetHost $dotnet

Write-Host '==> Launching OpenDevelop...'
& $dotnet run --project $exeProject --no-build
exit $LASTEXITCODE
