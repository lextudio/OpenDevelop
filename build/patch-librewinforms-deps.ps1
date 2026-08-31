#!/usr/bin/env pwsh
#
# Patches a .deps.json to add the real LibreWinForms.System.Windows.Forms/.WindowsFormsIntegration
# runtime assets that NuGet/RAR's conflict resolution drops from deps.json generation, because it
# picks the ref-pack-provided "Microsoft.WindowsDesktop.App" shared-framework component as the
# winner for System.Windows.Forms/WindowsFormsIntegration - a component that doesn't exist at all
# off Windows, causing FileNotFoundException at the ProGpuWpfSdkPortableBootstrap module initializer
# (the first thing that touches WindowsFormsHost) long before AddInTree even loads.
#
# The DLLs themselves are already copied into the output directory correctly by
# ProGPU.Wpf.Sdk.targets' own _ProGpuWpfSdkCopyPortableWinFormsCompatRuntimeAssets target - only
# the deps.json bookkeeping that the CoreCLR host uses to decide what's *allowed* to load is
# missing/wrong. See Directory.Build.targets' _ReplaceWindowsDesktopRefPackWinFormsFacades for the
# matching *compile-time* half of this fix.
#
# PowerShell port of the former patch-librewinforms-deps.py, so dist.ps1 no longer depends on a
# working python3/python interpreter being installed (a Windows machine without Python still has
# %LocalAppData%\Microsoft\WindowsApps\python.exe / python3.exe as Microsoft Store alias stubs,
# which made that failure mode confusing).

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$DepsPath,
    [Parameter(Mandatory, Position = 1)][string]$NugetPackageRoot
)

$ErrorActionPreference = 'Stop'

function Find-PackageVersion([string]$nugetPackageRoot, [string]$packageIdLower) {
    $pattern = Join-Path $nugetPackageRoot $packageIdLower
    if (-not (Test-Path $pattern)) { return $null }
    $candidates = Get-ChildItem -Path $pattern -Directory -ErrorAction SilentlyContinue
    if (-not $candidates) { return $null }
    # Prefer the newest by mtime - there should only ever be one version installed anyway.
    return ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).Name
}

# A handful of these packages (System.Windows.Extensions in particular) ship a Windows-only
# implementation under runtimes/win/lib/net10.0 PLUS a stub under lib/net10.0 that throws
# PlatformNotSupportedException on every member - that stub is what non-Windows platforms fall
# back to at runtime, and what the ref-pack substitution points compile-time references at. On
# Windows, always prefer the runtimes/win subtree when a package ships one; fall back to plain
# lib/net10.0 for packages that don't split by RID at all (most of the others below are genuinely
# cross-platform and have no runtimes/win subtree, so this fallback is what resolves them).
# Copying the throwing stub over an already-correct Windows assembly is exactly the FATAL
# "System.Windows.Extensions types are not supported on this platform" crash this caused once
# dist.ps1 started actually running this script on Windows (previously masked by python3/python
# never being found there at all).

function Get-VersionSortKey([string]$versionDir) {
    $stable = $versionDir.Split('-', 2)[0]
    return ,($stable.Split('.') | ForEach-Object { if ($_ -match '^\d+$') { [int]$_ } else { 0 } })
}

function Find-Net10Asset([string]$nugetPackageRoot, [string]$packageIdLower, [string]$filename) {
    # Returns @{ Version = <version>; RelativeDir = <relative dir under the version folder> } for
    # the best match, or $null. relativeDirCandidates is tried in order per platform.
    $relativeDirCandidates = if ($IsWindows) { @('runtimes/win/lib/net10.0', 'lib/net10.0') } else { @('lib/net10.0') }
    foreach ($relativeDir in $relativeDirCandidates) {
        $depth = $relativeDir.Split('/').Count + 1 # + 1 for the version folder itself
        $pattern = Join-Path $nugetPackageRoot "$packageIdLower/10.*/$relativeDir/$filename"
        $candidates = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue
        if (-not $candidates) { continue }

        $best = $candidates | ForEach-Object {
            $dir = $_.Directory
            for ($i = 1; $i -lt $depth; $i++) { $dir = $dir.Parent }
            [pscustomobject]@{ VersionDir = $dir.Name }
        } | Sort-Object -Property @{ Expression = { , (Get-VersionSortKey $_.VersionDir) } } | Select-Object -Last 1

        return @{ Version = $best.VersionDir; RelativeDir = $relativeDir }
    }
    return $null
}

