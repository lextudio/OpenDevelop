#!/usr/bin/env pwsh
#
# build.ps1 — build ONE part of OpenDevelop (the shell, a shared assembly, or a single AddIn)
# instead of the whole solution. This is the inner-loop tool; dist.ps1 remains the only way to
# produce a real distribution, and launch.ps1 the way to build-and-run everything.
#
# Usage:
#   ./build.ps1 shell                    # the IDE host (src/Main/SharpDevelop)
#   ./build.ps1 base                     # ICSharpCode.SharpDevelop (Main/Base)
#   ./build.ps1 core                     # ICSharpCode.Core
#   ./build.ps1 sln                      # the whole OpenDevelop.Mvp.slnx
#   ./build.ps1 unodesignhost            # fuzzy-match one AddIn project under src/AddIns
#   ./build.ps1 winui -List              # show what a fuzzy name matches, build nothing
#   ./build.ps1 unodesignhost -Kill      # kill a running OpenDevelop first (file locks)
#   ./build.ps1 shell -Configuration Release
#   ./build.ps1 base -Then shell         # build Base, then the shell, in one pinned pass
#
# WHY NOT JUST `dotnet build <csproj>` - three traps, each of which has cost a debugging session:
#
#  1. Assembly VERSION drift. Every project links src/Main/GlobalAssemblyInfo.cs, which the
#     GitVersion MSBuild task regenerates on each invocation - and it can compute a DIFFERENT
#     revision between two `dotnet` runs minutes apart. The rebuilt assembly then references a
#     host version that no longer matches what is on disk; the AddIn silently fails to load and
#     every one of its classes is reported as "Cannot find class ..." (see CLAUDE.md). This
#     script pins -p:GitVersion_* to the values ALREADY baked into GlobalAssemblyInfo.cs, so a
#     targeted rebuild stays binary-compatible with everything else already built. -NoPinVersion
#     opts out.
#
#  2. Architecture stamping. Without -p:ProGpuWpfUseCurrentRuntimeIdentifier=false, LibreWPF.Sdk
#     falls back to the build machine's own RID and emits an architecture-stamped assembly. It
#     loads fine on the machine that built it, then throws "The assembly architecture is not
#     compatible with the current process architecture" anywhere else - including in the x64
#     OpenDevelop process on an ARM64 machine, where it takes the whole app down at startup.
#     Passed by default here; -NativeRid opts out.
#
#  3. Projects `dotnet build` CANNOT build at all. WinUI 3's UseWinUI pulls in
#     MrtCore.PriGen.targets, whose tasks ship only with Visual Studio (MSB4062 otherwise), and
#     an unpackaged WinUI 3 app must be built RID-specific. Those projects are detected here and
#     routed to Visual Studio's MSBuild automatically, once per architecture.
#
# NOTE ON SHARED ASSEMBLIES: Base/Core and the Designer.* libraries are copied into every AddIn
# folder that references them. Rebuilding one of those refreshes the shared copy but NOT the
# per-AddIn copies, so the app can still load a stale one. This script warns when that applies.
#

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Additional targets to build after the first, in the same pinned/property context.
    [string[]]$Then = @(),

    # Print what $Target resolves to and exit without building.
    [switch]$List,

    # Kill OpenDevelop and any lingering dotnet/MSBuild build servers first. Those hold locks on
    # deployed AddIn DLLs (MSB3021/MSB3027), and the out-of-process designer hosts outlive the IDE.
    [switch]$Kill,

    # Let LibreWPF.Sdk stamp the build machine's own RID (see trap 2 above). Only for a build you
    # will run exclusively on this machine, in a process of this machine's architecture.
    [switch]$NativeRid,

    # Skip GitVersion pinning (see trap 1 above).
    [switch]$NoPinVersion,

    # Build with the SAME semantics dist.ps1's "addins" phase uses, so the output can be dropped
    # into a distribution payload via "./dist.ps1 -Phase payload".
    #
    # REQUIRED for that workflow: without OpenDevelopDistributionRidFamily the AddIn SDK's
    # OpenDevelopPruneAddinDeploymentAssets target does not strip other platforms' native assets,
    # so the build leaves runtimes/linux-*/native/libglfw.so.3 (and friends) in AddIns/ and the
    # payload phase then fails validation with "Distribution payload contains build-only or
    # foreign assets". Learned by doing exactly that.
    [switch]$ForDistribution,

    # Architectures to build VS-MSBuild/WinUI projects for. Defaults to both, matching dist.ps1.
    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64'),

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraProperties = @()
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force

