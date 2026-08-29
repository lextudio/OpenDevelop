#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Windows port of repack-librewpf.sh: build LibreWPF/ProGPU from the local openwpf checkout,
  pack it into the "local-librewpf" feed, and re-restore OpenDevelop against it.

.DESCRIPTION
  Unlike the macOS script, packages are packed with the SAME version numbers the published ones
  use (LibreWPF.ProGPU/.Transport at the LibreWPF.Sdk version pinned in global.json,
  LibreWPF.Interop/ProGPU.* at the SDK's ProGpuPackageVersion default), so nothing in the build
  needs a version override. NuGet.config's packageSourceMapping is what routes those exact ids to
  the local feed instead of nuget.org; because the ids AND versions collide, ~/.nuget/packages
  must be cleared for each repacked id or restore just reuses the already-extracted published
  copy. That cache clearing is done below.

  openwpf/global.json pins an 11.x preview SDK, so - exactly like the macOS script - every dotnet
  command runs with OpenDevelop's repo root as the working directory and an absolute project
  path, letting OpenDevelop's global.json pin SDK resolution to 10.x.

.PARAMETER Fast
  Rebuild only ProGPU.Wpf/Interop and the subsidiary ProGPU.* packages, skipping the full
  Microsoft.DotNet.Wpf source tree and the Transport package. See the staleness warning below:
  if anything under src/Microsoft.DotNet.Wpf/src changed, do NOT use this.

.PARAMETER LibreWpfRoot
  The openwpf checkout. Defaults to a sibling wpf-tools/openwpf next to the uno-tools workspace,
  matching the relative "local-librewpf" feed path in NuGet.config.

.EXAMPLE
  ./repack-librewpf.ps1
  ./repack-librewpf.ps1 -Fast
#>
[CmdletBinding()]
param(
    [switch]$Fast,
    [string]$LibreWpfRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot

function Fail([string]$message) {
    Write-Error "repack-librewpf.ps1: $message"
    exit 1
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) { Fail 'cannot find dotnet on PATH' }

if (-not $LibreWpfRoot) {
    $LibreWpfRoot = Join-Path $repoRoot '..\..\..\..\wpf-tools\openwpf'
}
if (-not (Test-Path -LiteralPath $LibreWpfRoot)) {
    Fail "openwpf checkout not found: $LibreWpfRoot (pass -LibreWpfRoot to override)"
}
$LibreWpfRoot = (Resolve-Path -LiteralPath $LibreWpfRoot).Path

# Versions come from the LibreWPF.Sdk pinned in OpenDevelop's global.json - see the .DESCRIPTION
# note on why these deliberately match the published ones instead of using a -dev suffix.
$globalJson = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'global.json') | ConvertFrom-Json
$sdkVersion = $globalJson.'msbuild-sdks'.'LibreWPF.Sdk'
if (-not $sdkVersion) { Fail 'global.json does not pin an msbuild-sdks LibreWPF.Sdk version' }

$sdkTargets = Join-Path $HOME ".nuget\packages\librewpf.sdk\$sdkVersion\targets\ProGPU.Wpf.Sdk.targets"
if (-not (Test-Path -LiteralPath $sdkTargets)) {
    Fail "LibreWPF.Sdk $sdkVersion is not in the NuGet cache - run a restore first ($sdkTargets)"
}
$progpuVersion = (Select-String -LiteralPath $sdkTargets -Pattern "<ProGpuPackageVersion Condition=[^>]*>([^<]+)<" |
    Select-Object -First 1).Matches[0].Groups[1].Value
if (-not $progpuVersion) { Fail "cannot read the ProGpuPackageVersion default out of $sdkTargets" }

# LibreWPF.ProGPU / LibreWPF.Transport ship at the SDK's own version; LibreWPF.Interop and the
# subsidiary ProGPU.* packages ship at ProGpuPackageVersion. They are NOT the same number.
$wpfVersion = $sdkVersion
Write-Host "==> LibreWPF.ProGPU/.Transport = $wpfVersion, LibreWPF.Interop/ProGPU.* = $progpuVersion"

$packageOutput = Join-Path $LibreWpfRoot 'artifacts\packages\Release\NonShipping'
New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null

$wpfSrc = Join-Path $LibreWpfRoot 'src\Microsoft.DotNet.Wpf\src'
$interopProject = Join-Path $LibreWpfRoot 'external\ProGPU\src\ProGPU.Wpf.Interop\ProGPU.Wpf.Interop.csproj'
$progpuProject = Join-Path $LibreWpfRoot 'src\ProGPU.Wpf\ProGPU.Wpf.csproj'
$progpuExtSrc = Join-Path $LibreWpfRoot 'external\ProGPU\src'
$transportProject = Join-Path $LibreWpfRoot 'packaging\Microsoft.DotNet.Wpf.GitHub\Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj'

# OpenDevelop's NuGet.config pulls these in as their OWN packages (see ProGPU.Wpf.Sdk.targets'
# "Package" reference-mode PackageReference list), NOT bundled inside LibreWPF.ProGPU.nupkg -
# ProGPU.Wpf.csproj's ProjectReferences build the assemblies as a side effect of Build-Clean
# below, but they still need their own pack + cache clear or OpenDevelop keeps resolving whatever
# stale copy a previous restore extracted.
$subsidiary = @(
    @{ Project = "$progpuExtSrc\ProGPU.Backend\ProGPU.Backend.csproj";       PackageId = 'ProGPU.Backend' }
    @{ Project = "$progpuExtSrc\ProGPU.DirectX\ProGPU.DirectX.csproj";       PackageId = 'ProGPU.DirectX' }
    @{ Project = "$progpuExtSrc\ProGPU.Scene\ProGPU.Scene.csproj";           PackageId = 'ProGPU.Scene' }
    @{ Project = "$progpuExtSrc\ProGPU.Vector\ProGPU.Vector.csproj";         PackageId = 'ProGPU.Vector' }
    @{ Project = "$progpuExtSrc\ProGPU.Text\ProGPU.Text.csproj";             PackageId = 'ProGPU.Text' }
    @{ Project = "$progpuExtSrc\ProGPU.Compute\ProGPU.Compute.csproj";       PackageId = 'ProGPU.Compute' }
    @{ Project = "$progpuExtSrc\ProGPU.Transpiler\ProGPU.Transpiler.csproj"; PackageId = 'ProGPU.Transpiler' }
)

# eng\WpfArcadeSdk\tools\ApiCompat.targets hard-disables RunNetFrameworkApiCompat when
# '$(OS)' != 'Windows_NT', so the macOS repack never runs it. On Windows it switches on and wants
# .tools\native\bin\net-framework-48-ref-assemblies\...\*.dll, which only Arcade's native-tools
# bootstrap installs. That check validates the WPF assemblies against the .NET Framework 4.8
# contract - irrelevant to packing these net10.0 packages - so turn it off and build the same
# configuration macOS already ships from.
$commonProps = @('-p:RunNetFrameworkApiCompat=false')

# WindowsBase's GenerateSources target and GenerateAvTraceMessages.targets run Perl scripts that
# generate real source files (PackageXmlStringTable.cs, AvTraceMessages.cs). eng\WpfArcadeSdk\
# Sdk\Sdk.props points PerlCommand at /usr/bin/perl off Windows but at Arcade's native-tools
# strawberry-perl on Windows, which only the native-tools bootstrap installs. Git for Windows
# ships a perl that runs these scripts fine, so point PerlCommand at whatever perl is on PATH
# rather than requiring the bootstrap.
# Git's perl lives under Git\usr\bin, which is on the Git Bash PATH but not the PowerShell one,
# so probe the known install locations before falling back to PATH.
$perl = @(
    'C:\Program Files\Git\usr\bin\perl.exe'
    'C:\Program Files (x86)\Git\usr\bin\perl.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $perl) { $perl = (Get-Command perl -ErrorAction SilentlyContinue)?.Source }
if (-not $perl) {
    Fail 'cannot find perl (needed by WindowsBase/PresentationCore source generation); install Git for Windows or Strawberry Perl'
}
# PerlCommand is pasted unquoted into an Exec command line, so a path with spaces (Git installs
# under "Program Files") makes cmd try to run "C:\Program" and fail with exit code 9009. Quoting
# the property value does not survive the trip through MSBuild's command line either, so hand it
# the 8.3 short path instead - no spaces, nothing left to quote.
$fso = New-Object -ComObject Scripting.FileSystemObject
$commonProps += "-p:PerlCommand=$($fso.GetFile($perl).ShortPath)"

function Get-VersionProps([string]$version) {
    $commonProps + @(
        "-p:VersionPrefix=$($version.Split('-')[0])"
        "-p:Version=$version"
        "-p:PackageVersion=$version"
    )
}

function Remove-DirIfExists([string]$path) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -Confirm:$false }
}

