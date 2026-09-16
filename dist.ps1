#!/usr/bin/env pwsh
#
# dist.ps1 — build a framework-dependent OpenDevelop distribution for local testing.
# Mirrors the CI steps in .github/workflows/package.yml. Replaces the former dist.macos.ps1 and
# dist.windows.ps1, which were ~85% identical and drifted apart every time one side was touched.
#
# The whole publish/patch/AddIn-build pipeline is platform-neutral and lives here once. Only the
# final packaging stage differs, and it is isolated in Invoke-MacPackaging / Invoke-WindowsPackaging
# at the bottom:
#
#   macOS   -> OpenDevelop.app bundle, then OpenDevelop-macos.dmg
#   Windows -> one OpenDevelop-win-<rid>/ payload directory and OpenDevelop-windows-<rid>.zip PER
#              RuntimeIdentifier (see -RuntimeIdentifiers below)
#
# Note the app runs on LibreWPF (portable WPF) on Windows too — SharpDevelop.csproj strips the
# Microsoft.WindowsDesktop.App runtime framework on EVERY platform — which is why the LibreWinForms
# deps.json patch and the LibreWPF.Transport overlay below are not macOS-specific. The Win32
# compatibility shims (kernel32.dll, user32.dll, ...) the macOS bundle carries are emitted by
# LibreWPF.Sdk only on OSX/Linux and must NOT appear on Windows, where those names belong to the
# real OS DLLs; that split is handled by OpenDevelopDistributionRidFamily, not here.
#
# Windows: one package per architecture, not "Any CPU". ProGPU/LibreWPF ship real native
# libraries (glfw3.dll, wgpu_native.dll, vcruntime140_cor3.dll, DirectX/Vulkan interop shims, ...),
# and a native DLL is inherently architecture-specific - there is no "Any CPU" for P/Invoke'd code.
# Without an explicit -p:RuntimeIdentifier, the SDK implicitly resolves native assets for whatever
# architecture the machine RUNNING this script happens to be (dotnet --info's own RID), which is
# how a previous run on an ARM64 dev machine silently produced only a win-arm64 package. Passing
# -RuntimeIdentifiers explicitly (default: both) makes the output deterministic regardless of the
# build machine's own architecture, and produces a correct package for each target.
#
# Usage: ./dist.ps1 [-SkipPublish] [-Configuration Debug|Release] [-RuntimeIdentifiers win-x64,win-arm64]
#   -SkipPublish            reuse existing publish output (faster iteration on packaging)
#   -Configuration Debug    package the Debug configuration instead of Release.
#   -RuntimeIdentifiers     Windows only; which architecture(s) to build. Defaults to both
#                           win-x64 and win-arm64, producing one zip per RID. Ignored on macOS
#                           (Invoke-MacPackaging always builds for the host's own architecture).
#
# On macOS this is normally reached through ./dist.macos.sh, which only locates pwsh.
#