# Named shortcuts for the projects that get rebuilt most often.
$shortcuts = @{
    'shell'  = 'src/Main/SharpDevelop/SharpDevelop.csproj'
    'host'   = 'src/Main/SharpDevelop/SharpDevelop.csproj'
    'base'   = 'src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj'
    'core'   = 'src/Main/Core/Project/ICSharpCode.Core.csproj'
    'sln'    = 'OpenDevelop.Mvp.slnx'
    'all'    = 'OpenDevelop.Mvp.slnx'
}

# Rebuilding one of these refreshes the shared copy but not the per-AddIn copies beside it.
$sharedAssemblyHints = @('Main/Base', 'Main/Core', 'Main/Designer', 'ICSharpCode.SharpDevelop.Widgets')

function Resolve-BuildTarget {
    param([Parameter(Mandatory)][string]$Name)

    if ($shortcuts.ContainsKey($Name.ToLowerInvariant())) {
        return @(Join-Path $repoRoot $shortcuts[$Name.ToLowerInvariant()])
    }

    # An explicit path (absolute, or relative to the repo root or the caller's location).
    foreach ($candidate in @($Name, (Join-Path $repoRoot $Name), (Join-Path (Get-Location).Path $Name))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return @((Resolve-Path $candidate).Path) }
    }

    # Fuzzy: match the project file name or its containing directory anywhere under src/.
    $found = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Where-Object {
            $_.BaseName -like "*$Name*" -or (Split-Path -Leaf $_.DirectoryName) -like "*$Name*"
        }

    # Tie-break: prefer a project whose name ENDS with what was typed, so "unodesignhost" picks
    # WinUIXamlDesigner.UnoDesignHost rather than also matching its .Remote/.Tests siblings.
    # Typing the fuller "unodesignhost.remote" still selects those.
    if (@($found).Count -gt 1) {
        $exact = @($found | Where-Object {
            $_.BaseName -like "*$Name" -or (Split-Path -Leaf $_.DirectoryName) -like "*$Name"
        })
        if ($exact.Count -ge 1) { $found = $exact }
    }

    return @($found | ForEach-Object { $_.FullName })
}

