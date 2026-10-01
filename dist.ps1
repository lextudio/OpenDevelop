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
# Usage: ./dist.ps1 [-SkipPublish] [-Configuration Debug|Release]
#   -SkipPublish            reuse existing publish output (faster iteration on packaging)
#   -Configuration Debug    package the Debug configuration instead of Release.
#
# On macOS this is normally reached through ./dist.macos.sh, which only locates pwsh.
#

[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Run ONLY these phases. Order given does not matter; they always execute in canonical order
    # (see -ListPhases). This is what makes patching a published payload cheap: rebuild one AddIn
    # with ./build.ps1, then re-run just -Phase payload instead of the whole pipeline.
    [string[]]$Phase = @(),

    # Run this phase and every phase after it.
    [string]$From,

    # Print the phases for this platform and exit.
    [switch]$ListPhases,

    # Distribution AddIns build in isolated project staging directories so their legacy shared
    # deployment OutputPath no longer forces serial MSBuild execution.
    [ValidateRange(1, 64)]
    [int]$AddInMaxCpuCount = 2,
    # The isolated staging path has a cold, whole-AddIns build validation at -m:2. Keep it on
    # for normal distributions; callers can still use -UseAddInStaging:$false while diagnosing.
    [switch]$UseAddInStaging = $true,

    # Stop anything holding the payload open before touching it. The smoke phase leaves a
    # "dotnet exec OpenDevelop.dll" process behind if it did not shut down cleanly, and the
    # out-of-process designer hosts outlive the IDE by design (SharedDesignerHostPool), so the
    # next payload phase fails on "Access to the path ...\CodeCoverage.dll is denied".
    [switch]$Kill
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
$openAvalonRoot = Join-Path (Split-Path -Parent $repoRoot) 'openavalon'
$windowsX64CanonicalFeed = Join-Path $openAvalonRoot 'artifacts/canonical-winforms-feed-x64'
$windowsArm64CanonicalFeed = Join-Path $openAvalonRoot 'artifacts/canonical-winforms-feed'
$nugetGlobalPackages = $null

# The Addin SDK's OpenDevelopPruneAddinDeploymentAssets target drops runtimes/win*, linux* and
# unix* only for the 'osx' family; on Windows those win* assets are exactly what the payload needs.
$ridFamily = if ($IsWindows) { 'win' } else { 'osx' }

# ONE platform-neutral payload/zip: the app is AnyCPU and apphost-free, and every architecture's
# native/runtime assets live under runtimes/<rid>/ in the same tree. Script-scoped because the
# payload / smoke / zip phases are separately runnable and all three refer to them.
$payloadRoot = Join-Path $repoRoot 'OpenDevelop-win'
$zipPath = Join-Path $repoRoot 'OpenDevelop-win.zip'
$publishDir = Join-Path $repoRoot "src/Main/SharpDevelop/bin/$Configuration/$tfm/publish"
$depsJson = Join-Path $publishDir 'OpenDevelop.deps.json'

function Get-NuGetGlobalPackages {
    # Needed by both the host publish and the AddIns build when they run as separate phases.
    # A complete distribution invokes this from host, AddIns and (on Windows) designer-host
    # phases. The cache location cannot change within this script process, so avoid starting the
    # dotnet CLI and resolving its SDK graph more than once.
    if ($script:nugetGlobalPackages) { return $script:nugetGlobalPackages }
    $line = & $dotnet nuget locals global-packages --list | Select-String '^global-packages: '
    if (-not $line) { throw 'dist.ps1: cannot determine the NuGet global-packages directory' }
    # A trailing directory separator escapes the closing quote when this value is forwarded to
    # the external dependency-patching script on Windows. Keep it as a canonical directory path
    # without a terminal separator.
    $script:nugetGlobalPackages = (($line.Line -replace '^global-packages:\s*', '').Trim().TrimEnd([char[]]@('\', '/')))
    return $script:nugetGlobalPackages
}

function Assert-HostPublished {
    # Every phase from "addins" onward consumes the host publish output. Fail with an actionable
    # message instead of silently assembling a payload around a missing or stale host.
    if (-not (Test-Path $publishDir)) {
        throw "dist.ps1: host publish output not found at $publishDir. Run './dist.ps1 -Phase host' first (or omit -Phase to run everything)."
    }
}

function New-TempDir {
    $p = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $p | Out-Null
    return $p
}

function Remove-RepoGeneratedDirectory {
    <#
      PowerShell's Remove-Item -Recurse enumerates the AddIns deployment file-by-file. On macOS
      that is disproportionately slow for its multi-gigabyte dependency closure. Distribution
      still needs an empty tree (an incremental project build may otherwise leave a removed
      package/RID asset behind), so retain the exact clean-state contract but use the native
      recursive unlink implementation there. The relative-path check makes this intentionally
      incapable of deleting the repository root or anything outside it.
    #>
    param([Parameter(Mandatory)][string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory)) { return }
    $directorySeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd($directorySeparators)
    $fullPath = [System.IO.Path]::GetFullPath($Directory).TrimEnd($directorySeparators)
    $relativePath = [System.IO.Path]::GetRelativePath($repoFullPath, $fullPath)
    $parentPrefix = '..' + [System.IO.Path]::DirectorySeparatorChar
    if ($relativePath -eq '.' -or $relativePath -eq '..' -or $relativePath.StartsWith($parentPrefix, [System.StringComparison]::Ordinal) -or [System.IO.Path]::IsPathRooted($relativePath)) {
        throw "dist.ps1: refusing to remove non-generated repository directory: $fullPath"
    }

    if ($IsWindows) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
        return
    }

    & /bin/rm -rf -- $fullPath
    if ($LASTEXITCODE -ne 0) { throw "dist.ps1: failed to remove generated directory: $fullPath" }
}

function Sync-WindowsDirectoryMirror {
    <#
      robocopy /MIR has the same important correctness property as deleting a generated payload
      before Copy-Item: target files no longer present in the source are removed. Unlike a fresh
      recursive copy it retains unchanged host files, making `-Phase payload` cheap after an
      AddIn-only rebuild. Robocopy exit codes 0..7 describe successful copy/difference states;
      only 8 and above are errors.
    #>
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Description,
        [string[]]$ExcludeDirectories = @(),
        [string[]]$ExcludeFiles = @()
    )

    if (-not $IsWindows) { throw 'Sync-WindowsDirectoryMirror is Windows-only.' }
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "dist.ps1: mirror source does not exist: $Source"
    }

    Write-Host "==> Synchronizing $Description..."
    # /MIR reports every purged file as EXTRA even with /NFL and /NDL. Keep normal release output
    # compact, but retain the detailed robocopy report when a locked file or I/O failure occurs.
    $logPath = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-robocopy-" + [Guid]::NewGuid().ToString('N') + '.log')
    try {
        $exclusions = @()
        if ($ExcludeDirectories.Count -gt 0) { $exclusions += '/XD'; $exclusions += $ExcludeDirectories }
        if ($ExcludeFiles.Count -gt 0) { $exclusions += '/XF'; $exclusions += $ExcludeFiles }
        & robocopy.exe $Source $Destination /MIR /COPY:DAT /DCOPY:DAT /XJ /R:2 /W:1 /NFL /NDL /NJH /NJS /NP @exclusions "/LOG:$logPath" | Out-Null
        $robocopyExitCode = $LASTEXITCODE
        if ($robocopyExitCode -ge 8) {
            $diagnostics = if (Test-Path -LiteralPath $logPath) {
                (Get-Content -LiteralPath $logPath -Tail 120) -join [Environment]::NewLine
            } else { '(robocopy did not create a log)' }
            throw "dist.ps1: robocopy failed while synchronizing $Description (exit code $robocopyExitCode). " +
                  "Something may still have the payload open. Re-run with -Kill, or stop OpenDevelop/dotnet first.`n$diagnostics"
        }
    }
    finally {
        Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    }
}


function Write-DistributionStepElapsed {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Stopwatch]$Stopwatch,
        [Parameter(Mandatory)][string]$Step
    )
    $Stopwatch.Stop()
    Write-Host ("    {0}: {1:N1}s" -f $Step, $Stopwatch.Elapsed.TotalSeconds)
    $Stopwatch.Restart()
}