[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force

$config = $Configuration
$tfm = 'net10.0-windows'
$sln = Join-Path $repoRoot 'OpenDevelop.Mvp.slnx'
$hostProject = Join-Path $repoRoot 'src/Main/SharpDevelop/SharpDevelop.csproj'
$dotnet = Find-DotNetHost
$patchScript = Join-Path $repoRoot 'build/patch-librewinforms-deps.ps1'

# The Addin SDK's OpenDevelopPruneAddinDeploymentAssets target drops runtimes/win*, linux* and
# unix* only for the 'osx' family; on Windows those win* assets are exactly what the payload needs.
# This is an OS-family switch, unrelated to $RuntimeIdentifiers (OS+architecture) below - both
# win-x64 and win-arm64 use ridFamily='win'.
$ridFamily = if ($IsWindows) { 'win' } else { 'osx' }

# macOS keeps building for the host's own architecture only (Invoke-MacPackaging), a single pass
# with no RID suffix on the publish directory - $null here means "don't pass -p:RuntimeIdentifier".
#
# Built as an explicit statement, NOT "$ridsToBuild = if (...) { ... } else { @($null) }" - when an
# if/else used as an EXPRESSION returns a single-element array whose only element is $null,
# PowerShell's pipeline unwrapping silently collapses the assignment to an empty array. That made
# $ridsToBuild.Count -eq 0 on macOS, so the foreach loop at the bottom of this script never ran -
# dist.macos.sh exited 0 having done nothing, with no error and no output.
[array]$ridsToBuild = @()
if ($IsWindows) { $ridsToBuild = $RuntimeIdentifiers } else { $ridsToBuild = @($null) }

function New-TempDir {
    $p = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $p | Out-Null
    return $p
}

function Sync-LibreWpfTransportRuntime([string]$publishDir, [string]$rid) {
    # LibreWPF.Sdk supplies the compile-time reference surface, but the .NET runtime pack also
    # contains assemblies with the same WPF simple names. Publish can therefore select the latter
    # by basename even though LibreWPF.Transport is the resolved package. That produces a subtly
    # mixed runtime (for example an old WindowsBase.dll without Dispatcher.NativeInputPump).
    # Always overlay the exact managed transport payload that restore selected.
    #
    # Architecture safety: the transport package's lib/net10.0/ may contain assemblies built
    # for the wrong host architecture (e.g. ARM64 on an ARM64 build host, but the target is
    # win-x64). We must never overwrite dotnet publish's correct-arch output with wrong-arch
    # assemblies. Check each DLL's PE machine type before copying.
    $assets = Join-Path $repoRoot 'src/Main/SharpDevelop/obj/project.assets.json'
    if (-not (Test-Path $assets)) { throw "LibreWPF transport sync requires restore assets: $assets" }
    $transportVersion = ((Get-Content $assets -Raw | ConvertFrom-Json).libraries.PSObject.Properties |
        Where-Object { $_.Name -like 'LibreWPF.Transport/*' } |
        Select-Object -First 1).Name -replace '^LibreWPF.Transport/', ''
    if (-not $transportVersion) { throw 'LibreWPF.Transport was not resolved for the OpenDevelop host.' }
    $nugetPackages = ((& $dotnet nuget locals global-packages --list) | Select-String '^global-packages: ').Line -replace '^global-packages:\s*', ''
    $transportRoot = Join-Path $nugetPackages "librewpf.transport/$transportVersion"

    # Expected PE machine types per RID
    $expectedMachine = switch ($rid) {
        'win-x64'  { 0x8664 }
        'win-arm64'{ 0xAA64 }
        'win-x86'  { 0x014C }
        default    { 0 }
    }
    function Test-DllArchMatch([string]$dllPath, [uint16]$target) {
        $bytes = [System.IO.File]::ReadAllBytes($dllPath)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        return $machine -eq $target
    }

    # Prefer the RID-specific managed payload when progpu-wpf-windows-managed-runtime.ps1
    # has run — these are guaranteed correct-arch. Otherwise fall back to lib/net10.0/ with
    # per-file arch filtering so wrong-arch assemblies do not overwrite dotnet publish output.
    $ridManaged = Join-Path $transportRoot "runtimes/$rid/lib/net10.0"
    $fallbackManaged = Join-Path $transportRoot "lib/net10.0"
    $copied = 0
    $skipped = 0

    if ((Test-Path $ridManaged) -and (Get-ChildItem $ridManaged -File -ErrorAction SilentlyContinue)) {
        # Overlay only the RID-specific managed payload (PresentationCore + DirectWriteForwarder).
        # These two assemblies have per-architecture builds from progpu-wpf-windows-managed-runtime.ps1.
        # All other assemblies in lib/net10.0/ have mixed host-architecture binaries and must NOT
        # be copied — dotnet publish already placed the correct-arch versions from the .NET runtime
        # pack or from the transport package's own restore selection.
        Write-Host "Overlaying RID-specific transport payload from $ridManaged"
        Copy-Item -Path (Join-Path $ridManaged '*') -Destination $publishDir -Recurse -Force
        $copied = (Get-ChildItem $ridManaged -File).Count
    } elseif (Test-Path $fallbackManaged) {
        # Legacy path: filter each DLL by PE machine type to avoid corrupting the publish output
        Write-Host "Filtering transport payload from lib/net10.0/ for $rid (expected machine 0x$($expectedMachine.ToString('X4')))"
        foreach ($dll in (Get-ChildItem $fallbackManaged -Filter '*.dll')) {
            if ($expectedMachine -eq 0 -or (Test-DllArchMatch $dll.FullName $expectedMachine)) {
                Copy-Item $dll.FullName -Destination $publishDir -Force
                $copied++
            } else {
                $skipped++
            }
        }
        # Copy non-DLL assets (themes subdirs, PDBs not needed, satellite resource dirs)
        foreach ($dir in (Get-ChildItem $fallbackManaged -Directory)) {
            Copy-Item $dir.FullName -Destination $publishDir -Recurse -Force
        }
    } else {
        throw "LibreWPF transport runtime payload not found at $transportRoot"
    }
    Write-Host "Synced LibreWPF.Transport $transportVersion runtime payload ($rid): $copied assemblies copied, $skipped wrong-arch skipped"
}

function Get-PinnedGitVersionProperties {
    <#
      src/Main/GlobalAssemblyInfo.cs is regenerated by Directory.Build.targets'
      OpenDevelopGenerateGlobalAssemblyInfo target, which invokes the GitVersion.MsBuild task
      independently IN EVERY PROJECT THAT LINKS THE FILE (~60 of them), including inside the
      later solution-wide "Build-Solution" AddIns pass below. GitVersion.MsBuild is not immune to
      producing a different CommitsSinceVersionSource between two separate top-level `dotnet`
      invocations a few seconds apart (observed: host published as revision 1, then the AddIns
      build silently rewrote the shared GlobalAssemblyInfo.cs to revision 2 partway through -
      WriteOnlyWhenDifferent still overwrites when the content DOES differ). Since the host DLL
      was already compiled and published against the OLD value, any AddIn compiled against the
      NEW value references a host assembly version that no longer exists on disk, throwing
      FileNotFoundException at runtime (e.g. GitAddIn's "ICSharpCode.SharpDevelop, Version=X"),
      which used to cascade into a hard crash showing the error dialog.
      Fix: read back the exact values the host publish step already committed to disk, and pass
      them as explicit -p:GitVersion_* MSBuild global properties to the AddIns build below.
      Global properties set via the command line cannot be overridden by a property assignment
      inside the build (GitVersion.MsBuild's own output is silently ignored once these are set),
      so every one of the ~60 projects is now guaranteed to compute byte-identical text and the
      shared file is never rewritten mid-pipeline.
    #>
    param([Parameter(Mandatory)][string]$GlobalAssemblyInfoPath)

    if (-not (Test-Path $GlobalAssemblyInfoPath)) {
        throw "dist.ps1: cannot pin GitVersion - $GlobalAssemblyInfoPath was not generated by the host publish"
    }
    $content = Get-Content -LiteralPath $GlobalAssemblyInfoPath -Raw
    $extract = { param($name)
        $m = [regex]::Match($content, "public const string $name = `"([^`"]*)`"")
        if (-not $m.Success) { throw "dist.ps1: cannot find RevisionClass.$name in $GlobalAssemblyInfoPath" }
        return $m.Groups[1].Value
    }
    $shaMatch = [regex]::Match($content, 'AssemblyInformationalVersion\(RevisionClass\.FullVersion \+ "\+([^"]*)"\)')
    if (-not $shaMatch.Success) { throw "dist.ps1: cannot find the informational-version sha suffix in $GlobalAssemblyInfoPath" }

    return @(
        "-p:GitVersion_Major=$(& $extract 'Major')",
        "-p:GitVersion_Minor=$(& $extract 'Minor')",
        "-p:GitVersion_Patch=$(& $extract 'Build')",
        "-p:GitVersion_CommitsSinceVersionSource=$(& $extract 'Revision')",
        "-p:GitVersion_FullSemVer=$(& $extract 'FullVersion')",
        "-p:GitVersion_ShortSha=$($shaMatch.Groups[1].Value)"
    )
}

function Build-MicrosoftDesignerHosts {
    <#
      Builds the genuine-Microsoft-framework designer backends that are NOT referenced by
      OpenDevelop.Mvp.slnx and therefore never get built by Build-Solution:
        - FormsDesigner\MicrosoftHost\Host (Windows Forms against the .NET Desktop Runtime)
        - WinUIXamlDesigner.MicrosoftHost  (real WinUI 3, multi-targets net9.0/net10.0)

      Each one is its own DeployToAddIns target (AfterTargets="Build") that copies straight into
      the shared AddIns/ tree, mirroring how the solution-referenced AddIns deploy themselves - so
      building these two projects on the side, before the AddIns copy step, is sufficient; nothing
      else needs to know they exist.

      WinUIXamlDesigner.MicrosoftHost specifically CANNOT be built by `dotnet build`: UseWinUI
      pulls in MrtCore.PriGen.targets, whose tasks ship only with Visual Studio (MSB4062
      otherwise) - see "Building WinUIXamlDesigner.MicrosoftHost" in CLAUDE.md. Per the project's
      own standing convention, both projects here are built with Visual Studio's MSBuild.exe
      rather than `dotnet build`, even though FormsDesigner's host does not strictly require it.

      $Rid matters only for the WinUI host: an unpackaged WinUI 3 app is built RID-specific, and
      its DeployToAddIns target copies $(TargetDir) - the RID subfolder - so a missing/incorrect
      -p:RuntimeIdentifier here silently ships the wrong (or build-host-default) architecture,
      exactly the trap RuntimeIdentifier's own doc comment above describes for the main app.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [string]$Rid,
        [string[]]$PinnedGitVersionProperties = @()
    )

    $msbuild = Find-VsMsBuild
    Write-Host "==> Using Visual Studio MSBuild for Microsoft designer hosts: $msbuild"

    $formsDesignerHost = Join-Path $RepoRoot 'src/AddIns/DisplayBindings/FormsDesigner/MicrosoftHost/Host/MicrosoftFormsDesigner.Host.csproj'
    Write-Host '==> Building Microsoft Windows Forms design host...'
    Invoke-Native $msbuild $formsDesignerHost '-restore' "-p:Configuration=$Configuration" '-p:DisableGitVersionTask=true' '-v:m' @PinnedGitVersionProperties

    $winUiHost = Join-Path $RepoRoot 'src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj'
    Write-Host "==> Building Microsoft WinUI design host (RuntimeIdentifier=$Rid)..."
    $winUiArgs = @('-restore', "-p:Configuration=$Configuration", '-p:DisableGitVersionTask=true', '-v:m') + $PinnedGitVersionProperties
    if ($Rid) { $winUiArgs += "-p:RuntimeIdentifier=$Rid" }
    Invoke-Native $msbuild $winUiHost @winUiArgs
}

function Test-PackagedAppStartup {
    <#
      Launch the packaged app and require it to STAY up. A distribution that is missing or
      mismatching a runtime assembly does not fail the build — it throws at boot, which is exactly
      what the deps.json patch and the transport overlay above exist to prevent. Dump the captured
      output on an early exit so the cause is visible without re-running by hand.
    #>
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$WorkingDirectory
    )
    $tag = [System.Guid]::NewGuid().ToString('N')
    # Start-Process rejects using ONE file for both streams; keep two.
    $outLog = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$tag.out.log"
    $errLog = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$tag.err.log"

    $startArgs = @{
        FilePath               = $ExePath
        RedirectStandardOutput = $outLog
        RedirectStandardError  = $errLog
        PassThru               = $true
    }
    if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }
    $proc = Start-Process @startArgs

    $ok = $true
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        if ($proc.HasExited) { $ok = $false; break }
    }
    if (-not $ok) {
        Write-Host "dist.ps1: packaged app exited during startup (status $($proc.ExitCode))" -ForegroundColor Red
        foreach ($log in $outLog, $errLog) {
            if (Test-Path $log) { Get-Content $log -TotalCount 160 | Write-Host }
        }
        Remove-Item -Force $outLog, $errLog -ErrorAction SilentlyContinue
        exit 1
    }

    # Ask the message loop to shut down before forcing the process, so the app releases its file
    # locks; on Windows a still-locked payload can otherwise be captured half-written by the .zip.
    try { $proc.CloseMainWindow() | Out-Null } catch {}
    Start-Sleep -Seconds 2
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Force $outLog, $errLog -ErrorAction SilentlyContinue
    Write-Host 'Packaged app startup smoke test passed'
}

function Test-WindowsPeArchitecture {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Rid)

    $expectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } elseif ($Rid -eq 'win-arm64') { 0xAA64 } else { throw "Unsupported Windows RID: $Rid" }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Not a PE executable: $Path" }
        $machine = $reader.ReadUInt16()
        if ($machine -ne $expectedMachine) {
            throw "Wrong executable architecture for ${Rid}: $Path has machine 0x$('{0:X4}' -f $machine), expected 0x$('{0:X4}' -f $expectedMachine)."
        }
    } finally { $stream.Dispose() }
}

