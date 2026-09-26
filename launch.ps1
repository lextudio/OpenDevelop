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
    # Reassert the transport payload, but never DOWNGRADE an assembly restore already placed at a
    # higher version. System.Private.Windows.Core is the case that matters: LibreWinForms'
    # System.Windows.Forms is built against v11 while LibreWPF's PresentationCore is built against
    # v10, and both packages ship a copy. NuGet resolves the conflict in favour of v11 (it logs
    # MSB3243 "Choosing 11.0.0.0"); copying the transport payload over it unconditionally put v10
    # back and the app died at startup with
    # "Could not load file or assembly 'System.Private.Windows.Core, Version=11.0.0.0'".
    # Assembly binding rolls FORWARD but not back, so keeping the higher version satisfies both
    # references. A file that is not a managed assembly (or that is absent) just gets copied.
    Get-ChildItem -LiteralPath $managedDir -Filter '*.dll' -File | ForEach-Object {
        $destination = Join-Path $outputDir $_.Name
        if (Test-Path -LiteralPath $destination) {
            $sourceVersion = $null
            $destinationVersion = $null
            try { $sourceVersion = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version } catch { }
            try { $destinationVersion = [System.Reflection.AssemblyName]::GetAssemblyName($destination).Version } catch { }
            if ($sourceVersion -and $destinationVersion -and $destinationVersion -gt $sourceVersion) {
                Write-Host "    keeping $($_.Name) $destinationVersion (transport ships $sourceVersion)"
                return
            }
        }
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
    # The RID-specific copies are installed last, on purpose. They are also the easiest place for
    # the payload to go stale: runtimes/<rid>/ is filled from LibreWPF's artifacts/windows-managed-
    # runtime, which only eng/progpu-wpf-windows-managed-runtime.ps1 produces - and that script
    # refuses to run when the repo's .dotnet host is not x64, so on an ARM64 workstation it quietly
    # stops being regenerated. Installing a months-old PresentationCore over the freshly built one
    # paired it with a newer PresentationFramework and broke every TextBox with
    # "MissingMethodException: InputManager.get_UsesPortableInput()".
    # Only take the RID copy when it is not older than the RID-neutral one it would replace.
    foreach ($assembly in 'PresentationCore.dll', 'DirectWriteForwarder.dll') {
        $source = Join-Path $runtimeDir $assembly
        if (-not (Test-Path -LiteralPath $source)) {
            throw "LibreWPF runtime assembly not found: $source"
        }
        $neutral = Join-Path $managedDir $assembly
        if (Test-Path -LiteralPath $neutral) {
            $ridStamp = (Get-Item -LiteralPath $source).LastWriteTimeUtc
            $neutralStamp = (Get-Item -LiteralPath $neutral).LastWriteTimeUtc
            if ($neutralStamp -gt $ridStamp) {
                Write-Host "    keeping RID-neutral $assembly ($neutralStamp) over stale $rid copy ($ridStamp)"
                Copy-Item -LiteralPath $neutral -Destination (Join-Path $outputDir $assembly) -Force
                continue
            }
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

    # OpenDevelop.Mvp.slnx never references FormsDesigner/MicrosoftHost or
    # WinUIXamlDesigner.MicrosoftHost - the real Microsoft-framework designer backends - so
    # Build-Solution above never builds them. WinUIXamlDesigner.MicrosoftHost specifically CANNOT
    # be built by `dotnet build` at all (UseWinUI pulls in MrtCore.PriGen.targets, which ships only
    # with Visual Studio - MSB4062 otherwise), so it must go through Find-VsMsBuild here, exactly
    # like dist.ps1's designer-hosts phase. Skipping this silently leaves AddIns/.../MicrosoftHost
    # missing and every WinUI XAML file's Design view falls back to "WinUI 3 runtime host is not
    # installed." - the previous behavior of this script.
    if ($IsWindows) {
        Write-Host '==> Building Microsoft-framework designer hosts (VS MSBuild)...'
        Build-MicrosoftDesignerHosts -RepoRoot $repoRoot -Configuration $Configuration `
            -PinnedGitVersionProperties (Get-PinnedGitVersionProperties -GlobalAssemblyInfoPath (Join-Path $repoRoot 'src/Main/GlobalAssemblyInfo.cs'))
    }

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