# Get-PinnedGitVersionProperties now lives in build/common.psm1, shared with build.ps1.

function Build-Launchers {
    <#
      The distribution payload is AnyCPU/apphost-free - OpenDevelop.dll is started as
      "dotnet exec OpenDevelop.dll" - EXCEPT for the one file that unavoidably has to be
      architecture-specific: the native apphost the user actually double-clicks. src/Main/
      OpenDevelop.Launcher is exactly that: a tiny apphost whose only job is to hand off to
      "dotnet exec OpenDevelop.dll" using a dotnet.exe guaranteed to match its own architecture
      (see that project's Program.cs header comment for why no path-guessing is needed - hostfxr
      already solved that just by starting this very process).

      Built twice, once per Windows architecture, and only the resulting .exe is kept per pass -
      renaming an apphost after publish is safe (verified empirically: the companion
      .dll/.deps.json/.runtimeconfig.json paths are baked in at publish time, not re-derived from
      the exe's own on-disk filename at runtime), so the win-x64 pass becomes OpenDevelop.exe and
      the win-arm64 pass becomes OpenDevelopARM64.exe. The companion trio itself (named
      OpenDevelop.Bootstrap.* - deliberately not "OpenDevelop", which is already the real IDE's own
      managed entry point sitting in the same folder) is copied from just ONE pass: the launcher
      project has zero PackageReferences, so there is nothing RID-conditional that could differ
      between the two builds' copies.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$DotNet,
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Release',
        [Parameter(Mandatory)][string]$PayloadRoot
    )

    $launcherProject = Join-Path $RepoRoot 'src/Main/OpenDevelop.Launcher/OpenDevelop.Launcher.csproj'
    $ridToExeName = [ordered]@{ 'win-x64' = 'OpenDevelop.exe'; 'win-arm64' = 'OpenDevelopARM64.exe' }
    $companionCopied = $false

    foreach ($rid in $ridToExeName.Keys) {
        $publishDir = New-TempDir
        try {
            Write-Host "==> Building launcher ($rid)..."
            Invoke-Native $DotNet publish $launcherProject -c $Configuration --self-contained false `
                "-r" $rid "-o" $publishDir

            $builtExe = Join-Path $publishDir 'OpenDevelop.Bootstrap.exe'
            if (-not (Test-Path -LiteralPath $builtExe)) {
                throw "Build-Launchers: expected launcher apphost not found: $builtExe"
            }
            Copy-Item -LiteralPath $builtExe -Destination (Join-Path $PayloadRoot $ridToExeName[$rid]) -Force

            if (-not $companionCopied) {
                foreach ($companionExtension in '.dll', '.deps.json', '.runtimeconfig.json') {
                    $companionFile = Join-Path $publishDir "OpenDevelop.Bootstrap$companionExtension"
                    if (-not (Test-Path -LiteralPath $companionFile)) {
                        throw "Build-Launchers: expected launcher companion file not found: $companionFile"
                    }
                    Copy-Item -LiteralPath $companionFile -Destination $PayloadRoot -Force
                }
                $companionCopied = $true
            }
        } finally {
            Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
        }
    }
}

function Test-WindowsLaunchersCurrent {
    <#
      Launchers are independent of the host/AddIn payload and are expensive to publish twice on
      every `-Phase payload`. Reuse them only when every launcher input predates every required
      output. The normal payload validation still checks both PE machine types afterwards.
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$PayloadRoot
    )

    $requiredOutputs = @(
        'OpenDevelop.exe', 'OpenDevelopARM64.exe', 'OpenDevelop.Bootstrap.dll',
        'OpenDevelop.Bootstrap.deps.json', 'OpenDevelop.Bootstrap.runtimeconfig.json'
    ) | ForEach-Object { Join-Path $PayloadRoot $_ }
    if (@($requiredOutputs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
        return $false
    }

    $launcherRoot = Join-Path $RepoRoot 'src/Main/OpenDevelop.Launcher'
    # bin/ and obj/ are outputs from the preceding launcher publish; treating them as inputs would
    # make their fresh timestamps invalidate this cache forever.
    $inputs = @(Get-ChildItem -LiteralPath $launcherRoot -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($launcherRoot.Length).TrimStart('\', '/').Replace('\', '/')
        -not ($relative.StartsWith('bin/', [System.StringComparison]::OrdinalIgnoreCase) -or
              $relative.StartsWith('obj/', [System.StringComparison]::OrdinalIgnoreCase))
    })
    foreach ($relativeInput in 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.config') {
        $input = Join-Path $RepoRoot $relativeInput
        if (Test-Path -LiteralPath $input -PathType Leaf) { $inputs += Get-Item -LiteralPath $input }
    }
    $newestInput = ($inputs | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
    $oldestOutput = (($requiredOutputs | ForEach-Object { Get-Item -LiteralPath $_ }) |
        Measure-Object -Property LastWriteTimeUtc -Minimum).Minimum
    return $newestInput -le $oldestOutput
}

function Test-PackagedAppStartup {
    <#
      Launch the packaged app and require it to STAY up. A distribution that is missing or
      mismatching a runtime assembly does not fail the build — it throws at boot, which is exactly
      what the deps.json patch exists to prevent. Dump the captured output on an early exit so the
      cause is visible without re-running by hand.

      The payload is apphost-free, so callers pass the user's dotnet host and the app dll - EXCEPT
      when testing a launcher (OpenDevelop.exe/OpenDevelopARM64.exe): that process itself spawns a
      CHILD "dotnet exec OpenDevelop.dll" and waits on it, so $FilePath's own window (if any) is
      not where the real WPF window/file locks live. Pass -KillProcessTree in that case so cleanup
      (below) reaches the child too - otherwise it becomes an orphan still holding the payload's
      files open, which corrupts the same .zip step this function's own file-lock comment is about.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [switch]$KillProcessTree
    )
    $tag = [System.Guid]::NewGuid().ToString('N')
    # Start-Process rejects using ONE file for both streams; keep two.
    $outLog = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$tag.out.log"
    $errLog = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$tag.err.log"

    $startArgs = @{
        FilePath               = $FilePath
        RedirectStandardOutput = $outLog
        RedirectStandardError  = $errLog
        PassThru               = $true
    }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
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
    if (-not $proc.HasExited) {
        if ($KillProcessTree) {
            # taskkill /T reaches the whole tree (the launcher AND the "dotnet exec" child it
            # spawned and is blocked on); Stop-Process alone only ever touches $proc.Id itself.
            & taskkill /PID $proc.Id /T /F | Out-Null
        } else {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -Force $outLog, $errLog -ErrorAction SilentlyContinue
    Write-Host 'Packaged app startup smoke test passed'
}


# The payload is one download for every architecture, so every IL-only assembly in it must be
# AnyCPU: an ARM64-only one cannot even be loaded by an x64 OpenDevelop ("The assembly architecture
# is not compatible with the current process architecture"). Exempt are:
#   - runtimes\ and per-RID win-x64/win-arm64/win-x86 folders, which exist precisely to carry
#     several architectures side by side for the host to pick from;
#   - mixed-mode C++/CLI assemblies (not ILOnly, e.g. DirectWriteForwarder.dll), architecture-
#     specific by nature and selected through the patched deps.json.
# A violation is almost always stale Debug output from a developer build - clean that project.
function Test-WindowsPayloadAssemblyArchitecture {
    param([Parameter(Mandatory)][string]$PayloadRoot)

    $violations = foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File -Include '*.dll', '*.exe') {
        $relative = $file.FullName.Substring($PayloadRoot.Length).TrimStart('\', '/')
        if ($relative -match '(^|[\\/])(runtimes|win-x64|win-arm64|win-x86)[\\/]') { continue }
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            $cor = $pe.PEHeaders.CorHeader
            if ($null -eq $cor) { continue }
            if (($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::ILOnly) -eq 0) { continue }
            $machine = $pe.PEHeaders.CoffHeader.Machine
            $requires32Bit = ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::Requires32Bit) -ne 0 -and
                ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::Prefers32Bit) -eq 0
            if ($machine -ne [System.Reflection.PortableExecutable.Machine]::I386 -or $requires32Bit) {
                "$relative ($machine$(if ($requires32Bit) { ', 32-bit required' }))"
            }
        } catch [System.BadImageFormatException] {
        } finally { $stream.Dispose() }
    }
    if ($violations) {
        throw "Distribution payload contains $(@($violations).Count) architecture-specific managed assemblies; it must be AnyCPU:`n  " + ($violations -join "`n  ")
    }
}

function Test-WindowsDistributionPayload {
    param([Parameter(Mandatory)][string]$PayloadRoot)

    $app = Join-Path $PayloadRoot 'OpenDevelop.dll'
    if (-not (Test-Path -LiteralPath $app)) { throw "Distribution payload has no OpenDevelop.dll: $PayloadRoot" }
    # OpenDevelop.dll itself is AnyCPU and apphost-free: it is started as "dotnet exec OpenDevelop.dll".
    # The launcher (Build-Launchers) supplies the ONE file that is unavoidably architecture-specific -
    # the native apphost the user double-clicks - as two separate files, one per Windows
    # architecture, so the platform-neutral payload can still be a single download. Verify both are
    # present and actually built for the architecture their name promises: a launcher of the wrong
    # PE machine type would fail with a bare "not a valid Win32 application" and no other clue.
    foreach ($launcherSpec in @(
        @{ Name = 'OpenDevelop.exe'; ExpectedMachine = 0x8664 },
        @{ Name = 'OpenDevelopARM64.exe'; ExpectedMachine = 0xAA64 }
    )) {
        $launcherPath = Join-Path $PayloadRoot $launcherSpec.Name
        if (-not (Test-Path -LiteralPath $launcherPath)) {
            throw "Distribution payload is missing the launcher: $launcherPath"
        }
        $stream = [System.IO.File]::OpenRead($launcherPath)
        try {
            $reader = [System.IO.BinaryReader]::new($stream)
            $stream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "Not a PE executable: $launcherPath" }
            $machine = $reader.ReadUInt16()
            if ($machine -ne $launcherSpec.ExpectedMachine) {
                throw "Wrong launcher architecture: $launcherPath has machine 0x$('{0:X4}' -f $machine), expected 0x$('{0:X4}' -f $launcherSpec.ExpectedMachine)."
            }
        } finally { $stream.Dispose() }
    }
    Test-WindowsPayloadAssemblyArchitecture -PayloadRoot $PayloadRoot
    foreach ($companionExtension in '.dll', '.deps.json', '.runtimeconfig.json') {
        $companionPath = Join-Path $PayloadRoot "OpenDevelop.Bootstrap$companionExtension"
        if (-not (Test-Path -LiteralPath $companionPath)) {
            throw "Distribution payload is missing the launcher's companion file: $companionPath"
        }
    }
    # Every supported architecture's native WPF runtime must be present side by side; the host picks
    # the matching one from deps.json runtimeTargets at startup.
    foreach ($rid in 'win-x64', 'win-arm64') {
        $native = Join-Path $PayloadRoot "runtimes\$rid\native\PresentationNative_cor3.dll"
        if (-not (Test-Path -LiteralPath $native)) {
            throw "Distribution payload is missing the $rid WPF native runtime: $native"
        }
    }
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
    # The real WinUI 3 child is architecture-specific, so the single payload must carry one per
    # supported Windows RID under MicrosoftHost\<CLR major>\<rid>\. The bootstrap picks by CLR major
    # (from the designed app's runtimeconfig) and then by process architecture.
    $winUiHostRoot = Join-Path $addIns 'DisplayBindings\WinUIXamlDesigner\MicrosoftHost'
    foreach ($tfmDir in 'net9.0', 'net10.0') {
        foreach ($rid in 'win-x64', 'win-arm64') {
            $winUiHostExe = Join-Path $winUiHostRoot "$tfmDir\$rid\WinUIXamlDesigner.MicrosoftHost.dll"
            if (-not (Test-Path -LiteralPath $winUiHostExe)) {
                throw "Distribution payload is missing the Microsoft WinUI design host ($tfmDir/$rid): $winUiHostExe. " +
                    "Check that Build-MicrosoftDesignerHosts built " +
                    "src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj " +
                    "with Visual Studio's own MSBuild.exe (dotnet build cannot build UseWinUI projects) for both win-x64 and win-arm64."
            }
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
    param([Parameter(Mandatory)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $prefix = "OpenDevelop-win/"
        if (-not ($archive.Entries.FullName -contains "${prefix}OpenDevelop.dll")) { throw "ZIP lacks ${prefix}OpenDevelop.dll: $ZipPath" }
        foreach ($launcherName in 'OpenDevelop.exe', 'OpenDevelopARM64.exe') {
            if (-not ($archive.Entries.FullName -contains "${prefix}${launcherName}")) {
                throw "ZIP is missing the launcher ${launcherName}: $ZipPath"
            }
        }
        foreach ($companionExtension in '.dll', '.deps.json', '.runtimeconfig.json') {
            $companionPath = "${prefix}OpenDevelop.Bootstrap$companionExtension"
            if (-not ($archive.Entries.FullName -contains $companionPath)) {
                throw "ZIP is missing the launcher's companion file ${companionPath}: $ZipPath"
            }
        }
        if (-not ($archive.Entries.FullName | Where-Object { $_ -like "${prefix}AddIns/*.addin" })) { throw "ZIP lacks addin manifests: $ZipPath" }
        # Mirror the designer-host checks in Test-WindowsDistributionPayload - they must survive the
        # staging/zip round-trip, not just be present in the staged payload dir.
        $expected = @(
            "${prefix}AddIns/DisplayBindings/WpfDesign/Host/WpfDesign.SurfaceHost.dll",
            "${prefix}AddIns/DisplayBindings/WpfDesign/MicrosoftHost/MicrosoftWpfDesign.SurfaceHost.dll",
            "${prefix}AddIns/DisplayBindings/FormsDesigner/MicrosoftHost/MicrosoftFormsDesigner.Host.dll"
        )
        foreach ($tfmDir in 'net9.0', 'net10.0') {
            foreach ($rid in 'win-x64', 'win-arm64') {
                $expected += "${prefix}AddIns/DisplayBindings/WinUIXamlDesigner/MicrosoftHost/$tfmDir/$rid/WinUIXamlDesigner.MicrosoftHost.dll"
            }
        }
        foreach ($path in $expected) {
            if (-not ($archive.Entries.FullName -contains $path)) {
                throw "ZIP is missing a designer host: $path ($ZipPath)"
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
# Shared pipeline (one AnyCPU pass - see Invoke-DistributionPipeline below)
# ---------------------------------------------------------------------------------------------
#
# Restore runs up front inside Invoke-DistributionPipeline with
# -p:ProGpuWpfUseCurrentRuntimeIdentifier=false, so every project's obj/project.assets.json has a
# RID-less target. On Windows this also seeds LibreWPF.Sdk resolution for the AvalonEdit submodule,
# whose own global.json has no "msbuild-sdks" entry and shadows the repo-root one for every project
# beneath it (otherwise MSB4236 "The SDK 'LibreWPF.Sdk' specified could not be found").

# ---------------------------------------------------------------------------------------------
# Platform packaging
# ---------------------------------------------------------------------------------------------

function Invoke-MacPayload {
    Write-Host "==> Building framework-dependent .app bundle ($config)..."
    $env:DIST_CONFIG = $config
    & bash (Join-Path $repoRoot 'build/macos/build-application-bundle.sh')
    if ($LASTEXITCODE -ne 0) { throw "build-application-bundle.sh exited with code $LASTEXITCODE" }

    # Same as the Windows payload: out-of-process hosts resolve only from their own deps.json,
    # which declares some packages (System.Windows.Extensions, ...) only under runtimes/win.
    # Without the patch the host cannot load them on macOS even though the file is present.
    $bundleAddIns = Join-Path $repoRoot 'OpenDevelop.app/Contents/MacOS/AddIns'
    # `dotnet nuget locals global-packages --list` is process startup plus SDK resolution. All
    # child manifests use the same cache, so resolve it once rather than repeating it for every
    # out-of-process host manifest in the bundle.
    $nugetPackageRoot = Get-NuGetGlobalPackages
    $childDepsPaths = @(Get-ChildItem -LiteralPath $bundleAddIns -Recurse -File -Filter '*.deps.json' | Where-Object {
        Test-Path -LiteralPath ($_.FullName -replace '\.deps\.json$', '.runtimeconfig.json')
    } | ForEach-Object FullName)
    if ($childDepsPaths.Count -gt 0) {
        & $patchScript -DepsPath $childDepsPaths -NugetPackageRoot $nugetPackageRoot -Quiet
    }
    return (Join-Path $repoRoot 'OpenDevelop.app')
}

function Invoke-MacSmoke {
    Write-Host '==> Smoke-testing packaged app...'
    Test-PackagedAppStartup -FilePath $dotnet -ArgumentList @((Join-Path $repoRoot 'OpenDevelop.app/Contents/MacOS/OpenDevelop.dll'))
}

function Invoke-MacArchive {
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

function Invoke-WindowsPayload {
    <#
      Assembles OpenDevelop-win/ from artifacts that ALREADY exist on disk: the host publish
      output and the built AddIns/ tree. It compiles nothing except the launchers, so it is the
      cheap phase to re-run after rebuilding one AddIn with ./build.ps1 - and doing that is
      strictly better than hand-copying a DLL into the payload, because the by-name dedup and the
      out-of-process-host exemptions below are applied consistently every time.
    #>
    Assert-HostPublished

    Write-Host "==> Assembling the platform-neutral distribution payload ($config)..."
    # Keep the exact-clean-output contract without needlessly deleting and re-copying an unchanged
    # host publish closure. data/ and AddIns/ have different authoritative sources and are synced
    # below, so exclude them here instead of purging/recreating thousands of unchanged files.
    $payloadStepStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Sync-WindowsDirectoryMirror -Source $publishDir -Destination $payloadRoot -Description 'host publish into Windows payload' `
        -ExcludeDirectories @('AddIns', 'data') `
        -ExcludeFiles @('OpenDevelop.exe', 'OpenDevelopARM64.exe', 'OpenDevelop.Bootstrap.dll', 'OpenDevelop.Bootstrap.deps.json', 'OpenDevelop.Bootstrap.runtimeconfig.json')
    Write-DistributionStepElapsed $payloadStepStopwatch 'host mirror'

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
    if (Test-WindowsLaunchersCurrent -RepoRoot $repoRoot -PayloadRoot $payloadRoot) {
        Write-Host '==> Reusing current Windows launchers (inputs unchanged)...'
    }
    else {
        Write-Host '==> Building launchers (one native apphost per Windows architecture)...'
        Build-Launchers -RepoRoot $repoRoot -DotNet $dotnet -Configuration $config -PayloadRoot $payloadRoot
    }
    Write-DistributionStepElapsed $payloadStepStopwatch 'host cleanup and launchers'

    # A RID-less publish carries EVERY platform's native tree (linux/osx/unix) as well as Windows.
    # The other platforms are dead weight in the Windows package, so drop everything except the
    # Windows RIDs (win, win-x64, win-x86, win-arm64) before validating/zipping.
    foreach ($runtimesDir in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Directory -Filter 'runtimes' -ErrorAction SilentlyContinue) {
        foreach ($ridDir in Get-ChildItem -LiteralPath $runtimesDir.FullName -Directory -ErrorAction SilentlyContinue) {
            if ($ridDir.Name -eq 'win' -or $ridDir.Name -like 'win-*') { continue }
            Remove-Item -LiteralPath $ridDir.FullName -Recurse -Force
        }
    }

    Write-DistributionStepElapsed $payloadStepStopwatch 'runtime pruning'

    # OpenDevelop locates its addins and data at runtime by walking UP from the executable looking
    # for data/resources/languages/LanguageDefinition.xml (SharpDevelopMain.FindApplicationRootPath),
    # then loading *.addin from <root>/AddIns. The payload must therefore contain data/ and AddIns/
    # next to the executable so the walk resolves on the first step.
    Sync-WindowsDirectoryMirror -Source (Join-Path $repoRoot 'data') -Destination (Join-Path $payloadRoot 'data') -Description 'data into Windows payload'
    Write-DistributionStepElapsed $payloadStepStopwatch 'data mirror'

    # AddIn build outputs carry their full dependency closures. Anything already supplied by the
    # published host resolves from the application base directory, so skip those files by name
    # instead of copying ~2 GB and pruning afterwards. This also keeps stale XML docs, satellite
    # resources and native helpers from an old developer build out of the payload. The incremental
    # payload path intentionally retains AddIns/ and data/ across runs, so they must not contribute
    # names here: only the host-owned part of the payload is eligible for copy-local de-duplication.
    $hostFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($payloadRoot.Length).TrimStart('\', '/').Replace('\', '/')
        if (-not ($relative.StartsWith('AddIns/', [System.StringComparison]::OrdinalIgnoreCase) -or
                  $relative.StartsWith('data/', [System.StringComparison]::OrdinalIgnoreCase))) {
            [void]$hostFiles.Add($_.Name)
        }
    }

    $addInsSource = (Resolve-Path (Join-Path $repoRoot 'AddIns')).Path
    $addInsTarget = Join-Path $payloadRoot 'AddIns'

    # OpenDevelopAddinKind=OutOfProcessHost projects (WinForms/WPF/WinUI design-surface hosts) run
    # as their own separate "dotnet exec" child process with its own working directory - unlike an
    # InProcess addin, they cannot resolve a same-named dependency from files sitting beside the
    # app dll, so the by-name dedup below must not strip files out of their deployment folders.
    # Missing this once (FormsDesigner's Host\ folder losing PresentationFramework.dll, every
    # ProGPU.*.dll, System.Windows.Forms.dll, ...) made the WinForms designer's child host crash
    # before completing its handshake, surfacing only as an opaque
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
        'DisplayBindings\WpfDesign\MicrosoftHost',
        'LanguageServices\XamlLanguageServer.Wpf',
        'LanguageServices\WinUIXamlLanguageServer',
        'LanguageServices\UnoXamlLanguageServer',
        'LanguageServices\ProGpuWinUIXamlLanguageServer',
        'LanguageServices\LibreWpfXamlLanguageServer'
    )

    # The same holds for any other program that runs as its own process from an addin folder, e.g.
    # the SharpDbg.Cli debug adapter beside the in-process Debugger addin: stripping its
    # ICSharpCode.Decompiler/Microsoft.CodeAnalysis/Newtonsoft.Json (all also in the host) made it
    # exit with code 1 and no output, so every debug session "stopped" as soon as it started. A
    # *.runtimeconfig.json marks such a program; keep exactly the assets its deps.json lists, both
    # the managed ones and the per-RID native ones it has to load for itself.
    # Project references also copy-local stray OpenDevelop/MSBuild/Roslyn.Host runtimeconfigs into
    # in-process addin folders; those programs are host files and run from the payload root, so
    # they are skipped - honouring them would duplicate the host's whole closure into each folder.
    $ownProcessDependencies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($runtimeConfig in Get-ChildItem -LiteralPath $addInsSource -Recurse -File -Filter '*.runtimeconfig.json') {
        $appDir = $runtimeConfig.DirectoryName
        $appName = $runtimeConfig.Name -replace '\.runtimeconfig\.json$', ''
        # A stray copy has no program beside it (FormsDesigner\ carries Host\'s runtimeconfig and would
        # otherwise pull 119 host files into the in-process addin folder)
        if ($hostFiles.Contains("$appName.dll") -or -not (Test-Path -LiteralPath (Join-Path $appDir "$appName.dll"))) { continue }
        $deps = Join-Path $appDir "$appName.deps.json"
        if (-not (Test-Path -LiteralPath $deps)) { continue }
        $targets = (Get-Content -LiteralPath $deps -Raw | ConvertFrom-Json).targets
        foreach ($target in $targets.PSObject.Properties.Value) {
            foreach ($library in $target.PSObject.Properties.Value) {
                # runtime assets are copied flat into the folder, but a runtimeTargets asset keeps
                # its runtimes\<rid>\... path - dbgshim's three per-RID dbgshim.dll are exactly that
                # case, and they sit under a folder the filter above deliberately keeps. Record both
                # spellings of every asset: this set is only ever read with Contains against a real
                # file's full path, so an entry that does not exist costs nothing and neither layout
                # can be missed. Covering runtimeTargets is what stops the adapter's natives from
                # depending on their name not colliding with the host publish.
                foreach ($section in @($library.runtime, $library.runtimeTargets)) {
                    if (-not $section) { continue }
                    foreach ($asset in $section.PSObject.Properties.Name) {
                        if ($asset -like '#*') { continue } # "#_Imports" is a marker, not an asset
                        [void]$ownProcessDependencies.Add((Join-Path $appDir ([System.IO.Path]::GetFileName($asset))))
                        if ($asset -like '*/*') {
                            [void]$ownProcessDependencies.Add((Join-Path $appDir ($asset -replace '/', '\')))
                        }
                    }
                }
            }
        }
    }

    # Index source files once. The old filter issued one Test-Path for every XML document and a
    # nine-entry pipeline for every candidate; that became the dominant cost after copy itself was
    # incremental. An in-memory DLL index and short-circuiting loop retain the exact filter rules.
    $allAddInFiles = @(Get-ChildItem -LiteralPath $addInsSource -Recurse -File)
    $addInDllPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $allAddInFiles) {
        if ($file.Extension -eq '.dll') { [void]$addInDllPaths.Add($file.FullName) }
    }
    $addInFiles = foreach ($file in $allAddInFiles) {
        $name = $file.Name
        if ($name -like '*.pdb' -or $name -like '*.dylib' -or $name -like '*.so' -or
            $name -like 'LeXtudio.DevFlow.*' -or $name -like 'CliclickSharp*') { continue }
        $relative = $file.FullName.Substring($addInsSource.Length).TrimStart('\', '/')
        if ($relative -match '(^|[\\/])(ref|runtimes[\\/](linux|unix|osx))([\\/]|$)') { continue }
        if ($file.Extension -eq '.xml' -and $addInDllPaths.Contains([System.IO.Path]::ChangeExtension($file.FullName, '.dll'))) { continue }

        $relativeDir = Split-Path -Parent $relative
        $isOutOfProcessHost = $false
        foreach ($hostDir in $outOfProcessHostDirs) {
            if ($relativeDir -eq $hostDir -or $relativeDir.StartsWith("$hostDir\", [System.StringComparison]::OrdinalIgnoreCase)) {
                $isOutOfProcessHost = $true
                break
            }
        }
        if (-not $hostFiles.Contains($name) -or $isOutOfProcessHost -or $ownProcessDependencies.Contains($file.FullName)) { $file }
    }

    $expectedAddInFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $copiedAddInCount = 0
    $unchangedAddInCount = 0
    foreach ($file in $addInFiles) {
        $relative = $file.FullName.Substring($addInsSource.Length).TrimStart('\', '/')
        [void]$expectedAddInFiles.Add($relative)
        $destination = Join-Path $addInsTarget $relative
        $existing = Get-Item -LiteralPath $destination -ErrorAction SilentlyContinue
        if ($existing -and $existing.Length -eq $file.Length -and $existing.LastWriteTimeUtc -eq $file.LastWriteTimeUtc) {
            $unchangedAddInCount++
            continue
        }
        $destinationDir = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $copiedAddInCount++
    }
    $removedAddInCount = 0
    if (Test-Path -LiteralPath $addInsTarget) {
        Get-ChildItem -LiteralPath $addInsTarget -Recurse -File -Force | ForEach-Object {
            $relative = $_.FullName.Substring($addInsTarget.Length).TrimStart('\', '/')
            if (-not $expectedAddInFiles.Contains($relative)) {
                Remove-Item -LiteralPath $_.FullName -Force
                $removedAddInCount++
            }
        }
        Get-ChildItem -LiteralPath $addInsTarget -Recurse -Directory -Force |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object {
                if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) {
                    Remove-Item -LiteralPath $_.FullName -Force
                }
            }
    }
    Write-Host "    AddIns: copied $copiedAddInCount, unchanged $unchangedAddInCount, stale removed $removedAddInCount"
    Write-DistributionStepElapsed $payloadStepStopwatch 'AddIn synchronization'

    # Out-of-process AddIns have their own deps.json and their own application base directory.
    # Patch every staged dependency manifest, not only OpenDevelop.deps.json, so the Forms/WPF
    # designer hosts and language-service children also select the matching x64/ARM64 mixed-mode
    # LibreWPF and ProGPU runtime assemblies.
    $payloadDepsPaths = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter '*.deps.json' | Where-Object {
        $relative = $_.FullName.Substring($payloadRoot.Length).TrimStart('\', '/')
        $relative -eq 'OpenDevelop.deps.json' -or
        ($outOfProcessHostDirs | Where-Object { $relative.StartsWith("$_\", [System.StringComparison]::OrdinalIgnoreCase) })
    } | ForEach-Object FullName)
    if ($payloadDepsPaths.Count -gt 0) {
        & $patchScript -DepsPath $payloadDepsPaths -NugetPackageRoot (Get-NuGetGlobalPackages) -WindowsX64PackageRoot $windowsX64CanonicalFeed -WindowsArm64PackageRoot $windowsArm64CanonicalFeed -Quiet
    }
    Write-DistributionStepElapsed $payloadStepStopwatch 'dependency manifest patching'

    $appPath = Join-Path $payloadRoot 'OpenDevelop.dll'
    if (-not (Test-Path $appPath)) { throw "dist.ps1: packaged app not found: $appPath" }
    Test-WindowsDistributionPayload -PayloadRoot $payloadRoot
    Write-DistributionStepElapsed $payloadStepStopwatch 'payload validation'
    Write-Host "Payload ready: $payloadRoot"

    return $payloadRoot
}

function Invoke-WindowsSmoke {
    if (-not (Test-Path $payloadRoot)) {
        throw "dist.ps1: no payload to smoke-test at $payloadRoot. Run './dist.ps1 -Phase payload' first."
    }
    $appPath = Join-Path $payloadRoot 'OpenDevelop.dll'

    Write-Host '==> Smoke-testing packaged app...'
    Test-PackagedAppStartup -FilePath $dotnet -ArgumentList @($appPath) -WorkingDirectory $payloadRoot

    # Also exercise the ACTUAL double-click path end to end, not just "dotnet exec": the launcher
    # matching this build machine's own architecture is the one this machine can run directly.
    $hostArchitectureLauncher = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq
        [System.Runtime.InteropServices.Architecture]::Arm64) { 'OpenDevelopARM64.exe' } else { 'OpenDevelop.exe' }
    Write-Host "==> Smoke-testing the $hostArchitectureLauncher launcher..."
    Test-PackagedAppStartup -FilePath (Join-Path $payloadRoot $hostArchitectureLauncher) -WorkingDirectory $payloadRoot -KillProcessTree
}

function Invoke-WindowsArchive {
    if (-not (Test-Path $payloadRoot)) {
        throw "dist.ps1: no payload to zip at $payloadRoot. Run './dist.ps1 -Phase payload' first."
    }

    Write-Host '==> Building .zip...'
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadRoot, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
    Test-WindowsDistributionZip -ZipPath $zipPath

    return $zipPath
}

function Invoke-RestorePhase {
    Write-Host '==> Restoring solution...'
    Restore-Solution -DotNet $dotnet -Solution $sln -ExtraProperties @('-p:ProGpuWpfUseCurrentRuntimeIdentifier=false')

    # The hot-reload agents are built through the IDE's deployment targets rather than being
    # solution entries.  The later distribution build deliberately uses --no-restore, so make
    # their assets explicit here instead of allowing a missing project.assets.json to fail the
    # AddIns phase.
    $hotReloadAgent = Join-Path $repoRoot 'src/Main/HotReload/WpfHotReload.Agent/WpfHotReload.Agent.csproj'
    Write-Host '==> Restoring LibreWPF hot-reload agent...'
    Invoke-Native $dotnet restore $hotReloadAgent '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false'

    $microsoftHotReloadAgent = Join-Path $repoRoot 'src/Main/HotReload/WpfHotReload.Agent.Microsoft/WpfHotReload.Agent.Microsoft.csproj'
    Write-Host '==> Restoring Microsoft WPF hot-reload agent...'
    Invoke-Native $dotnet restore $microsoftHotReloadAgent '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false'
}

function Invoke-HostPhase {
    # One AnyCPU pass: no -p:RuntimeIdentifier anywhere, so the output is a single RID-less
    # (platform-neutral) payload that carries every architecture's assets under runtimes/.
    #
    # Clear the shared intermediate output so publish cannot reuse artifacts left by a previous
    # build (of this RID, or of a differently-RID'd pass before this one). This distribution
    # intentionally remains framework-dependent and uses the installed .NET runtime; the
    # SDK-generated apphost is only the native entry point and does not bundle that runtime.
    Write-Host '==> Cleaning intermediate outputs...'
    $hostObj = Join-Path $repoRoot "src/Main/SharpDevelop/obj/$config/$tfm"
    Remove-RepoGeneratedDirectory $hostObj

    # Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources (WPF theme
    # resource blob) can otherwise stale-cross from a previous build and produce a 12-byte
    # corrupt file that crashes at boot with EndOfStreamException in
    # FindResource/LoadThemedDictionary. Clear obj/ and bin/ entirely (not just the $config
    # subfolder) since an explicit -p:RuntimeIdentifier can binplace this project's output
    # under an additional RID-suffixed subfolder that the plain "$sub/$config" path misses.
    $corePres = Join-Path $repoRoot 'src/Main/ICSharpCode.Core.Presentation'
    foreach ($sub in 'obj', 'bin') {
        $dir = Join-Path $corePres $sub
        Remove-RepoGeneratedDirectory $dir
    }

    Write-Host "==> Publishing framework-dependent AnyCPU app ($config)..."
    Remove-RepoGeneratedDirectory $publishDir
    Invoke-Native $dotnet publish $hostProject -c $config --self-contained false `
        "-p:OpenDevelopDistributionBuild=true" `
        "-p:PublishDir=$publishDir" `
        "-p:ProGpuWpfUseCurrentRuntimeIdentifier=false"

    if (-not (Test-Path $publishDir)) {
        throw "dist.ps1: host publish directory not found: $publishDir"
    }

    # NuGet conflict resolution omits LibreWinForms from the standard publish closure.
    # Patch the final manifest and copy its matching runtime files.
    & $patchScript $depsJson (Get-NuGetGlobalPackages) -WindowsX64PackageRoot $windowsX64CanonicalFeed -WindowsArm64PackageRoot $windowsArm64CanonicalFeed
}

function Invoke-AddInsPhase {
    Assert-HostPublished

    # The developer solution deliberately contains test projects. Their Build hooks compile
    # independent sample applications (including a second, incompatible Uno SDK graph), none of
    # which belong in a distributable payload. Build a short-lived filtered solution rather than
    # letting those test-only hooks contaminate the release closure.
    $distributionSolution = Join-Path $repoRoot ".opendevelop-distribution-$([guid]::NewGuid().ToString('N')).slnx"
    $solutionText = Get-Content -LiteralPath $sln -Raw
    $solutionText = [regex]::Replace($solutionText, '(?m)^\s*<Project Path="tests/OpenDevelop\.(?:IntegrationTests|Base\.Tests)/OpenDevelop\.[^"]+\.csproj" />\s*\r?\n', '')
    Set-Content -LiteralPath $distributionSolution -Value $solutionText -NoNewline

    # Copy-local filtering only needs filename+extension matching, not a writable clone of the
    # published host. Generate that exact file-name set once and let Directory.Build.targets read
    # it in every add-in project. This replaces a full publish-tree copy plus a recursive glob per
    # add-in, while keeping the verified publish output immutable.
    $hostPublishManifestDirectory = New-TempDir
    $hostPublishManifest = Join-Path $hostPublishManifestDirectory 'OpenDevelop.host-publish-files.txt'
    Get-ChildItem -LiteralPath $publishDir -Recurse -File |
        ForEach-Object { $_.Name } |
        Sort-Object -Unique |
        Set-Content -LiteralPath $hostPublishManifest
    try {
        # AddIns/ is an ignored deployment directory, not source. A partial or cross-platform
        # build used to leave .so/.dylib, ref/, package build props and stale assemblies here;
        # later package runs then copied them even though the current solution never produced
        # them. Start every distribution build with a genuinely empty deployment root.
        Write-Host '==> Cleaning generated AddIn deployment root...'
        $generatedAddIns = Join-Path $repoRoot 'AddIns'
        Remove-RepoGeneratedDirectory $generatedAddIns
        New-Item -ItemType Directory -Path $generatedAddIns | Out-Null

        Write-Host '==> Building distribution AddIns without shared runtime copies...'
        $addInProperties = @(
            '-p:OpenDevelopDistributionBuild=true',
            "-p:OpenDevelopDistributionRidFamily=$ridFamily",
            "-p:OpenDevelopHostPublishManifest=$hostPublishManifest",
            '-p:ProGpuWpfCopyPackageRuntimeAssets=false',
            '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false',
            # A solution build unsets Configuration for a ProjectReference to a project the solution
            # does not list, so ~20 such projects (DesignerCanvas, Designer.Shell, Widgets, TreeView,
            # SharpDevelop.EnvDTE, ...) built as Debug - reusing whatever a developer's last Debug
            # build left in obj\Debug, which on an ARM64 machine is ARM64. That is how ARM64-only
            # add-ins reached the AnyCPU payload. Let every reference build as the distribution's own
            # configuration instead of keeping the solution and that list in sync by hand.
            '-p:ShouldUnsetParentConfigurationAndPlatform=false'
        )
        if ($UseAddInStaging) {
            # Keep the experimental closure outside AddIns/. It must be cleaned once, before the
            # project graph starts: cleaning a leaf in BuildDependsOn is unsafe because MSBuild
            # can schedule another build request for that same project while a dependent is still
            # consuming its reference assembly.
            $stagingRoot = Join-Path $repoRoot "obj/OpenDevelopDistributionStaging/$config/"
            Remove-RepoGeneratedDirectory $stagingRoot
            New-Item -ItemType Directory -Path $stagingRoot | Out-Null
            $addInProperties += "-p:OpenDevelopDistributionStagingRoot=$stagingRoot"
            Write-Host "==> Using isolated AddIn staging: $stagingRoot"
        }
        Build-Solution -DotNet $dotnet -Solution $distributionSolution -Configuration $config -ExtraProperties ($addInProperties + (Get-PinnedGitVersionProperties -GlobalAssemblyInfoPath (Join-Path $repoRoot 'src/Main/GlobalAssemblyInfo.cs'))) -MaxCpuCount $AddInMaxCpuCount
    }
    finally {
        Remove-Item -Recurse -Force $hostPublishManifestDirectory -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $distributionSolution -Force -ErrorAction SilentlyContinue
    }

    # The solution traversal may copy reference assemblies over the original PublishDir through
    # cached project state. Restore the authoritative package runtime payload after the build.
    & $patchScript $depsJson (Get-NuGetGlobalPackages) -WindowsX64PackageRoot $windowsX64CanonicalFeed -WindowsArm64PackageRoot $windowsArm64CanonicalFeed
}

function Invoke-DesignerHostsPhase {
    Assert-HostPublished
    Write-Host '==> Building Microsoft-framework designer hosts (VS MSBuild)...'
    Build-MicrosoftDesignerHosts -RepoRoot $repoRoot -Configuration $config `
        -PinnedGitVersionProperties (Get-PinnedGitVersionProperties -GlobalAssemblyInfoPath (Join-Path $repoRoot 'src/Main/GlobalAssemblyInfo.cs'))

    # These build straight into AddIns/ too, so re-assert the authoritative deps payload for the
    # same reason the AddIns phase does.
    & $patchScript $depsJson (Get-NuGetGlobalPackages) -WindowsX64PackageRoot $windowsX64CanonicalFeed -WindowsArm64PackageRoot $windowsArm64CanonicalFeed
}

function Get-DistributionPhases {
    # Canonical order. "designer-hosts" needs Visual Studio's MSBuild and is Windows-only.
    if ($IsWindows) { return @('restore', 'host', 'addins', 'designer-hosts', 'payload', 'smoke', 'zip') }
    return @('restore', 'host', 'addins', 'payload', 'smoke', 'zip')
}

function Resolve-SelectedPhases {
    $all = Get-DistributionPhases

    foreach ($name in (@($Phase) + @($From) | Where-Object { $_ })) {
        if ($all -notcontains $name) {
            throw "dist.ps1: unknown phase '$name'. Valid phases for this platform: $($all -join ', ')"
        }
    }

    if ($Phase.Count -gt 0 -and $From) {
        throw 'dist.ps1: pass either -Phase or -From, not both.'
    }
    if ($Phase.Count -gt 0) {
        # Preserve canonical order regardless of the order the caller listed them in.
        return @($all | Where-Object { $Phase -contains $_ })
    }
    if ($From) {
        return @($all[$all.IndexOf($From)..($all.Count - 1)])
    }
    if ($SkipPublish) {
        # Back-compat: -SkipPublish means "reuse existing build output, just package it".
        return @($all | Where-Object { $_ -notin @('restore', 'host', 'addins', 'designer-hosts') })
    }
    return $all
}

function Invoke-DistributionPipeline {
    $selected = Resolve-SelectedPhases
    Write-Host "==> Phases: $($selected -join ' -> ')"

    if ($Kill) {
        Write-Host '==> Stopping OpenDevelop and lingering build servers / design hosts...'
        Get-Process OpenDevelop, OpenDevelopARM64, dotnet, MSBuild -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }

    $artifact = $null
    foreach ($name in $selected) {
        $phaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        switch ($name) {
            'restore'        { Invoke-RestorePhase }
            'host'           { Invoke-HostPhase }
            'addins'         { Invoke-AddInsPhase }
            'designer-hosts' { Invoke-DesignerHostsPhase }
            'payload'        { $artifact = if ($IsWindows) { Invoke-WindowsPayload } else { Invoke-MacPayload } }
            'smoke'          { if ($IsWindows) { Invoke-WindowsSmoke } else { Invoke-MacSmoke } }
            'zip'            { $artifact = if ($IsWindows) { Invoke-WindowsArchive } else { Invoke-MacArchive } }
        }
        $phaseStopwatch.Stop()
        Write-Host ("==> Phase '{0}' completed in {1:N1}s" -f $name, $phaseStopwatch.Elapsed.TotalSeconds)
    }

    if (-not $artifact) {
        $artifact = if ($IsWindows) { $payloadRoot } else { Join-Path $repoRoot 'OpenDevelop.app' }
    }
    return $artifact
}

if ($ListPhases) {
    Write-Host "Phases for this platform (canonical order):"
    Get-DistributionPhases | ForEach-Object { "  $_" }
    Write-Host ''
    Write-Host 'Examples:'
    Write-Host '  ./dist.ps1                        # everything (unchanged default)'
    Write-Host '  ./dist.ps1 -Phase payload         # re-assemble OpenDevelop-win from what is already built'
    Write-Host '  ./dist.ps1 -Phase payload,smoke   # ...and verify it starts'
    Write-Host '  ./dist.ps1 -From payload          # payload, smoke and zip'
    Write-Host '  ./dist.ps1 -Phase addins,payload  # rebuild AddIns, then re-assemble'
    Write-Host ''
    Write-Host 'To patch a published payload after changing ONE project:'
    Write-Host '  ./build.ps1 <project> -Configuration Release'
    Write-Host '  ./dist.ps1 -Phase payload'
    exit 0
}

$artifact = Invoke-DistributionPipeline

Write-Host ''
Write-Host "Done: $artifact"