function Test-WindowsPayloadAssemblyArchitecture {
    # Test-WindowsPeArchitecture above only ever checked OpenDevelop.exe - but the apphost is the
    # ONE file dotnet publish always emits for the requested RID, so it is the one file that could
    # never be wrong. Everything actually at risk is a managed assembly: most are AnyCPU IL (machine
    # 0x014C, loadable anywhere and correctly ignored here), while ReadyToRun-compiled ones carry a
    # real target machine and will not load at all in a process of another architecture
    # ("The assembly architecture is not compatible with the current process architecture", or for a
    # native-loaded dependency "Format of the executable (.exe) or library (.dll) is invalid").
    #
    # A stale per-project bin/obj from a build at a different RID is enough to slip such an assembly
    # into the payload: dotnet publish reports success and only warns (CS8012 "targets a different
    # processor"), so nothing fails until a user launches the app and it dies on startup. Fail the
    # package build here instead - checking every assembly, since which projects emit R2R output
    # varies with configuration.
    param([Parameter(Mandatory)][string]$PayloadRoot, [Parameter(Mandatory)][string]$Rid)

    $expectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } elseif ($Rid -eq 'win-arm64') { 0xAA64 } else { throw "Unsupported Windows RID: $Rid" }
    $anyCpu = 0x014C
    $mismatched = @()

    # Known, accepted exception. Every other child process in this payload is launched as
    # "dotnet exec <dll>" and has its apphost suppressed (UseAppHost=false, see
    # Directory.Build.targets), which is what keeps the payload architecture-clean. This one
    # apphost cannot be suppressed the same way: its project lives under
    # externals/vscode-wpf/external/wxsg/external/XamlToCSharpGenerator/, which carries its own
    # Directory.Build.targets and therefore never sees this repo's. The apphost is unused - the
    # language server is started from its .dll - so a wrong-architecture copy is inert rather than
    # broken. Remove this entry if that submodule ever adopts the setting, or if the file stops
    # being published.
    $knownForeignApphosts = @('XamlToCSharpGenerator.LanguageServer.exe')

    foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File -Include '*.dll', '*.exe') {
        # Anything under a runtimes/<rid>/ folder is SUPPOSED to be architecture-specific: that is the
        # standard .NET layout for shipping several architectures side by side, and the host picks the
        # matching one at startup via deps.json runtimeTargets. A win-arm64 native library inside a
        # win-x64 payload's runtimes/win-arm64/ is correct, not a defect - flagging it would make this
        # check impossible to satisfy.
        if ($file.FullName -like '*\runtimes\*') { continue }

        $stream = $null
        try {
            $stream = [System.IO.File]::OpenRead($file.FullName)
            if ($stream.Length -lt 0x40) { continue }
            $reader = [System.IO.BinaryReader]::new($stream)
            $stream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            if ($peOffset -le 0 -or ($peOffset + 6) -gt $stream.Length) { continue }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { continue }   # not PE (native resource, etc.)
            $machine = $reader.ReadUInt16()
            if ($machine -ne $expectedMachine -and $machine -ne $anyCpu -and
                $knownForeignApphosts -notcontains $file.Name) {
                $relative = $file.FullName.Substring($PayloadRoot.Length).TrimStart('\', '/')
                $mismatched += "  $relative (machine 0x$('{0:X4}' -f $machine))"
            }
        } catch {
            # An unreadable/locked file is the packaging checks' problem elsewhere, not this one.
        } finally {
            if ($stream) { $stream.Dispose() }
        }
    }

    if ($mismatched) {
        throw "Distribution payload for $Rid contains assemblies built for another architecture " +
            "(expected 0x$('{0:X4}' -f $expectedMachine) or AnyCPU 0x014C):$([Environment]::NewLine)" +
            (($mismatched | Sort-Object | Select-Object -First 20) -join [Environment]::NewLine) +
            "$([Environment]::NewLine)This usually means a per-project bin/obj still holds output from a build at a " +
            "different RuntimeIdentifier - clean those projects and re-publish."
    }
}