$sysformsPkgId = 'LibreWinForms.System.Windows.Forms'
$winintPkgId = 'LibreWinForms.WindowsFormsIntegration'
$progpudrawingPkgId = 'ProGPU.System.Drawing.Common'
$transportPkgId = 'LibreWPF.Transport'

$sysformsVersion = Find-PackageVersion $NugetPackageRoot $sysformsPkgId.ToLowerInvariant()
if (-not $sysformsVersion) {
    Write-Host "patch-librewinforms-deps.ps1: $sysformsPkgId not found under $NugetPackageRoot, skipping"
    return
}

# WindowsFormsIntegration is optional - not every project that pulls in System.Windows.Forms
# also uses WindowsFormsHost. Only add its entry when the package is actually installed.
$winintVersion = Find-PackageVersion $NugetPackageRoot $winintPkgId.ToLowerInvariant()
$progpudrawingVersion = Find-PackageVersion $NugetPackageRoot $progpudrawingPkgId.ToLowerInvariant()
$transportVersion = Find-PackageVersion $NugetPackageRoot $transportPkgId.ToLowerInvariant()

$deps = Get-Content -Raw -Path $DepsPath | ConvertFrom-Json -AsHashtable

$sysformsKey = "$sysformsPkgId/$sysformsVersion"
$winintKey = if ($winintVersion) { "$winintPkgId/$winintVersion" } else { $null }
$progpudrawingKey = if ($progpudrawingVersion) { "$progpudrawingPkgId/$progpudrawingVersion" } else { $null }

if (-not $deps.ContainsKey('targets')) { $deps['targets'] = @{} }
foreach ($tfm in @($deps['targets'].Keys)) {
    $libs = $deps['targets'][$tfm]

    if (-not $libs.ContainsKey($sysformsKey)) { $libs[$sysformsKey] = @{} }
    if (-not $libs[$sysformsKey].ContainsKey('runtime')) { $libs[$sysformsKey]['runtime'] = @{} }
    $libs[$sysformsKey]['runtime']['lib/net10.0/System.Windows.Forms.dll'] = @{}

    if ($winintKey) {
        # RAR/GenerateDepsFile conflict resolution can drop this package from deps.json's
        # targets/libraries bookkeeping entirely (even though it resolves fine in
        # project.assets.json and its DLL is copied to the output dir) once another project in
        # the graph pins a package version LibreWinForms.WindowsFormsIntegration transitively
        # depends on (e.g. ProGPU.System.Drawing.Common) - the CoreCLR host then refuses to
        # load the DLL at all ("cannot find the file specified") because deps.json says it
        # isn't allowed to. Ensure the entry unconditionally rather than only patching an
        # existing one.
        if (-not $libs.ContainsKey($winintKey)) { $libs[$winintKey] = @{} }
        if (-not $libs[$winintKey].ContainsKey('runtime')) { $libs[$winintKey]['runtime'] = @{} }
        $libs[$winintKey]['runtime']['lib/net10.0/WindowsFormsIntegration.dll'] = @{}
    }

    if ($progpudrawingKey) {
        # Same conflict-resolution bug, different symptom: ProGPU.System.Drawing.Common's
        # RID-specific target entry survives in deps.json but with only a "dependencies"
        # object and no "runtime" object, so the CoreCLR host won't load its DLL even though
        # the file is physically present and copied to the output dir (WindowsFormsHost's own
        # module initializer needs it at ProGpuWpfSdkPortableBootstrap.Initialize() time).
        if (-not $libs.ContainsKey($progpudrawingKey)) { $libs[$progpudrawingKey] = @{} }
        if (-not $libs[$progpudrawingKey].ContainsKey('runtime')) { $libs[$progpudrawingKey]['runtime'] = @{} }
        $libs[$progpudrawingKey]['runtime']['lib/net10.0/System.Drawing.Common.dll'] = @{}
    }
}