function Build-Clean([string]$project, [string]$version) {
    $dir = Split-Path -Parent $project
    Remove-DirIfExists (Join-Path $dir 'bin\Release')
    Remove-DirIfExists (Join-Path $dir 'obj\Release')
    & $dotnet build $project -c Release -v:minimal @(Get-VersionProps $version)
    if ($LASTEXITCODE -ne 0) { Fail "build failed: $project" }
}

function Invoke-Pack([string]$project, [string]$version) {
    # --no-build/--no-restore: every project below was just built by Build-Clean, so packing
    # without these flags would silently re-run Build (and an implicit restore) a second time per
    # project - that duplicate work, not the per-project build loop itself, is the slow part.
    & $dotnet pack $project -c Release -o $packageOutput -v:minimal --no-build --no-restore @(Get-VersionProps $version)
    if ($LASTEXITCODE -ne 0) { Fail "pack failed: $project" }
}

function Invoke-PackFull([string]$project, [string]$version) {
    # The Transport ArchNeutral packaging project needs its Restore and Build to run in the SAME
    # `dotnet pack` invocation: its target reads $(PkgMicrosoft_Private_Winforms), a property
    # NuGet's GeneratePathProperty only populates into props evaluated fresh after restore.
    # Passing --no-build skips that re-evaluation and the property comes back empty. This project
    # has no real source to compile, so skipping --no-build costs nothing.
    & $dotnet pack $project -c Release -o $packageOutput -v:minimal @(Get-VersionProps $version)
    if ($LASTEXITCODE -ne 0) { Fail "pack failed: $project" }
}