function Test-WindowsDistributionPayload {
    param([Parameter(Mandatory)][string]$PayloadRoot, [Parameter(Mandatory)][string]$Rid)

    $exe = Join-Path $PayloadRoot 'OpenDevelop.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Distribution payload has no OpenDevelop.exe: $PayloadRoot" }
    Test-WindowsPeArchitecture -Path $exe -Rid $Rid
    Test-WindowsPayloadAssemblyArchitecture -PayloadRoot $PayloadRoot -Rid $Rid
    $addIns = Join-Path $PayloadRoot 'AddIns'
    if (-not (Test-Path -LiteralPath $addIns)) { throw "Distribution payload has no AddIns directory: $PayloadRoot" }
    if (@(Get-ChildItem -LiteralPath $addIns -Recurse -File -Filter '*.addin').Count -eq 0) { throw "Distribution payload has no addin manifests: $addIns" }

    # The WPF designer ships two mutually-exclusive out-of-process hosts - LibreWPF's own
    # portable runtime (Host\) and the genuine Microsoft WPF host that runs against the
    # machine's installed .NET Desktop Runtime (MicrosoftHost\). WpfSurfaceHostClient
    # deliberately refuses to fall back from one to the other at runtime (see
    # WpfSurfaceHostClient.StartAsync/AcquireSharedAsync), so a distribution missing either one
    # would leave that backend's projects unable to open a designer at all - fail the package
    # build now instead of shipping that silently.
    # Checked as .dll, not .exe: out-of-process hosts no longer generate an apphost (UseAppHost=false
    # in Directory.Build.targets) because DesignerHostProcessClient launches them as
    # "dotnet exec <host>.dll". The managed assembly is what actually has to be present.
    $wpfDesignRoot = Join-Path $addIns 'DisplayBindings\WpfDesign'
    foreach ($hostSpec in @(
        @{ SubDir = 'Host';          Exe = 'WpfDesign.SurfaceHost.dll';          Backend = 'LibreWPF' },
        @{ SubDir = 'MicrosoftHost'; Exe = 'MicrosoftWpfDesign.SurfaceHost.dll'; Backend = 'Microsoft WPF' }
    )) {
        $hostExe = Join-Path $wpfDesignRoot "$($hostSpec.SubDir)\$($hostSpec.Exe)"
        if (-not (Test-Path -LiteralPath $hostExe)) {
            throw "Distribution payload is missing the $($hostSpec.Backend) design host: $hostExe. " +
                "Both WPF designer backends must be built and deployed - check that every " +
                "src/AddIns/DisplayBindings/WpfDesign/MicrosoftHost/*.csproj project is referenced " +
                "by OpenDevelop.Mvp.slnx."
        }
    }

    # The Windows Forms and WinUI designers each have their own genuine-Microsoft-framework
    # backend, neither referenced by OpenDevelop.Mvp.slnx (WinForms.MicrosoftHost needs the
    # .NET Desktop Runtime; WinUIXamlDesigner.MicrosoftHost additionally needs UseWinUI's
    # MrtCore.PriGen.targets, which only ships with Visual Studio - see "Building
    # WinUIXamlDesigner.MicrosoftHost" in CLAUDE.md). Both are built by a dedicated
    # Build-MicrosoftDesignerHosts step, not the normal solution build, so verify their output
    # lands in the payload the same way the WPF check above does - a silent gap here is exactly
    # how this backend went unbuilt/unshipped before.
    $formsDesignerHostExe = Join-Path $addIns 'DisplayBindings\FormsDesigner\MicrosoftHost\MicrosoftFormsDesigner.Host.dll'
    if (-not (Test-Path -LiteralPath $formsDesignerHostExe)) {
        throw "Distribution payload is missing the Microsoft Windows Forms design host: $formsDesignerHostExe. " +
            "Check that Build-MicrosoftDesignerHosts built " +
            "src/AddIns/DisplayBindings/FormsDesigner/MicrosoftHost/Host/MicrosoftFormsDesigner.Host.csproj."
    }
    # Deployed under a per-TFM subfolder (net9.0 / net10.0) picked at runtime by
    # MicrosoftWinUIDesignRuntimeHostBootstrap based on the designed app's own runtimeconfig -
    # both must be present, not just whichever one a plain build happened to produce last.
    $winUiHostRoot = Join-Path $addIns 'DisplayBindings\WinUIXamlDesigner\MicrosoftHost'
    foreach ($tfmDir in 'net9.0', 'net10.0') {
        $winUiHostExe = Join-Path $winUiHostRoot "$tfmDir\WinUIXamlDesigner.MicrosoftHost.exe"
        if (-not (Test-Path -LiteralPath $winUiHostExe)) {
            throw "Distribution payload is missing the Microsoft WinUI design host ($tfmDir): $winUiHostExe. " +
                "Check that Build-MicrosoftDesignerHosts built " +
                "src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj " +
                "with Visual Studio's own MSBuild.exe (dotnet build cannot build UseWinUI projects)."
        }
    }

    # PDBs, reference assemblies and foreign native assets are build-time artifacts. Their
    # presence means either an SDK target or the staging copy regressed, and makes the final ZIP
    # needlessly architecture/OS-agnostic rather than deployable.
    $forbidden = Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($PayloadRoot.Length).Replace('\', '/').ToLowerInvariant()
        $_.Extension -in '.pdb', '.dylib', '.so' -or
        $relative -match '/ref/' -or
        $relative -match '/runtimes/(linux|unix|osx)'
    }
    if ($forbidden) {
        $sample = ($forbidden | Select-Object -First 12 -ExpandProperty FullName) -join [Environment]::NewLine
        throw "Distribution payload contains build-only or foreign assets:$([Environment]::NewLine)$sample"
    }
}