if (-not $deps.ContainsKey('libraries')) { $deps['libraries'] = @{} }
$libraries = $deps['libraries']
if (-not $libraries.ContainsKey($sysformsKey)) {
    $libraries[$sysformsKey] = @{ type = 'package'; serviceable = $true; sha512 = '' }
}
if ($winintKey -and -not $libraries.ContainsKey($winintKey)) {
    $libraries[$winintKey] = @{ type = 'package'; serviceable = $true; sha512 = '' }
}
if ($progpudrawingKey -and -not $libraries.ContainsKey($progpudrawingKey)) {
    $libraries[$progpudrawingKey] = @{ type = 'package'; serviceable = $true; sha512 = '' }
}

($deps | ConvertTo-Json -Depth 100) + "`n" | Set-Content -NoNewline -Encoding utf8 -Path $DepsPath

# Keep the physical deployment beside the dependency manifest in sync with the entries above.
# Framework conflict resolution can remove these package files from both RuntimeCopyLocalItems
# and a RID-less PublishDir.
$outputDir = Split-Path -Parent $DepsPath
$runtimeAssets = @(
    @{ Id = $sysformsPkgId.ToLowerInvariant(); Version = $sysformsVersion; File = 'System.Windows.Forms.dll' },
    @{ Id = $winintPkgId.ToLowerInvariant(); Version = $winintVersion; File = 'WindowsFormsIntegration.dll' },
    @{ Id = $progpudrawingPkgId.ToLowerInvariant(); Version = $progpudrawingVersion; File = 'System.Drawing.Common.dll' }
)
foreach ($asset in $runtimeAssets) {
    if (-not $asset.Version) { continue }
    $source = Join-Path $NugetPackageRoot "$($asset.Id)/$($asset.Version)/lib/net10.0/$($asset.File)"
    if (Test-Path $source) { Copy-Item -Force $source (Join-Path $outputDir $asset.File) }
}

# LibreWPF's reference-pack substitution records these assemblies in deps.json as *.Reference
# libraries, but RID-less publish conflict resolution can leave only their XML documentation in
# PublishDir. They are real runtime dependencies on macOS, so restore the matching package
# implementation beside the app.
$net10Assets = @(
    @{ Id = 'system.configuration.configurationmanager'; File = 'System.Configuration.ConfigurationManager.dll' },
    @{ Id = 'system.formats.nrbf'; File = 'System.Formats.Nrbf.dll' },
    @{ Id = 'system.io.packaging'; File = 'System.IO.Packaging.dll' },
    @{ Id = 'system.security.cryptography.xml'; File = 'System.Security.Cryptography.Xml.dll' },
    @{ Id = 'system.security.permissions'; File = 'System.Security.Permissions.dll' },
    @{ Id = 'system.windows.extensions'; File = 'System.Windows.Extensions.dll' }
)
foreach ($asset in $net10Assets) {
    $found = Find-Net10Asset $NugetPackageRoot $asset.Id $asset.File
    if (-not $found) { continue }
    $source = Join-Path $NugetPackageRoot "$($asset.Id)/$($found.Version)/$($found.RelativeDir)/$($asset.File)"
    if (Test-Path $source) { Copy-Item -Force $source (Join-Path $outputDir $asset.File) }
}

# LibreWPF.Transport has parallel ref/ and lib/ trees with identical file names. A later solution
# build can copy reference assemblies over an already-published host. Restore the complete
# executable transport payload from lib/ after all builds have finished.
if ($transportVersion) {
    $transportRuntime = Join-Path $NugetPackageRoot "$($transportPkgId.ToLowerInvariant())/$transportVersion/lib/net10.0"
    Get-ChildItem -Path (Join-Path $transportRuntime '*.dll') -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item -Force $_.FullName (Join-Path $outputDir $_.Name)
    }
}

$summary = "patch-librewinforms-deps.ps1: patched $DepsPath ($sysformsKey"
if ($winintKey) { $summary += ", $winintKey" }
if ($progpudrawingKey) { $summary += ", $progpudrawingKey" }
$summary += ')'
Write-Host $summary
