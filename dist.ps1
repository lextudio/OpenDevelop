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
#   Windows -> OpenDevelop-win/ payload directory, then OpenDevelop-windows.zip
#
# Note the app runs on LibreWPF (portable WPF) on Windows too — SharpDevelop.csproj strips the
# Microsoft.WindowsDesktop.App runtime framework on EVERY platform — which is why the LibreWinForms
# deps.json patch and the LibreWPF.Transport overlay below are not macOS-specific. The Win32
# compatibility shims (kernel32.dll, user32.dll, ...) the macOS bundle carries are emitted by
# LibreWPF.Sdk only on OSX/Linux and must NOT appear on Windows, where those names belong to the
# real OS DLLs; that split is handled by OpenDevelopDistributionRidFamily, not here.
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
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force

$config = $Configuration
$tfm = 'net10.0-windows'
$sln = Join-Path $repoRoot 'OpenDevelop.Mvp.slnx'
$hostProject = Join-Path $repoRoot 'src/Main/SharpDevelop/SharpDevelop.csproj'
$dotnet = Find-DotNetHost
$publishDir = Join-Path $repoRoot "src/Main/SharpDevelop/bin/$config/$tfm/publish"
$depsJson = Join-Path $publishDir 'OpenDevelop.deps.json'
$patchScript = Join-Path $repoRoot 'build/patch-librewinforms-deps.ps1'

# The Addin SDK's OpenDevelopPruneAddinDeploymentAssets target drops runtimes/win*, linux* and
# unix* only for the 'osx' family; on Windows those win* assets are exactly what the payload needs.
$ridFamily = if ($IsWindows) { 'win' } else { 'osx' }

function New-TempDir {
    $p = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $p | Out-Null
    return $p
}

