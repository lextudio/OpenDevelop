#!/usr/bin/env pwsh
#
# Registers the LibreWPF managed payload in a host .deps.json so the CoreCLR host can load it from the app base.
#
#   pwsh .github/ci/patch-host-deps.ps1 [-HostDirectory <dir>] [-ReportOnly]
#
# Why this exists: on Windows the SDK's portable bootstrap returns before preloading the payload, and the deps manifest
# that `dotnet build` / `dotnet publish` produce here lists the transport assemblies as package-relative assets
# (`lib/net10.0/PresentationFramework.dll`). The host then resolves them against the package folder, finds nothing, keeps
# them out of TRUSTED_PLATFORM_ASSEMBLIES, and the IDE dies at start-up with FileNotFoundException 11.0.0.0 for a file
# that sits right next to it. The shipped Windows payload (v5.6.2) instead carries app-local library entries
# (`PresentationFramework/11.0.0.0` with runtime `PresentationFramework.dll`), which is what this script adds.
#
# Detection first, by design: the payload set is read from the deps' own LibreWPF.Transport library and every file is
# checked for an existing app-local (or RID-shaped) entry that resolves under the host directory. When the SDK already
# produces those entries - a future LibreWPF release - every file is resolvable, nothing is written, and this script
# reports that instead of layering a conflicting patch on top.
[CmdletBinding()]
param(
    [string]$HostDirectory,
    [string]$DepsPath,
    [switch]$ReportOnly
)

$ErrorActionPreference = 'Stop'

if (-not $HostDirectory) {
    $HostDirectory = Join-Path $PSScriptRoot '../../src/Main/SharpDevelop/bin/Debug/net10.0-windows'
}
$HostDirectory = (Resolve-Path $HostDirectory).Path
if (-not $DepsPath) { $DepsPath = Join-Path $HostDirectory 'OpenDevelop.deps.json' }
$DepsPath = (Resolve-Path $DepsPath).Path

$deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $deps['targets']) { throw "$DepsPath has no 'targets'; not a host manifest." }
if (-not $deps['libraries']) { $deps['libraries'] = [ordered]@{} }
$libraries = $deps['libraries']

# The payload is whatever the managed transport library declares as its runtime assets: that keeps the file list
# version-independent instead of hardcoding assembly names here.
function Get-TransportPayload([hashtable]$target) {
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($library in $target.Keys) {
        if ($library -notmatch '^(?i)LibreWPF\.Transport/') { continue }
        $entry = $target[$library]
        foreach ($group in @('runtime', 'runtimeTargets')) {
            if (-not $entry[$group]) { continue }
            foreach ($asset in $entry[$group].Keys) {
                $names.Add(($asset -split '/')[-1])
            }
        }
    }
    return $names
}

# A library entry resolves a file when its runtime asset is the bare file name or a runtimes/<rid>/... path whose file is
# actually in the host directory. Package-relative lib/<tfm>/... paths do not: that is the broken shape.
function Test-Resolvable([hashtable]$target, [string]$fileName) {
    foreach ($library in $target.Keys) {
        $entry = $target[$library]
        foreach ($group in @('runtime', 'runtimeTargets')) {
            if (-not $entry[$group]) { continue }
            foreach ($asset in $entry[$group].Keys) {
                if (($asset -split '/')[-1] -ne $fileName) { continue }
                if ($asset -notmatch '/') { return $true }
                if (Test-Path (Join-Path $HostDirectory ($asset -replace '/', [System.IO.Path]::DirectorySeparatorChar))) {
                    return $true
                }
            }
        }
    }
    return $false
}

$patchedTargets = 0
$added = [System.Collections.Generic.List[string]]::new()
foreach ($targetName in $deps['targets'].Keys) {
    $target = $deps['targets'][$targetName]
    $payload = Get-TransportPayload $target
    # Say what the detection saw, so a "nothing to do" can be checked against the manifest instead of trusted: which
    # libraries carry PresentationFramework.dll, with which asset path, and whether the transport payload names it.
    $frameworkAssets = @(foreach ($library in $target.Keys) {
        foreach ($group in @('runtime', 'runtimeTargets')) {
            if (-not $target[$library][$group]) { continue }
            foreach ($asset in $target[$library][$group].Keys) {
                if (($asset -split '/')[-1] -eq 'PresentationFramework.dll') { "$library [$group] $asset" }
            }
        }
    })
    Write-Host "patch-host-deps: target '$targetName': transport payload $($payload.Count) file(s), names PresentationFramework.dll: $($payload -contains 'PresentationFramework.dll'); PresentationFramework.dll assets: $(if ($frameworkAssets.Count) { $frameworkAssets -join '; ' } else { 'none' })"
    if ($payload.Count -eq 0) { continue }

    $missing = @($payload | Sort-Object -Unique | Where-Object {
        $file = Join-Path $HostDirectory $_
        (Test-Path $file) -and -not (Test-Resolvable $target $_)
    })
    if ($missing.Count -eq 0) { continue }

    foreach ($fileName in $missing) {
        $path = Join-Path $HostDirectory $fileName
        # The payload also carries native images (PenImc_cor3.dll and friends). They are found through
        # NATIVE_DLL_SEARCH_DIRECTORIES, not through a managed library entry, and reading them as assemblies throws.
        $assemblyName = try { [System.Reflection.AssemblyName]::GetAssemblyName($path) } catch { $null }
        if ($null -eq $assemblyName) {
            Write-Verbose "patch-host-deps: '$fileName' is not a managed assembly; left to the native search paths."
            continue
        }
        $fileVersion = (Get-Item $path).VersionInfo.FileVersion
        $key = "$($assemblyName.Name)/$($assemblyName.Version)"
        $metadata = [ordered]@{ assemblyVersion = [string]$assemblyName.Version }
        if ($fileVersion) { $metadata['fileVersion'] = [string]$fileVersion }
        if (-not $target.ContainsKey($key)) {
            $target[$key] = [ordered]@{ runtime = [ordered]@{ $fileName = $metadata } }
        }
        elseif ($target[$key]['runtime'] -and -not $target[$key]['runtime'].ContainsKey($fileName)) {
            $target[$key]['runtime'][$fileName] = $metadata
        }
        # The host resolves a target entry only through its 'libraries' record; a 'reference' record is what makes the
        # asset resolve app-base instead of looking for a package path. Without this half the entry above is ignored
        # (measured: the entry alone still produced FileNotFoundException 11.0.0.0).
        if (-not $libraries.ContainsKey($key)) {
            $libraries[$key] = [ordered]@{ type = 'reference'; serviceable = $false; sha512 = '' }
        }
        $added.Add($fileName)
    }
    $patchedTargets++
}

if ($added.Count -eq 0) {
    Write-Host "patch-host-deps: the manifest already resolves every LibreWPF transport payload assembly from the host directory; nothing to do."
    exit 0
}
if ($ReportOnly) {
    Write-Host "patch-host-deps: would add app-local entries for $($added.Count) assemblies: $(($added | Sort-Object -Unique) -join ', ')"
    exit 0
}

$json = $deps | ConvertTo-Json -Depth 32
Set-Content -LiteralPath $DepsPath -Value $json -Encoding utf8
Write-Host "patch-host-deps: added app-local entries for $($added.Count) assemblies in $patchedTargets target(s): $(($added | Sort-Object -Unique) -join ', ')"