function Test-WindowsDistributionZip {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$Rid)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $prefix = "OpenDevelop-$Rid/"
        if (-not ($archive.Entries.FullName -contains "${prefix}OpenDevelop.exe")) { throw "ZIP lacks ${prefix}OpenDevelop.exe: $ZipPath" }
        if (-not ($archive.Entries.FullName | Where-Object { $_ -like "${prefix}AddIns/*.addin" })) { throw "ZIP lacks addin manifests: $ZipPath" }
        # Mirror the WPF designer host check in Test-WindowsDistributionPayload - both backends
        # must survive the staging/zip round-trip, not just be present in the staged payload dir.
        foreach ($hostSpec in @(
            @{ Path = "${prefix}AddIns/DisplayBindings/WpfDesign/Host/WpfDesign.SurfaceHost.dll";          Backend = 'LibreWPF' },
            @{ Path = "${prefix}AddIns/DisplayBindings/WpfDesign/MicrosoftHost/MicrosoftWpfDesign.SurfaceHost.dll"; Backend = 'Microsoft WPF' },
            @{ Path = "${prefix}AddIns/DisplayBindings/FormsDesigner/MicrosoftHost/MicrosoftFormsDesigner.Host.dll"; Backend = 'Microsoft Windows Forms' },
            @{ Path = "${prefix}AddIns/DisplayBindings/WinUIXamlDesigner/MicrosoftHost/net9.0/WinUIXamlDesigner.MicrosoftHost.exe"; Backend = 'Microsoft WinUI (net9.0)' },
            @{ Path = "${prefix}AddIns/DisplayBindings/WinUIXamlDesigner/MicrosoftHost/net10.0/WinUIXamlDesigner.MicrosoftHost.exe"; Backend = 'Microsoft WinUI (net10.0)' }
        )) {
            if (-not ($archive.Entries.FullName -contains $hostSpec.Path)) {
                throw "ZIP is missing the $($hostSpec.Backend) design host: $($hostSpec.Path) ($ZipPath)"
            }
        }
        $forbidden = $archive.Entries.FullName | Where-Object {
            $name = $_.ToLowerInvariant()
            $name.EndsWith('.pdb') -or $name.EndsWith('.dylib') -or $name.EndsWith('.so') -or
            $name -match '/ref/' -or $name -match '/runtimes/(linux|unix|osx)'
        }
        if ($forbidden) { throw "ZIP contains build-only or foreign assets:$([Environment]::NewLine)$(($forbidden | Select-Object -First 12) -join [Environment]::NewLine)" }
    } finally { $archive.Dispose() }
}