function Clear-PackageCache([string]$packageId) {
    Remove-DirIfExists (Join-Path $HOME ".nuget\packages\$($packageId.ToLowerInvariant())")
}

function Test-TransportStaleness {
    $transportNupkg = Join-Path $packageOutput "LibreWPF.Transport.$wpfVersion.nupkg"
    if (-not (Test-Path -LiteralPath $transportNupkg)) {
        Write-Warning "LibreWPF.Transport.$wpfVersion.nupkg has never been packed - ProGPU.Wpf compiles against the interfaces/types in $wpfSrc (PresentationCore etc.), but those live in Transport, which -Fast never builds. Run without -Fast first."
        return
    }
    # -Fast only rebuilds ProGPU.Wpf/Interop (+ subsidiary ProGPU.* packages) against whatever
    # Transport happens to already be packed. If the real WPF source tree changed more recently
    # than that pack, ProGPU.Wpf can compile fine against the NEW interface shape while the cached
    # Transport package still ships the OLD PresentationCore.dll - a MissingMethodException at
    # startup that looks nothing like a build failure. Warn instead of leaving it a surprise.
    $packTime = (Get-Item -LiteralPath $transportNupkg).LastWriteTimeUtc
    $newer = Get-ChildItem -LiteralPath $wpfSrc -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -gt $packTime } | Select-Object -First 1
    if ($newer) {
        Write-Warning "$($newer.FullName) (and possibly other files under $wpfSrc) changed after Transport was last packed. -Fast will NOT rebuild Transport, so ProGPU.Wpf may compile against interfaces that Transport's cached PresentationCore.dll doesn't implement yet - a System.MissingMethodException at app startup, not a build error. If you changed anything under Microsoft.DotNet.Wpf/src, run without -Fast."
    }
}

