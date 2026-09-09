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
    [string]$Configuration = 'Debug',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$OpenFiles
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force

$sln = Join-Path $repoRoot 'OpenDevelop.Mvp.slnx'
$exeProject = Join-Path $repoRoot 'src/Main/SharpDevelop/SharpDevelop.csproj'

# OpenDevelop and LibreWPF both target net10.0/net10.0-windows now, so the system
# .NET 10 SDK builds and runs the app.
$dotnet = Find-DotNetHost

function Sync-LibreWpfDevelopmentRuntime {
    # The host is built before the rest of the solution so its base manifest exists for add-ins.
    # Some later add-in builds can then copy their WPF reference surface back to the host output.
    # Reapply the restore-selected transport payload only after the entire build has completed.
    # The project itself owns the normal per-build copying; this is the cross-project finalization.
    if (-not $IsWindows) { return }

    $assets = Join-Path $repoRoot 'src/Main/SharpDevelop/obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assets)) {
        throw "LibreWPF runtime sync requires restore assets: $assets"
    }

    $libraries = (Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json).libraries.PSObject.Properties
    $transportVersion = ($libraries | Where-Object { $_.Name -like 'LibreWPF.Transport/*' } |
        Select-Object -First 1).Name -replace '^LibreWPF.Transport/', ''
    if (-not $transportVersion) {
        throw 'LibreWPF.Transport was not resolved for the OpenDevelop host.'
    }

    $rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'Arm64' { 'win-arm64' }
        'X64' { 'win-x64' }
        'X86' { 'win-x86' }
        default { throw "Unsupported Windows process architecture: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)" }
    }
    $packageRoot = ((& $dotnet nuget locals global-packages --list) |
        Select-String '^global-packages: ').Line -replace '^global-packages:\s*', ''
    $transportDir = Join-Path $packageRoot "librewpf.transport/$transportVersion"
    $managedDir = Join-Path $transportDir 'lib/net10.0'
    $runtimeDir = Join-Path $transportDir "runtimes/$rid/lib/net10.0"
    $outputDir = Join-Path $repoRoot "src/Main/SharpDevelop/bin/$Configuration/net10.0-windows"
    Get-ChildItem -LiteralPath $managedDir -Filter '*.dll' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $outputDir $_.Name) -Force
    }
    foreach ($assembly in 'PresentationCore.dll', 'DirectWriteForwarder.dll') {
        $source = Join-Path $runtimeDir $assembly
        if (-not (Test-Path -LiteralPath $source)) {
            throw "LibreWPF runtime assembly not found: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $outputDir $assembly) -Force
    }
}

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

Sync-LibreWpfDevelopmentRuntime

if ($BuildOnly) {
    Write-Host '==> Build only (-BuildOnly); not launching.'
    exit 0
}

# MSBuildSDKsPath and related overrides are only needed for SharpDevelop's in-process
# MSBuild hosting - they interfere with `dotnet build` (which respects global.json), so
# apply them only right before launching.
Set-DotNetEnv -DotNetHost $dotnet

Write-Host '==> Launching OpenDevelop...'
& $dotnet run --project $exeProject --no-build -- @OpenFiles
exit $LASTEXITCODE