# ---------------------------------------------------------------------------------------------
# Shared pipeline (run once per RID in $ridsToBuild - see Invoke-DistributionPipeline below)
# ---------------------------------------------------------------------------------------------
#
# Restore itself runs per-RID (inside Invoke-DistributionPipeline), NOT once up front here:
# once any project's obj/project.assets.json gets an explicit -p:RuntimeIdentifier baked in by
# one restore, a later --no-restore build/publish for a DIFFERENT (or no) RuntimeIdentifier fails
# with NETSDK1047 ("doesn't have a target for ...") because that RID's target section was never
# written. Passing the SAME -p:RuntimeIdentifier to restore that the matching build/publish call
# will use keeps every pass self-consistent regardless of what a previous pass (or a previous
# manual `dotnet build`/`publish` in this repo) last restored for. Restore is incremental, so
# doing it twice (once per RID) stays cheap when little changed; it also fails fast with a clear
# error instead of leaving a half-built tree. On Windows it additionally seeds LibreWPF.Sdk
# resolution for the AvalonEdit submodule, whose own global.json has no "msbuild-sdks" entry and
# shadows the repo-root one for every project beneath it (otherwise MSB4236 "The SDK
# 'LibreWPF.Sdk' specified could not be found").

# ---------------------------------------------------------------------------------------------
# Platform packaging
# ---------------------------------------------------------------------------------------------