# Run everything from OpenDevelop's repo root so its global.json (SDK 10.x) wins over
# openwpf/global.json (11.x preview).
Push-Location $repoRoot
try {
    foreach ($item in $subsidiary) {
        $dir = Split-Path -Parent $item.Project
        Remove-DirIfExists (Join-Path $dir 'bin\Release')
        Remove-DirIfExists (Join-Path $dir 'obj\Release')
    }

    if ($Fast) {
        Test-TransportStaleness
        Write-Host "==> [-Fast] Building ProGPU.Wpf.Interop + ProGPU.Wpf..."
        Build-Clean $interopProject $progpuVersion
        Build-Clean $progpuProject $wpfVersion
        Remove-Item -LiteralPath (Join-Path $packageOutput "LibreWPF.Interop.$progpuVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $packageOutput "LibreWPF.ProGPU.$wpfVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Invoke-Pack $interopProject $progpuVersion
        Invoke-Pack $progpuProject $wpfVersion
        Clear-PackageCache 'LibreWPF.Interop'
        Clear-PackageCache 'LibreWPF.ProGPU'
    }
    else {
        Write-Host "==> Building the full Microsoft.DotNet.Wpf tree + ProGPU.Wpf..."
        Build-Clean $interopProject $progpuVersion
        foreach ($name in @(
                'PresentationBuildTasks\PresentationBuildTasks'
                'WindowsBase\WindowsBase'
                'System.Xaml\System.Xaml'
                'UIAutomation\UIAutomationTypes\UIAutomationTypes'
                'UIAutomation\UIAutomationProvider\UIAutomationProvider'
                'System.Windows.Input.Manipulations\System.Windows.Input.Manipulations'
                'System.Windows.Primitives\System.Windows.Primitives'
                'PresentationCore\PresentationCore'
                'ReachFramework\ReachFramework'
                'PresentationUI\PresentationUI'
                'PresentationFramework\PresentationFramework'
                'Themes\PresentationFramework.Aero\PresentationFramework.Aero'
                'Themes\PresentationFramework.Aero2\PresentationFramework.Aero2'
                'Themes\PresentationFramework.AeroLite\PresentationFramework.AeroLite'
                'Themes\PresentationFramework.Classic\PresentationFramework.Classic'
                'Themes\PresentationFramework.Fluent\PresentationFramework.Fluent'
                'Themes\PresentationFramework.Luna\PresentationFramework.Luna'
                'Themes\PresentationFramework.Royale\PresentationFramework.Royale'
                'System.Windows.Controls.Ribbon\System.Windows.Controls.Ribbon'
            )) {
            Build-Clean (Join-Path $wpfSrc "$name.csproj") $wpfVersion
        }
        Build-Clean $progpuProject $wpfVersion

        Remove-Item -LiteralPath (Join-Path $packageOutput "LibreWPF.Interop.$progpuVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $packageOutput "LibreWPF.ProGPU.$wpfVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $packageOutput "LibreWPF.Transport.$wpfVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Invoke-Pack $interopProject $progpuVersion
        Invoke-Pack $progpuProject $wpfVersion
        # Transport packs already-staged content from ArtifactsPackagingDir (populated as a side
        # effect of the projects above building), not its own compiled source.
        Invoke-PackFull $transportProject $wpfVersion

        Clear-PackageCache 'LibreWPF.Interop'
        Clear-PackageCache 'LibreWPF.ProGPU'
        Clear-PackageCache 'LibreWPF.Transport'
    }

    foreach ($item in $subsidiary) {
        Remove-Item -LiteralPath (Join-Path $packageOutput "$($item.PackageId).$progpuVersion.nupkg") -Force -ErrorAction SilentlyContinue
        Invoke-Pack $item.Project $progpuVersion
        Clear-PackageCache $item.PackageId
    }

    # Re-extract the freshly-packed (and just cache-cleared) LibreWPF packages into OpenDevelop's
    # restore graph. dist.ps1 restores again anyway, but doing it here keeps this script
    # correct when run standalone.
    Write-Host "==> Restoring OpenDevelop against the local feed..."
    & $dotnet restore (Join-Path $repoRoot 'OpenDevelop.Mvp.slnx') --force --no-cache
    if ($LASTEXITCODE -ne 0) { Fail "restore failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

Write-Host ""
Write-Host "Done: packages in $packageOutput"
