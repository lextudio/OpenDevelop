#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build a framework-dependent Windows distribution payload (folder + .zip) for local testing.

.DESCRIPTION
  Windows counterpart of dist.macos.sh. Same publish/patch/AddIn-build pipeline; the only
  difference is packaging: instead of an .app bundle + .dmg, this produces a flat payload
  directory and a .zip beside it.

  Note this app runs on LibreWPF (portable WPF) on Windows too - SharpDevelop.csproj strips the
  Microsoft.WindowsDesktop.App runtime framework on every platform - so the LibreWinForms
  deps.json patch is required here exactly as it is on macOS. The Win32 compatibility shims
  (kernel32.dll, user32.dll, ...) that the macOS bundle carries are emitted by LibreWPF.Sdk only
  on OSX/Linux and must NOT be present on Windows, where those names belong to the real OS DLLs.

.PARAMETER SkipPublish
  Reuse existing publish output (faster iteration on payload/zip).

.PARAMETER Debug
  Package the Debug configuration instead of Release.

.EXAMPLE
  ./dist.windows.ps1
  ./dist.windows.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$DebugConfig
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
$hostProject = Join-Path $scriptDir 'src\Main\SharpDevelop\SharpDevelop.csproj'
$mvpSolution = Join-Path $scriptDir 'OpenDevelop.Mvp.slnx'
$config = if ($DebugConfig) { 'Debug' } else { 'Release' }
$tfm = 'net10.0-windows'

function Fail([string]$message) {
    Write-Error "dist.windows.ps1: $message"
    exit 1
}

function Remove-DirIfExists([string]$path) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force -Confirm:$false
    }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) { Fail 'cannot find dotnet on PATH' }

$python = (Get-Command python -ErrorAction SilentlyContinue)?.Source
if (-not $python) { $python = (Get-Command python3 -ErrorAction SilentlyContinue)?.Source }
if (-not $python) { Fail 'cannot find python (needed for build/patch-librewinforms-deps.py)' }

$baseDir = Join-Path $scriptDir "src\Main\SharpDevelop\bin\$config\$tfm"
$publishDir = Join-Path $baseDir 'publish'
$payloadRoot = Join-Path $scriptDir 'OpenDevelop-win'
$zipPath = Join-Path $scriptDir 'OpenDevelop-windows.zip'
$depsJson = Join-Path $publishDir 'OpenDevelop.deps.json'
$patchScript = Join-Path $scriptDir 'build\patch-librewinforms-deps.py'

# Ensure clean state for ICSharpCode.Core.Presentation - its .g.resources (WPF theme resource
# blob) can otherwise stale-cross from a previous build and produce a 12-byte corrupt file that
# crashes at boot with EndOfStreamException in FindResource / LoadThemedDictionary.
$corePres = Join-Path $scriptDir 'src\Main\ICSharpCode.Core.Presentation'
Remove-DirIfExists (Join-Path $corePres "obj\$config")
Remove-DirIfExists (Join-Path $corePres "bin\$config")

