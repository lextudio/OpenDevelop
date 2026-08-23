#
# dist.macos.ps1 — build a framework-dependent macOS .dmg for local testing.
# Mirrors the CI steps in .github/workflows/package.yml. All real logic lives here;
# dist.macos.sh is a thin wrapper that execs this file.
#
# Usage: ./dist.macos.ps1 [-SkipPublish] [-Configuration Debug|Release]
#   -SkipPublish            reuse existing publish output (faster iteration on bundle/dmg)
#   -Configuration Debug    package the Debug configuration instead of Release.
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
$sln = Join-Path $repoRoot 'OpenDevelop.Mvp.slnx'
$hostProject = Join-Path $repoRoot 'src/Main/SharpDevelop/SharpDevelop.csproj'
$dotnet = Find-DotNetHost

function New-TempDir {
    $p = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $p | Out-Null
    return $p
}

# Restore the solution up front. The build below runs with --no-restore, so a stale or
# corrupt project.assets.json left by an earlier restore (e.g. after a NuGet.config
# change or a restore for a different RID/TFM combination) would otherwise surface as an
# obscure "ResolvePackageAssets" NullReferenceException deep inside the build - exactly
# the failure mode seen for the AspNetCore projects. Restore is incremental, so this
# stays cheap when nothing changed; it also fails fast with a clear error instead of
# leaving a half-built tree.
Write-Host '==> Restoring solution...'
Restore-Solution -DotNet $dotnet -Solution $sln

# Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources (WPF theme
# resource blob) can otherwise stale-cross from a previous build and produce a 12-byte
# corrupt file that crashes at boot with EndOfStreamException in FindResource /
# LoadThemedDictionary.
$corePres = Join-Path $repoRoot 'src/Main/ICSharpCode.Core.Presentation'
foreach ($sub in 'obj', 'bin') {
    $dir = Join-Path $corePres "$sub/$config"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

if (-not $SkipPublish) {
    # Clear the shared intermediate output so publish cannot reuse artifacts left by a
    # previous build. This distribution intentionally remains framework-dependent and
    # uses the installed .NET runtime; the SDK-generated apphost is only the native
    # entry point and does not bundle that runtime.
    Write-Host '==> Cleaning intermediate outputs...'
    $hostObj = Join-Path $repoRoot "src/Main/SharpDevelop/obj/$config/net10.0-windows"
    if (Test-Path $hostObj) { Remove-Item -Recurse -Force $hostObj }

    Write-Host "==> Publishing framework-dependent app ($config)..."
    $publishDir = Join-Path $repoRoot "src/Main/SharpDevelop/bin/$config/net10.0-windows/publish"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Invoke-Native $dotnet publish $hostProject -c $config --self-contained false `
        "-p:OpenDevelopDistributionBuild=true" `
        "-p:PublishDir=$publishDir"

    if (-not (Test-Path $publishDir)) {
        throw "dist.macos.ps1: host publish directory not found: $publishDir"
    }

    # NuGet conflict resolution omits LibreWinForms from the standard publish closure.
    # Patch the final manifest and copy its matching runtime files.
    $nugetPackagesLine = & $dotnet nuget locals global-packages --list |
        Select-String '^global-packages: '
    if (-not $nugetPackagesLine) {
        throw "dist.macos.ps1: cannot determine the NuGet global-packages directory"
    }
    $nugetPackages = ($nugetPackagesLine.Line -replace '^global-packages:\s*', '')
    Invoke-Native python3 (Join-Path $repoRoot 'build/patch-librewinforms-deps.py') `
        (Join-Path $publishDir 'OpenDevelop.deps.json') $nugetPackages

    # Some projects write to OpenDevelopHostPublishDir while computing their
    # distribution closure. Give that build a disposable copy so the verified host
    # deployment above remains immutable.
    $hostPublishSnapshot = New-TempDir
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $hostPublishSnapshot -Recurse -Force

    Write-Host '==> Cleaning stale AddIn outputs...'
    Get-ChildItem (Join-Path $repoRoot 'AddIns') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.dll', '.dylib', '.so', '.pdb' } |
        Remove-Item -Force

    Write-Host '==> Building distribution AddIns without shared runtime copies...'
    Build-Solution -DotNet $dotnet -Solution $sln -Configuration $config -ExtraProperties @(
        '-p:OpenDevelopDistributionBuild=true',
        '-p:OpenDevelopDistributionRidFamily=osx',
        "-p:OpenDevelopHostPublishDir=$hostPublishSnapshot",
        '-p:ProGpuWpfCopyPackageRuntimeAssets=false'
    )
    Remove-Item -Recurse -Force $hostPublishSnapshot

    # The solution traversal may copy reference assemblies over the original PublishDir
    # through cached project state. Restore the authoritative package runtime payload
    # only after every build has completed.
    Invoke-Native python3 (Join-Path $repoRoot 'build/patch-librewinforms-deps.py') `
        (Join-Path $publishDir 'OpenDevelop.deps.json') $nugetPackages
}
else {
    Write-Host '==> Skipping publish (-SkipPublish)'
}

Write-Host "==> Building framework-dependent .app bundle ($config)..."
$env:DIST_CONFIG = $config
& bash (Join-Path $repoRoot 'build/macos/build-application-bundle.sh')
if ($LASTEXITCODE -ne 0) { throw "build-application-bundle.sh exited with code $LASTEXITCODE" }

Write-Host '==> Smoke-testing packaged app...'
$appBin = Join-Path $repoRoot 'OpenDevelop.app/Contents/MacOS/OpenDevelop'
$smokeTag = [System.Guid]::NewGuid().ToString('N')
# Start-Process rejects using ONE file for both streams; keep two.
$smokeOut = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$smokeTag.out.log"
$smokeErr = Join-Path ([System.IO.Path]::GetTempPath()) "opendevelop-smoke-$smokeTag.err.log"
$smoke = Start-Process -FilePath $appBin -RedirectStandardOutput $smokeOut -RedirectStandardError $smokeErr -PassThru
$smokeOk = $true
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    if ($smoke.HasExited) {
        $smokeOk = $false
        break
    }
}
if (-not $smokeOk) {
    Write-Host "dist.macos.ps1: packaged app exited during startup (status $($smoke.ExitCode))" -ForegroundColor Red
    foreach ($log in $smokeOut, $smokeErr) {
        if (Test-Path $log) { Get-Content $log -TotalCount 160 | Write-Host }
    }
    Remove-Item -Force $smokeOut, $smokeErr -ErrorAction SilentlyContinue
    exit 1
}
Stop-Process -Id $smoke.Id -Force -ErrorAction SilentlyContinue
Remove-Item -Force $smokeOut, $smokeErr -ErrorAction SilentlyContinue
Write-Host 'Packaged app startup smoke test passed'

Write-Host '==> Building .dmg...'
Push-Location $repoRoot
try {
    & bash (Join-Path $repoRoot 'build/macos/build-dmg.sh') OpenDevelop.app OpenDevelop-macos.dmg
    if ($LASTEXITCODE -ne 0) { throw "build-dmg.sh exited with code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "Done: $(Join-Path $repoRoot 'OpenDevelop-macos.dmg')"