function Test-NeedsVsMsBuild {
    param([Parameter(Mandatory)][string]$ProjectPath)
    # WinUI 3 (UseWinUI -> MrtCore.PriGen.targets) and the Microsoft WinForms design host are the
    # projects dotnet build cannot produce correctly; see Build-MicrosoftDesignerHosts in dist.ps1.
    $normalized = $ProjectPath.Replace('\', '/')
    return $normalized -match 'MicrosoftHost'
}

function Test-IsRidSpecific {
    param([Parameter(Mandatory)][string]$ProjectPath)
    # The WinUI child is a real unpackaged WinUI 3 app: RID-specific, one build per architecture.
    # The Microsoft WinForms host is an ordinary managed child launched via "dotnet exec": RID-less.
    return $ProjectPath.Replace('\', '/') -match 'WinUIXamlDesigner.MicrosoftHost'
}

if (-not $Target) {
    Write-Host 'build.ps1 - build one part of OpenDevelop.' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Shortcuts:' -ForegroundColor Cyan
    $shortcuts.GetEnumerator() | Sort-Object Name | ForEach-Object { '  {0,-8} {1}' -f $_.Name, $_.Value }
    Write-Host ''
    Write-Host 'Or pass an AddIn name (fuzzy) or a .csproj path. Examples:' -ForegroundColor Cyan
    Write-Host '  ./build.ps1 shell'
    Write-Host '  ./build.ps1 unodesignhost -Kill'
    Write-Host '  ./build.ps1 winui -List'
    Write-Host '  ./build.ps1 base -Then shell -Configuration Release'
    exit 0
}

# @(...) at the CALL site, not just inside the function: PowerShell unrolls a single-element
# array as it leaves a function, so a one-match result would arrive here as a bare string and
# $resolved[0] would index its first CHARACTER.
$resolved = @(Resolve-BuildTarget -Name $Target)

if ($resolved.Count -eq 0) {
    Write-Host "build.ps1: nothing matched '$Target'." -ForegroundColor Red
    Write-Host "Try './build.ps1 <partial-name> -List', or pass a .csproj path." -ForegroundColor Yellow
    exit 1
}

if ($List -or $resolved.Count -gt 1) {
    if ($resolved.Count -gt 1 -and -not $List) {
        Write-Host "build.ps1: '$Target' is ambiguous - $($resolved.Count) projects match:" -ForegroundColor Yellow
    } else {
        Write-Host "'$Target' matches $($resolved.Count) project(s):" -ForegroundColor Cyan
    }
    $resolved | ForEach-Object { '  ' + $_.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/') }
    if ($List) { exit 0 }
    Write-Host 'Narrow the name, or pass the full path.' -ForegroundColor Yellow
    exit 1
}

$targets = @($resolved[0])
foreach ($extra in $Then) {
    $extraResolved = @(Resolve-BuildTarget -Name $extra)
    if ($extraResolved.Count -ne 1) {
        Write-Host "build.ps1: -Then '$extra' resolved to $($extraResolved.Count) projects; be more specific." -ForegroundColor Red
        exit 1
    }
    $targets += $extraResolved[0]
}

if ($Kill) {
    Write-Host '==> Stopping OpenDevelop and lingering build servers/design hosts...'
    # The out-of-process designer hosts are plain "dotnet" processes kept alive by
    # SharedDesignerHostPool across IDE restarts, so killing OpenDevelop alone is not enough.
    Get-Process OpenDevelop, OpenDevelopARM64, dotnet, MSBuild -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$dotnet = Find-DotNetHost

$commonProperties = @()
if (-not $NativeRid) { $commonProperties += '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false' }
if ($ForDistribution) {
    $ridFamily = if ($IsWindows) { 'win' } else { 'osx' }
    $commonProperties += @(
        '-p:OpenDevelopDistributionBuild=true',
        "-p:OpenDevelopDistributionRidFamily=$ridFamily",
        '-p:ProGpuWpfCopyPackageRuntimeAssets=false'
    )
}
if (-not $NoPinVersion) {
    try {
        $commonProperties += Get-PinnedGitVersionProperties -GlobalAssemblyInfoPath (Join-Path $repoRoot 'src/Main/GlobalAssemblyInfo.cs')
    } catch {
        Write-Host "build.ps1: not pinning GitVersion ($($_.Exception.Message))." -ForegroundColor Yellow
        Write-Host '           The first build of a fresh clone generates it; re-run to get pinning.' -ForegroundColor Yellow
    }
}
$commonProperties += $ExtraProperties

foreach ($project in $targets) {
    $relative = $project.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/')

    if ($project -like '*.slnx') {
        Write-Host "==> Building solution $relative ($Configuration)..."
        Restore-Solution -DotNet $dotnet -Solution $project -ExtraProperties @('-p:ProGpuWpfUseCurrentRuntimeIdentifier=false')
        Build-Solution -DotNet $dotnet -Solution $project -Configuration $Configuration -ExtraProperties $commonProperties
        continue
    }

    if (Test-NeedsVsMsBuild -ProjectPath $project) {
        $msbuild = Find-VsMsBuild
        Write-Host "==> $relative cannot be built by 'dotnet build' (WinUI/PRI tasks ship with Visual Studio)."
        Write-Host "==> Using Visual Studio MSBuild: $msbuild"
        # DisableGitVersionTask avoids MSB4216: GitVersion wants an x86 .NET task host that is
        # not present. The pinned -p:GitVersion_* values supply the version instead.
        $vsCommon = @('-restore', "-p:Configuration=$Configuration", '-p:DisableGitVersionTask=true', '-v:m') + $commonProperties
        if (Test-IsRidSpecific -ProjectPath $project) {
            foreach ($rid in $RuntimeIdentifiers) {
                Write-Host "==> Building $relative (RuntimeIdentifier=$rid)..."
                Invoke-Native $msbuild $project @vsCommon "-p:RuntimeIdentifier=$rid"
            }
        } else {
            Write-Host "==> Building $relative (RID-less)..."
            Invoke-Native $msbuild $project @vsCommon
        }
        continue
    }

    Write-Host "==> Building $relative ($Configuration)..."
    Invoke-Native $dotnet build $project -c $Configuration '-v' 'minimal' @commonProperties

    if ($sharedAssemblyHints | Where-Object { $relative -like "*$_*" }) {
        Write-Host ''
        Write-Host "Note: $relative is a SHARED assembly - it is copied into every AddIn folder that" -ForegroundColor Yellow
        Write-Host '      references it. This build refreshed the shared copy only, so the app can still' -ForegroundColor Yellow
        Write-Host '      load a stale per-AddIn copy. Rebuild the AddIns that matter, or use:' -ForegroundColor Yellow
        Write-Host "        ./build.ps1 sln -Configuration $Configuration" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Build finished.' -ForegroundColor Green