if (-not $SkipPublish) {
    # src\Main\GlobalAssemblyInfo.cs is generated from GlobalAssemblyInfo.cs.template (it is
    # gitignored) and is <Compile Include>d by nearly every project, so a clean checkout fails
    # with CS2001 before anything else runs. UpdateAssemblyInfo also stamps the current git
    # commit hash and revision count into the assembly version, which a release build wants
    # regenerated rather than reused.
    Write-Host "==> Generating GlobalAssemblyInfo.cs..."
    Push-Location $scriptDir
    try {
        & $dotnet run --project (Join-Path $scriptDir 'src\Tools\UpdateAssemblyInfo\UpdateAssemblyInfo.csproj') -c Release
        if ($LASTEXITCODE -ne 0) { Fail "UpdateAssemblyInfo failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
    if (-not (Test-Path -LiteralPath (Join-Path $scriptDir 'src\Main\GlobalAssemblyInfo.cs'))) {
        Fail 'UpdateAssemblyInfo did not produce src\Main\GlobalAssemblyInfo.cs'
    }

    Write-Host "==> Cleaning intermediate outputs..."
    Remove-DirIfExists (Join-Path $scriptDir "src\Main\SharpDevelop\obj\$config\$tfm")

    # src\Libraries\AvalonEdit\global.json (submodule) has no "msbuild-sdks" entry and shadows
    # the repo-root one for every project beneath it, so LibreWPF.Sdk resolution there depends on
    # the root-level resolution already being cached for the build session. Restoring as its own
    # invocation seeds that reliably; publishing in the same command as an up-to-date restore does
    # not, and fails with MSB4236 "The SDK 'LibreWPF.Sdk' specified could not be found".
    Write-Host "==> Restoring ($config)..."
    & $dotnet restore $hostProject
    if ($LASTEXITCODE -ne 0) { Fail "restore failed ($LASTEXITCODE)" }

    Write-Host "==> Publishing framework-dependent app ($config)..."
    Remove-DirIfExists $publishDir
    & $dotnet publish $hostProject -c $config --no-restore --self-contained false `
        -p:OpenDevelopDistributionBuild=true `
        -p:PublishDir="$publishDir"
    if ($LASTEXITCODE -ne 0) { Fail "host publish failed ($LASTEXITCODE)" }

    if (-not (Test-Path -LiteralPath $publishDir)) {
        Fail "host publish directory not found: $publishDir"
    }

    # NuGet conflict resolution omits LibreWinForms from the standard publish closure.
    # Patch the final manifest and copy its matching runtime files.
    $nugetPackages = (& $dotnet nuget locals global-packages --list |
        Select-String -Pattern '^\s*(?:info : )?global-packages: (.+)$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } |
        Select-Object -First 1)
    if (-not $nugetPackages) { Fail 'cannot determine the NuGet global-packages directory' }

    & $python $patchScript $depsJson $nugetPackages
    if ($LASTEXITCODE -ne 0) { Fail "patch-librewinforms-deps.py failed ($LASTEXITCODE)" }

    # Some projects write to OpenDevelopHostPublishDir while computing their distribution
    # closure. Give that build a disposable copy so the verified host deployment above stays
    # immutable.
    $hostSnapshot = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-host-publish-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $hostSnapshot -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $hostSnapshot -Recurse -Force

    Write-Host "==> Cleaning stale AddIn outputs..."
    Get-ChildItem -LiteralPath (Join-Path $scriptDir 'AddIns') -Recurse -File `
        -Include '*.dll', '*.dylib', '*.so', '*.pdb' -ErrorAction SilentlyContinue |
        Remove-Item -Force -Confirm:$false

    Write-Host "==> Building distribution AddIns without shared runtime copies..."
    & $dotnet build $mvpSolution -c $config --no-restore `
        -p:OpenDevelopDistributionBuild=true `
        -p:OpenDevelopHostPublishDir="$hostSnapshot" `
        -p:ProGpuWpfCopyPackageRuntimeAssets=false
    $buildExit = $LASTEXITCODE
    Remove-DirIfExists $hostSnapshot
    if ($buildExit -ne 0) { Fail "AddIn solution build failed ($buildExit)" }

    # The solution traversal may copy reference assemblies over the original PublishDir through
    # cached project state. Restore the authoritative package runtime payload only after every
    # build has completed.
    & $python $patchScript $depsJson $nugetPackages
    if ($LASTEXITCODE -ne 0) { Fail "patch-librewinforms-deps.py failed ($LASTEXITCODE)" }
}
else {
    Write-Host "==> Skipping publish (-SkipPublish)"
}

Write-Host "==> Assembling distribution payload ($config)..."
if (-not (Test-Path -LiteralPath $publishDir)) {
    Fail "framework-dependent publish directory not found: $publishDir"
}

Remove-DirIfExists $payloadRoot
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $payloadRoot -Recurse -Force

# OpenDevelop locates its addins and data at runtime by walking UP from the executable looking
# for data\resources\languages\LanguageDefinition.xml (SharpDevelopMain.FindApplicationRootPath),
# then loading *.addin from <root>\AddIns. The payload must therefore contain data\ and AddIns\
# next to the executable so the walk resolves on the first step.
Copy-Item -Path (Join-Path $scriptDir 'data') -Destination (Join-Path $payloadRoot 'data') -Recurse -Force

# AddIn build outputs carry their full dependency closures. Anything already supplied by the
# published host resolves from the application base directory, so skip those files by name
# instead of copying ~2 GB and pruning afterwards. This also keeps stale XML docs, satellite
# resources and native helpers from an old developer build out of the payload.
$hostFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | ForEach-Object { [void]$hostFiles.Add($_.Name) }

$addInsSource = (Resolve-Path (Join-Path $scriptDir 'AddIns')).Path
$addInsTarget = Join-Path $payloadRoot 'AddIns'
$copied = 0
Get-ChildItem -LiteralPath $addInsSource -Recurse -File | ForEach-Object {
    $name = $_.Name
    if ($name -like '*.pdb') { return }
    if ($name -like 'LeXtudio.DevFlow.*') { return }
    if ($name -like 'CliclickSharp*') { return }
    if ($hostFiles.Contains($name)) { return }

    $relative = $_.FullName.Substring($addInsSource.Length).TrimStart('\', '/')
    $destination = Join-Path $addInsTarget $relative
    $destinationDir = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    $script:copied++
}
Write-Host "    AddIn files copied: $copied"

$exePath = Join-Path $payloadRoot 'OpenDevelop.exe'
if (-not (Test-Path -LiteralPath $exePath)) { Fail "packaged executable not found: $exePath" }
Write-Host "Payload ready: $payloadRoot"

Write-Host "==> Smoke-testing packaged app..."
$smokeLog = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-package-smoke-" + [System.Guid]::NewGuid().ToString('N') + '.log')
$smoke = Start-Process -FilePath $exePath -WorkingDirectory $payloadRoot -PassThru `
    -RedirectStandardOutput $smokeLog -RedirectStandardError "$smokeLog.err"
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    if ($smoke.HasExited) {
        Write-Host "dist.windows.ps1: packaged app exited during startup (status $($smoke.ExitCode))"
        foreach ($log in @($smokeLog, "$smokeLog.err")) {
            if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -TotalCount 160 }
        }
        Remove-Item -LiteralPath $smokeLog, "$smokeLog.err" -Force -ErrorAction SilentlyContinue
        exit 1
    }
}
try { $smoke.CloseMainWindow() | Out-Null } catch {}
Start-Sleep -Seconds 2
if (-not $smoke.HasExited) { Stop-Process -Id $smoke.Id -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $smokeLog, "$smokeLog.err" -Force -ErrorAction SilentlyContinue
Write-Host "Packaged app startup smoke test passed"

Write-Host "==> Building .zip..."
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payloadRoot, $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)

Write-Host ""
Write-Host "Done: $zipPath"