function Sync-LibreWpfTransportRuntime([string]$publishDir) {
    # LibreWPF.Sdk supplies the compile-time reference surface, but the .NET runtime pack also
    # contains assemblies with the same WPF simple names. Publish can therefore select the latter
    # by basename even though LibreWPF.Transport is the resolved package. That produces a subtly
    # mixed runtime (for example an old WindowsBase.dll without Dispatcher.NativeInputPump).
    # Always overlay the exact managed transport payload that restore selected.
    $assets = Join-Path $repoRoot 'src/Main/SharpDevelop/obj/project.assets.json'
    if (-not (Test-Path $assets)) { throw "LibreWPF transport sync requires restore assets: $assets" }
    $transportVersion = ((Get-Content $assets -Raw | ConvertFrom-Json).libraries.PSObject.Properties |
        Where-Object { $_.Name -like 'LibreWPF.Transport/*' } |
        Select-Object -First 1).Name -replace '^LibreWPF.Transport/', ''
    if (-not $transportVersion) { throw 'LibreWPF.Transport was not resolved for the OpenDevelop host.' }
    $nugetPackages = ((& $dotnet nuget locals global-packages --list) | Select-String '^global-packages: ').Line -replace '^global-packages:\s*', ''
    $transportRuntime = Join-Path $nugetPackages "librewpf.transport/$transportVersion/lib/net10.0"
    if (-not (Test-Path $transportRuntime)) { throw "LibreWPF transport runtime payload not found: $transportRuntime" }
    Copy-Item -Path (Join-Path $transportRuntime '*') -Destination $publishDir -Recurse -Force
    Write-Host "Synced LibreWPF.Transport $transportVersion runtime payload into publish output"
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

# ---------------------------------------------------------------------------------------------
# Shared pipeline
# ---------------------------------------------------------------------------------------------

# Restore the solution up front. The build below runs with --no-restore, so a stale or corrupt
# project.assets.json left by an earlier restore (e.g. after a NuGet.config change or a restore
# for a different RID/TFM combination) would otherwise surface as an obscure "ResolvePackageAssets"
# NullReferenceException deep inside the build — exactly the failure mode seen for the AspNetCore
# projects. Restore is incremental, so this stays cheap when nothing changed; it also fails fast
# with a clear error instead of leaving a half-built tree. On Windows it additionally seeds
# LibreWPF.Sdk resolution for the AvalonEdit submodule, whose own global.json has no
# "msbuild-sdks" entry and shadows the repo-root one for every project beneath it (otherwise
# MSB4236 "The SDK 'LibreWPF.Sdk' specified could not be found").
Write-Host '==> Restoring solution...'
Restore-Solution -DotNet $dotnet -Solution $sln

# Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources (WPF theme resource
# blob) can otherwise stale-cross from a previous build and produce a 12-byte corrupt file that
# crashes at boot with EndOfStreamException in FindResource / LoadThemedDictionary.
$corePres = Join-Path $repoRoot 'src/Main/ICSharpCode.Core.Presentation'
foreach ($sub in 'obj', 'bin') {
    $dir = Join-Path $corePres "$sub/$config"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

if (-not $SkipPublish) {
    # Clear the shared intermediate output so publish cannot reuse artifacts left by a previous
    # build. This distribution intentionally remains framework-dependent and uses the installed
    # .NET runtime; the SDK-generated apphost is only the native entry point and does not bundle
    # that runtime.
    Write-Host '==> Cleaning intermediate outputs...'
    $hostObj = Join-Path $repoRoot "src/Main/SharpDevelop/obj/$config/$tfm"
    if (Test-Path $hostObj) { Remove-Item -Recurse -Force $hostObj }

    Write-Host "==> Publishing framework-dependent app ($config)..."
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Invoke-Native $dotnet publish $hostProject -c $config --self-contained false `
        "-p:OpenDevelopDistributionBuild=true" `
        "-p:PublishDir=$publishDir"

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
    Sync-LibreWpfTransportRuntime $publishDir

    # Some projects write to OpenDevelopHostPublishDir while computing their distribution
    # closure. Give that build a disposable copy so the verified host deployment above remains
    # immutable.
    $hostPublishSnapshot = New-TempDir
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $hostPublishSnapshot -Recurse -Force

    Write-Host '==> Cleaning stale AddIn outputs...'
    Get-ChildItem (Join-Path $repoRoot 'AddIns') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.dll', '.dylib', '.so', '.pdb' } |
        Remove-Item -Force

    Write-Host '==> Building distribution AddIns without shared runtime copies...'
    Build-Solution -DotNet $dotnet -Solution $sln -Configuration $config -ExtraProperties @(
        '-p:OpenDevelopDistributionBuild=true',
        "-p:OpenDevelopDistributionRidFamily=$ridFamily",
        "-p:OpenDevelopHostPublishDir=$hostPublishSnapshot",
        '-p:ProGpuWpfCopyPackageRuntimeAssets=false'
    )
    Remove-Item -Recurse -Force $hostPublishSnapshot

    # The solution traversal may copy reference assemblies over the original PublishDir through
    # cached project state. Restore the authoritative package runtime payload only after every
    # build has completed.
    & $patchScript $depsJson $nugetPackages
    Sync-LibreWpfTransportRuntime $publishDir
}
else {
    Write-Host '==> Skipping publish (-SkipPublish)'
}

if (-not (Test-Path $publishDir)) {
    throw "dist.ps1: framework-dependent publish directory not found: $publishDir"
}

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
    $payloadRoot = Join-Path $repoRoot 'OpenDevelop-win'
    $zipPath = Join-Path $repoRoot 'OpenDevelop-windows.zip'

    Write-Host "==> Assembling distribution payload ($config)..."
    if (Test-Path $payloadRoot) { Remove-Item -Recurse -Force $payloadRoot }
    New-Item -ItemType Directory -Path $payloadRoot | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $payloadRoot -Recurse -Force

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
        $relativeDir = Split-Path -Parent ($_.FullName.Substring($addInsSource.Length).TrimStart('\', '/'))
        $isOutOfProcessHost = $outOfProcessHostDirs | Where-Object { $relativeDir -eq $_ -or $relativeDir.StartsWith("$_\") }
        -not ($name -like '*.pdb') -and
        -not ($name -like 'LeXtudio.DevFlow.*') -and
        -not ($name -like 'CliclickSharp*') -and
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
    Write-Host "Payload ready: $payloadRoot"

    Write-Host '==> Smoke-testing packaged app...'
    Test-PackagedAppStartup -ExePath $exePath -WorkingDirectory $payloadRoot

    Write-Host '==> Building .zip...'
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadRoot, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)

    return $zipPath
}

$artifact = if ($IsWindows) { Invoke-WindowsPackaging } else { Invoke-MacPackaging }

Write-Host ''
Write-Host "Done: $artifact"