function Invoke-MacPackaging {
    Write-Host "==> Building framework-dependent .app bundle ($config)..."
    $env:DIST_CONFIG = $config
    & bash (Join-Path $repoRoot 'build/macos/build-application-bundle.sh')
    if ($LASTEXITCODE -ne 0) { throw "build-application-bundle.sh exited with code $LASTEXITCODE" }

    Write-Host '==> Smoke-testing packaged app...'
    Test-PackagedAppStartup -ExePath (Join-Path $repoRoot 'OpenDevelop.app/Contents/MacOS/OpenDevelop')

    Write-Host '==> Building .dmg...'
    Push-Location $repoRoot
    try {
        & bash (Join-Path $repoRoot 'build/macos/build-dmg.sh') OpenDevelop.app OpenDevelop-macos.dmg
        if ($LASTEXITCODE -ne 0) { throw "build-dmg.sh exited with code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    return (Join-Path $repoRoot 'OpenDevelop-macos.dmg')
}

function Invoke-WindowsPackaging {
    # $rid names this pass's payload/zip (e.g. "OpenDevelop-win-arm64", "OpenDevelop-windows-arm64.zip")
    # so building both architectures in one dist.ps1 run never has one overwrite the other.
    param([Parameter(Mandatory)][string]$Rid, [Parameter(Mandatory)][string]$PublishDir)

    # $Rid is a full RID like "win-x64"/"win-arm64" - do not prefix another "win-" onto it.
    $payloadRoot = Join-Path $repoRoot "OpenDevelop-$Rid"
    $zipPath = Join-Path $repoRoot "OpenDevelop-$Rid.zip"

    Write-Host "==> Assembling distribution payload ($config, $Rid)..."
    if (Test-Path $payloadRoot) { Remove-Item -Recurse -Force $payloadRoot }
    New-Item -ItemType Directory -Path $payloadRoot | Out-Null
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $payloadRoot -Recurse -Force

    # `dotnet publish` may preserve symbol/reference files from a Debug (and occasionally a
    # cached Release) build. They are neither loaded by the framework-dependent app nor useful
    # to an end user, but previously dominated the Windows archive. Do this in staging so the
    # verified publish directory remains available for diagnostics and incremental builds.
    $hostBuildOnlyFiles = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($payloadRoot.Length).Replace('\', '/').ToLowerInvariant()
        $_.Extension -eq '.pdb' -or $relative -match '/ref/' -or
        ($_.Extension -eq '.xml' -and (Test-Path -LiteralPath (Join-Path $_.DirectoryName "$($_.BaseName).dll")))
    }
    Remove-Item -LiteralPath $hostBuildOnlyFiles.FullName -Force -ErrorAction SilentlyContinue
    $hostReferenceDirs = Get-ChildItem -LiteralPath $payloadRoot -Recurse -Directory -Filter ref -ErrorAction SilentlyContinue
    foreach ($referenceDir in $hostReferenceDirs) { Remove-Item -LiteralPath $referenceDir.FullName -Recurse -Force }

    # OpenDevelop locates its addins and data at runtime by walking UP from the executable looking
    # for data/resources/languages/LanguageDefinition.xml (SharpDevelopMain.FindApplicationRootPath),
    # then loading *.addin from <root>/AddIns. The payload must therefore contain data/ and AddIns/
    # next to the executable so the walk resolves on the first step.
    Copy-Item -Path (Join-Path $repoRoot 'data') -Destination (Join-Path $payloadRoot 'data') -Recurse -Force

    # AddIn build outputs carry their full dependency closures. Anything already supplied by the
    # published host resolves from the application base directory, so skip those files by name
    # instead of copying ~2 GB and pruning afterwards. This also keeps stale XML docs, satellite
    # resources and native helpers from an old developer build out of the payload.
    $hostFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | ForEach-Object { [void]$hostFiles.Add($_.Name) }

    $addInsSource = (Resolve-Path (Join-Path $repoRoot 'AddIns')).Path
    $addInsTarget = Join-Path $payloadRoot 'AddIns'

    # OpenDevelopAddinKind=OutOfProcessHost projects (WinForms/WPF/WinUI design-surface hosts) run
    # as their own separate "dotnet exec" child process with its own working directory - unlike an
    # InProcess addin, they cannot resolve a same-named dependency from files sitting beside
    # OpenDevelop.exe, so the by-name dedup below must not strip files out of their deployment
    # folders. Missing this once (FormsDesigner's Host\ folder losing PresentationFramework.dll,
    # every ProGPU.*.dll, System.Windows.Forms.dll, ...) made the WinForms designer's child host
    # crash before completing its handshake, surfacing only as an opaque
    # "System.TimeoutException: The operation has timed out" with no further detail. Keep this in
    # sync with each OutOfProcessHost project's own DeployToAddIns destination.
    $outOfProcessHostDirs = @(
        'DisplayBindings\FormsDesigner\Host',
        'DisplayBindings\FormsDesigner\MicrosoftHost',
        'DisplayBindings\GtkDesigner\Host',
        'DisplayBindings\MewUIDesigner\Host',
        'DisplayBindings\WinUIXamlDesigner\UnoHost',
        'DisplayBindings\WinUIXamlDesigner\MicrosoftHost',
        'DisplayBindings\WpfDesign\Host',
        'DisplayBindings\WpfDesign\MicrosoftHost'
    )

    # Select first, then copy in a plain foreach. A ForEach-Object block runs in a child scope, so
    # a counter incremented inside one needs an explicit $script: qualifier — which silently
    # counted nothing once this loop moved inside a function. Keeping the copy in a normal loop
    # means the count and the filtering read the same way and cannot drift apart again.
    $addInFiles = Get-ChildItem -LiteralPath $addInsSource -Recurse -File | Where-Object {
        $name = $_.Name
        $relative = $_.FullName.Substring($addInsSource.Length).TrimStart('\', '/')
        $relativeDir = Split-Path -Parent $relative
        $isOutOfProcessHost = $outOfProcessHostDirs | Where-Object { $relativeDir -eq $_ -or $relativeDir.StartsWith("$_\") }
        -not ($name -like '*.pdb') -and
        -not ($name -like '*.dylib') -and
        -not ($name -like '*.so') -and
        -not ($name -like 'LeXtudio.DevFlow.*') -and
        -not ($name -like 'CliclickSharp*') -and
        -not ($relative -match '(^|[\\/])(ref|runtimes[\\/](linux|unix|osx))([\\/]|$)') -and
        -not ($_.Extension -eq '.xml' -and (Test-Path -LiteralPath (Join-Path $_.DirectoryName "$($_.BaseName).dll"))) -and
        (-not $hostFiles.Contains($name) -or $isOutOfProcessHost)
    }

    foreach ($file in $addInFiles) {
        $relative = $file.FullName.Substring($addInsSource.Length).TrimStart('\', '/')
        $destination = Join-Path $addInsTarget $relative
        $destinationDir = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    Write-Host "    AddIn files copied: $(@($addInFiles).Count)"

    $exePath = Join-Path $payloadRoot 'OpenDevelop.exe'
    if (-not (Test-Path $exePath)) { throw "dist.ps1: packaged executable not found: $exePath" }
    Test-WindowsDistributionPayload -PayloadRoot $payloadRoot -Rid $Rid
    Write-Host "Payload ready: $payloadRoot"

    # A Windows ARM64 build machine can produce an x64 package, but does not necessarily have
    # the x64 framework-dependent .NET/LibreWPF runtime installed. Validate that cross-RID
    # package structurally (including its PE machine and ZIP contents) and reserve execution
    # smoke tests for the package that can actually run on this host.
    $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $hostRid = if ($hostArchitecture -eq 'x64') { 'win-x64' } elseif ($hostArchitecture -eq 'arm64') { 'win-arm64' } else { '' }
    if ($Rid -eq $hostRid) {
        Write-Host '==> Smoke-testing packaged app...'
        Test-PackagedAppStartup -ExePath $exePath -WorkingDirectory $payloadRoot
    } else {
        Write-Host "==> Skipping execution smoke test for $Rid on $hostRid; PE and ZIP content checks still apply."
    }

    Write-Host '==> Building .zip...'
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadRoot, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
    Test-WindowsDistributionZip -ZipPath $zipPath -Rid $Rid

    return $zipPath
}

function Invoke-DistributionPipeline([string]$Rid) {
    # $Rid is $null on macOS (single host-architecture pass, no -p:RuntimeIdentifier override -
    # matches the pre-multi-arch behavior exactly). On Windows it is one of $RuntimeIdentifiers.
    $ridSuffix = if ($Rid) { "/$Rid" } else { '' }
    $publishDir = Join-Path $repoRoot "src/Main/SharpDevelop/bin/$config/$tfm$ridSuffix/publish"
    $depsJson = Join-Path $publishDir 'OpenDevelop.deps.json'
    # Built with an explicit typed empty array + += (not "if (...) { @(...) } else { @() }"
    # assigned directly) - the latter can degrade a single-element array into a bare string on
    # assignment, which then splats one character per argument instead of the whole "-p:..." token.
    [string[]]$ridArgs = @()
    if ($Rid) { $ridArgs += "-p:RuntimeIdentifier=$Rid" }
    $ridLabel = if ($Rid) { " ($Rid)" } else { '' }

    Write-Host "==> Restoring solution$ridLabel..."
    Restore-Solution -DotNet $dotnet -Solution $sln -ExtraProperties $ridArgs

    if (-not $SkipPublish) {
        # Clear the shared intermediate output so publish cannot reuse artifacts left by a
        # previous build (of this RID, or of a differently-RID'd pass before this one). This
        # distribution intentionally remains framework-dependent and uses the installed .NET
        # runtime; the SDK-generated apphost is only the native entry point and does not bundle
        # that runtime.
        Write-Host "==> Cleaning intermediate outputs$ridLabel..."
        $hostObj = Join-Path $repoRoot "src/Main/SharpDevelop/obj/$config/$tfm"
        if (Test-Path $hostObj) { Remove-Item -Recurse -Force $hostObj }

        # Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources (WPF theme
        # resource blob) can otherwise stale-cross from a previous build and produce a 12-byte
        # corrupt file that crashes at boot with EndOfStreamException in
        # FindResource/LoadThemedDictionary. Clear obj/ and bin/ entirely (not just the $config
        # subfolder) since an explicit -p:RuntimeIdentifier can binplace this project's output
        # under an additional RID-suffixed subfolder that the plain "$sub/$config" path misses.
        $corePres = Join-Path $repoRoot 'src/Main/ICSharpCode.Core.Presentation'
        foreach ($sub in 'obj', 'bin') {
            $dir = Join-Path $corePres $sub
            if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        }

        Write-Host "==> Publishing framework-dependent app ($config)$ridLabel..."
        if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
        Invoke-Native $dotnet publish $hostProject -c $config --self-contained false `
            "-p:OpenDevelopDistributionBuild=true" `
            "-p:PublishDir=$publishDir" `
            "-p:ProGpuWpfUseCurrentRuntimeIdentifier=false" `
            @ridArgs

        if (-not (Test-Path $publishDir)) {
            throw "dist.ps1: host publish directory not found: $publishDir"
        }

        # NuGet conflict resolution omits LibreWinForms from the standard publish closure.
        # Patch the final manifest and copy its matching runtime files.
        $nugetPackagesLine = & $dotnet nuget locals global-packages --list | Select-String '^global-packages: '
        if (-not $nugetPackagesLine) {
            throw 'dist.ps1: cannot determine the NuGet global-packages directory'
        }
        $nugetPackages = ($nugetPackagesLine.Line -replace '^global-packages:\s*', '')
        & $patchScript $depsJson $nugetPackages
        if ($Rid) { Sync-LibreWpfTransportRuntime $publishDir $Rid }

        # Some projects write to OpenDevelopHostPublishDir while computing their distribution
        # closure. Give that build a disposable copy so the verified host deployment above
        # remains immutable.
        $hostPublishSnapshot = New-TempDir
        Copy-Item -Path (Join-Path $publishDir '*') -Destination $hostPublishSnapshot -Recurse -Force

        # AddIns/ is an ignored deployment directory, not source. A partial or cross-platform
        # build used to leave .so/.dylib, ref/, package build props and stale assemblies here;
        # later package runs then copied them even though the current solution never produced
        # them. Start every distribution build with a genuinely empty deployment root.
        Write-Host '==> Cleaning generated AddIn deployment root...'
        $generatedAddIns = Join-Path $repoRoot 'AddIns'
        if (Test-Path $generatedAddIns) { Remove-Item -LiteralPath $generatedAddIns -Recurse -Force }
        New-Item -ItemType Directory -Path $generatedAddIns | Out-Null

        # Pin every AddIn to the EXACT GitVersion values the just-published host assembly was
        # compiled against - see Get-PinnedGitVersionProperties for why this is required.
        $globalAssemblyInfo = Join-Path $repoRoot 'src/Main/GlobalAssemblyInfo.cs'
        $pinnedGitVersionProperties = Get-PinnedGitVersionProperties -GlobalAssemblyInfoPath $globalAssemblyInfo

        Write-Host "==> Building distribution AddIns without shared runtime copies$ridLabel..."
        Build-Solution -DotNet $dotnet -Solution $sln -Configuration $config -ExtraProperties (@(
            '-p:OpenDevelopDistributionBuild=true',
            "-p:OpenDevelopDistributionRidFamily=$ridFamily",
            "-p:OpenDevelopHostPublishDir=$hostPublishSnapshot",
            '-p:ProGpuWpfCopyPackageRuntimeAssets=false',
            '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false'
        ) + $pinnedGitVersionProperties)

        if ($IsWindows) {
            Write-Host "==> Building Microsoft-framework designer hosts (VS MSBuild)$ridLabel..."
            Build-MicrosoftDesignerHosts -RepoRoot $repoRoot -Configuration $config -Rid $Rid `
                -PinnedGitVersionProperties $pinnedGitVersionProperties
        }
        Remove-Item -Recurse -Force $hostPublishSnapshot

        # The solution traversal may copy reference assemblies over the original PublishDir
        # through cached project state. Restore the authoritative package runtime payload only
        # after every build has completed.
        & $patchScript $depsJson $nugetPackages
        if ($Rid) { Sync-LibreWpfTransportRuntime $publishDir $Rid }
    }
    else {
        Write-Host "==> Skipping publish$ridLabel (-SkipPublish)"
    }

    if (-not (Test-Path $publishDir)) {
        throw "dist.ps1: framework-dependent publish directory not found: $publishDir"
    }

    if ($IsWindows) { Invoke-WindowsPackaging -Rid $Rid -PublishDir $publishDir }
    else { Invoke-MacPackaging }
}

$artifacts = @()
foreach ($rid in $ridsToBuild) {
    $artifacts += Invoke-DistributionPipeline -Rid $rid
}

Write-Host ''
foreach ($artifact in $artifacts) {
    Write-Host "Done: $artifact"
}
