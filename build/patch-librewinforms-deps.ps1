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
    [Parameter(Mandatory, Position = 1)][string]$NugetPackageRoot,
    [string]$WindowsX64PackageRoot,
    [string]$WindowsArm64PackageRoot
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

function Find-Net10Asset([string]$nugetPackageRoot, [string]$packageIdLower, [string]$filename, [string]$version = $null) {
    # Returns @{ Version = <version>; RelativeDir = <relative dir under the version folder> } for
    # the best match, or $null. relativeDirCandidates is tried in order per platform.
    $relativeDirCandidates = if ($IsWindows) { @('runtimes/win/lib/net10.0', 'lib/net10.0') } else { @('lib/net10.0') }
    foreach ($relativeDir in $relativeDirCandidates) {
        if ($version) {
            $path = Join-Path $nugetPackageRoot "$packageIdLower/$version/$relativeDir/$filename"
            if (Test-Path $path) { return @{ Version = $version; RelativeDir = $relativeDir } }
            continue
        }

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

function Find-DependencyVersion([hashtable]$deps, [string]$packageId) {
    $prefix = "$packageId/"
    foreach ($key in $deps['libraries'].Keys) {
        if ($key.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            return $key.Substring($prefix.Length)
        }
    }
    return $null
}

$sysformsPkgId = 'LibreWinForms.System.Windows.Forms'
$winintPkgId = 'LibreWinForms.WindowsFormsIntegration'
$progpudrawingPkgId = 'ProGPU.System.Drawing.Common'
$transportPkgId = 'LibreWPF.Transport'
$interopPkgId = 'LibreWPF.Interop'

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
$interopVersion = Find-PackageVersion $NugetPackageRoot $interopPkgId.ToLowerInvariant()

$deps = Get-Content -Raw -Path $DepsPath | ConvertFrom-Json -AsHashtable

$sysformsKey = "$sysformsPkgId/$sysformsVersion"
$winintKey = if ($winintVersion) { "$winintPkgId/$winintVersion" } else { $null }
$progpudrawingKey = if ($progpudrawingVersion) { "$progpudrawingPkgId/$progpudrawingVersion" } else { $null }
$transportKey = if ($transportVersion) { "$transportPkgId/$transportVersion" } else { $null }

# These assemblies are copied beside portable apps below. They must also appear in
# deps.json: the CoreCLR host does not probe an otherwise-present DLL which has no
# runtime asset declaration.
$net10Assets = @(
    @{ Id = 'system.configuration.configurationmanager'; PackageId = 'System.Configuration.ConfigurationManager'; File = 'System.Configuration.ConfigurationManager.dll' },
    @{ Id = 'system.formats.nrbf'; PackageId = 'System.Formats.Nrbf'; File = 'System.Formats.Nrbf.dll' },
    @{ Id = 'system.io.packaging'; PackageId = 'System.IO.Packaging'; File = 'System.IO.Packaging.dll' },
    @{ Id = 'system.security.cryptography.xml'; PackageId = 'System.Security.Cryptography.Xml'; File = 'System.Security.Cryptography.Xml.dll' },
    @{ Id = 'system.security.permissions'; PackageId = 'System.Security.Permissions'; File = 'System.Security.Permissions.dll' },
    @{ Id = 'system.windows.extensions'; PackageId = 'System.Windows.Extensions'; File = 'System.Windows.Extensions.dll' }
)
$net10RuntimeAssets = @(
    foreach ($asset in $net10Assets) {
        $version = Find-DependencyVersion $deps $asset.PackageId
        $found = Find-Net10Asset $NugetPackageRoot $asset.Id $asset.File $version
        if ($found) {
            @{ Id = $asset.Id; PackageId = $asset.PackageId; Version = $found.Version; RelativeDir = $found.RelativeDir; File = $asset.File }
        }
    }
)

if (-not $deps.ContainsKey('targets')) { $deps['targets'] = @{} }
foreach ($tfm in @($deps['targets'].Keys)) {
    $libs = $deps['targets'][$tfm]

    foreach ($asset in $net10RuntimeAssets) {
        $legacyPrefix = "$($asset.Id)/"
        foreach ($key in @($libs.Keys)) {
            if ($key.StartsWith($legacyPrefix, [StringComparison]::Ordinal)) { $libs.Remove($key) }
        }
    }

    if (-not $libs.ContainsKey($sysformsKey)) { $libs[$sysformsKey] = @{} }
    if (-not $libs[$sysformsKey].ContainsKey('runtime')) { $libs[$sysformsKey]['runtime'] = @{} }
    $libs[$sysformsKey]['runtime']['lib/net10.0/System.Windows.Forms.dll'] = @{}
    # The current LibreWinForms payload carries the v11 portable WPF core.  Transport's
    # older convenience copy is v10, and restoring it after build makes the host reject
    # assemblies compiled against the v11 contract at startup.
    $libs[$sysformsKey]['runtime']['lib/net10.0/System.Private.Windows.Core.dll'] = @{
        assemblyVersion = '11.0.0.0'
        fileVersion = '42.42.42.42424'
    }

    if ($transportKey -and $libs.ContainsKey($transportKey)) {
        $transportEntry = $libs[$transportKey]
        if ($transportEntry.ContainsKey('runtime') -and $transportEntry['runtime'].ContainsKey('lib/net10.0/System.Private.Windows.Core.dll')) {
            $transportEntry['runtime']['lib/net10.0/System.Private.Windows.Core.dll'] = @{
                assemblyVersion = '11.0.0.0'
                fileVersion = '42.42.42.42424'
            }
        }
        if ($transportEntry.ContainsKey('runtimeTargets')) {
            foreach ($assetPath in @($transportEntry['runtimeTargets'].Keys)) {
                if ($assetPath.EndsWith('/System.Private.Windows.Core.dll', [StringComparison]::OrdinalIgnoreCase)) {
                    $transportEntry['runtimeTargets'][$assetPath]['assemblyVersion'] = '11.0.0.0'
                    $transportEntry['runtimeTargets'][$assetPath]['fileVersion'] = '42.42.42.42424'
                }
            }
        }
    }

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

    foreach ($asset in $net10RuntimeAssets) {
        $key = "$($asset.PackageId)/$($asset.Version)"
        if (-not $libs.ContainsKey($key)) { $libs[$key] = @{} }
        if (-not $libs[$key].ContainsKey('runtime')) { $libs[$key]['runtime'] = @{} }
        $libs[$key]['runtime']["$($asset.RelativeDir)/$($asset.File)"] = @{}
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
foreach ($asset in $net10RuntimeAssets) {
    $key = "$($asset.PackageId)/$($asset.Version)"
    if (-not $libraries.ContainsKey($key)) {
        $libraries[$key] = @{ type = 'package'; serviceable = $true; sha512 = '' }
    }
    $legacyPrefix = "$($asset.Id)/"
    foreach ($legacyKey in @($libraries.Keys)) {
        if ($legacyKey.StartsWith($legacyPrefix, [StringComparison]::Ordinal)) { $libraries.Remove($legacyKey) }
    }
}

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

# LibreWPF's reference-pack substitution records these assemblies as *.Reference libraries,
# but RID-less publish conflict resolution can leave only their XML documentation in PublishDir.
# Keep the physical output in sync with the runtime entries written above.
foreach ($asset in $net10RuntimeAssets) {
    $source = Join-Path $NugetPackageRoot "$($asset.Id)/$($asset.Version)/$($asset.RelativeDir)/$($asset.File)"
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

# Transport also carries an interop copy for convenience, but it can be older than the explicit
# LibreWPF.Interop package selected by the local feed.  The portable WPF implementation calls
# into that explicit surface during module initialization, so restore it last rather than letting
# Transport silently overwrite it.
if ($interopVersion) {
    $interopDll = Join-Path $NugetPackageRoot "$($interopPkgId.ToLowerInvariant())/$interopVersion/lib/net10.0/ProGPU.Wpf.Interop.dll"
    if (Test-Path $interopDll) {
        Copy-Item -Force $interopDll (Join-Path $outputDir 'ProGPU.Wpf.Interop.dll')
        # LibreWPF.Transport also carries architecture-specific overlay directories. The host
        # selects one of those ahead of the root asset, so repair every Windows overlay too.
        Get-ChildItem -Path (Join-Path $outputDir 'runtimes/win-*/lib/net10.0') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item -Force $interopDll (Join-Path $_.FullName 'ProGPU.Wpf.Interop.dll')
        }
    }
}

# LibreWPF.Transport currently includes a v10 copy of System.Private.Windows.Core.  The
# LibreWinForms package selected by this feed supplies the matching v11 implementation;
# overwrite both the root and RID overlays after restoring Transport.
$portableCore = Join-Path $NugetPackageRoot "$($sysformsPkgId.ToLowerInvariant())/$sysformsVersion/lib/net10.0/System.Private.Windows.Core.dll"
if (Test-Path $portableCore) {
    Copy-Item -Force $portableCore (Join-Path $outputDir 'System.Private.Windows.Core.dll')
    Get-ChildItem -Path (Join-Path $outputDir 'runtimes/win-*/lib/net10.0') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item -Force $portableCore (Join-Path $_.FullName 'System.Private.Windows.Core.dll')
    }
}

# The ProGPU packages are built for the active native architecture.  Publish can retain an older
# package's root asset (or the transport-provided overlay) even after restore selects the local
# feed, which makes an ARM64 host attempt to load an x64 managed/native bridge.  Restore the
# selected portable runtime payload after all other copy-local processing, both at the root and
# in every Windows RID overlay.
$portableProGpuPackageIds = @(
    'ProGPU.Backend',
    'ProGPU.Compute',
    'ProGPU.DirectX',
    'ProGPU.Scene',
    'ProGPU.SkiaSharp',
    'ProGPU.System.Drawing.Common',
    'ProGPU.Text',
    'ProGPU.Text.Shaping',
    'ProGPU.Transpiler',
    'ProGPU.Vector',
    'ProGPU.WinRT'
)
foreach ($packageId in $portableProGpuPackageIds) {
    $packageIdLower = $packageId.ToLowerInvariant()
    $packageVersion = Find-PackageVersion $NugetPackageRoot $packageIdLower
    if (-not $packageVersion) { continue }

    $runtimeDir = Join-Path $NugetPackageRoot "$packageIdLower/$packageVersion/lib/net10.0"
    Get-ChildItem -Path (Join-Path $runtimeDir '*.dll') -ErrorAction SilentlyContinue | ForEach-Object {
        $sourceDll = $_
        Copy-Item -Force $sourceDll.FullName (Join-Path $outputDir $sourceDll.Name)
        Get-ChildItem -Path (Join-Path $outputDir 'runtimes/win-*/lib/net10.0') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item -Force $sourceDll.FullName (Join-Path $_.FullName $sourceDll.Name)
        }
    }
}

function Copy-PackageLibAsset([string]$packageRoot, [string]$packageId, [string]$version, [string]$fileName, [string]$destination) {
    if (-not $packageRoot) { return $false }
    $packagePath = Join-Path $packageRoot "$packageId.$version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath)) { return $false }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entry = $archive.GetEntry("lib/net10.0/$fileName")
        if (-not $entry) { return $false }
        $destinationDir = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        $input = $entry.Open()
        try {
            $output = [System.IO.File]::Open($destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
        return $true
    }
    finally { $archive.Dispose() }
}

function Add-RidRuntimeTargets([hashtable]$deps, [string]$packageId, [string]$version, [string]$fileName) {
    $libraryKey = "$packageId/$version"
    foreach ($targetName in $deps.targets.Keys) {
        $target = $deps.targets[$targetName]
        if (-not $target.ContainsKey($libraryKey)) { continue }
        $entry = $target[$libraryKey]
        if (-not $entry.ContainsKey('runtimeTargets')) { $entry['runtimeTargets'] = @{} }
        foreach ($rid in 'win-x64', 'win-arm64') {
            $entry['runtimeTargets']["runtimes/$rid/lib/net10.0/$fileName"] = @{ rid = $rid; assetType = 'runtime' }
        }
    }
}

# A single Windows ZIP serves x64 and ARM64. These mixed-mode ProGPU/WinForms assemblies cannot
# sit at one RID-neutral lib/ path: CoreCLR must select a matching runtimeTarget.  The two
# canonical feeds are intentionally kept separate while packing; stage their matching assets here
# and declare both selections in deps.json.
if ($WindowsX64PackageRoot -and $WindowsArm64PackageRoot) {
    $ridPackageIds = @($portableProGpuPackageIds + $interopPkgId + $winintPkgId)
    foreach ($packageId in $ridPackageIds) {
        $version = Find-DependencyVersion $deps $packageId
        if (-not $version) { continue }
        $packageFileName = "$packageId.$version.nupkg"
        $x64Package = Join-Path $WindowsX64PackageRoot $packageFileName
        $armPackage = Join-Path $WindowsArm64PackageRoot $packageFileName
        if (-not (Test-Path -LiteralPath $x64Package) -or -not (Test-Path -LiteralPath $armPackage)) { continue }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $x64Archive = [System.IO.Compression.ZipFile]::OpenRead($x64Package)
        try {
            $dllNames = @($x64Archive.Entries | Where-Object { $_.FullName -match '^lib/net10\.0/[^/]+\.dll$' } | ForEach-Object Name)
        }
        finally { $x64Archive.Dispose() }
        foreach ($dllName in $dllNames) {
            $x64Destination = Join-Path $outputDir "runtimes/win-x64/lib/net10.0/$dllName"
            $armDestination = Join-Path $outputDir "runtimes/win-arm64/lib/net10.0/$dllName"
            if ((Copy-PackageLibAsset $WindowsX64PackageRoot $packageId $version $dllName $x64Destination) -and
                (Copy-PackageLibAsset $WindowsArm64PackageRoot $packageId $version $dllName $armDestination)) {
                Add-RidRuntimeTargets $deps $packageId $version $dllName
            }
        }
    }
}

# Write through a PROCESS-UNIQUE temp file and move it into place, rather than writing $DepsPath
# directly. A rename is atomic, so concurrent WPF temporary-project invocations cannot leave a
# truncated dependency manifest behind.
$depsTempPath = "$DepsPath.$PID.tmp"
($deps | ConvertTo-Json -Depth 100) + "`n" | Set-Content -NoNewline -Encoding utf8 -Path $depsTempPath
Move-Item -Force -Path $depsTempPath -Destination $DepsPath

$summary = "patch-librewinforms-deps.ps1: patched $DepsPath ($sysformsKey"
if ($winintKey) { $summary += ", $winintKey" }
if ($progpudrawingKey) { $summary += ", $progpudrawingKey" }
$summary += ')'
Write-Host $summary
