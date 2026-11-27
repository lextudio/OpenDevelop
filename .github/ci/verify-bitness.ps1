#!/usr/bin/env pwsh
#
# Bitness checks for a CI lane, in both directions.
#
#   pwsh .github/ci/verify-bitness.ps1 -Platform windows-arm64 -PayloadRoot <host dir> -PayloadOnly
#   pwsh .github/ci/verify-bitness.ps1 -Platform windows-arm64 -PayloadRoot <host dir> -RunnerOnly
#
# Why: the Windows distribution is one payload for x64 and ARM64 (dist.windows.bat ships runtimes/ plus per-RID
# win-x64/win-arm64 folders and both launchers), so a single build feeds both Windows lanes. That only holds while two
# things stay true, and CI is where they can silently rot:
#
#   * every IL-only assembly outside the per-RID trees is AnyCPU. One architecture-specific managed assembly passes on
#     the architecture that built it and fails on the other with "The assembly architecture is not compatible with the
#     current process architecture" - the check is build/payload-architecture.ps1, the same one dist.ps1 runs.
#   * a lane runs the architecture its platform promises, and the payload carries that architecture's assets. A lane
#     that quietly falls back to the host's architecture would test x64 twice and report coverage it does not have.
param(
    [Parameter(Mandatory)][ValidateSet('windows-x64', 'windows-arm64', 'macos-arm64', 'linux-x64', 'linux-arm64')][string]$Platform,
    [string]$PayloadRoot,
    [switch]$PayloadOnly,
    [switch]$RunnerOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-plan.json') -Raw | ConvertFrom-Json
$platformEntry = $plan.platforms.PSObject.Properties[$Platform]
# The two Linux values are intentionally usable before the test plan enables Linux jobs: the
# distribution packager and its focused payload checks need to prove their contract first. Once
# a platform is enabled, its plan RID remains the source of truth and is checked against this map.
$expectedRid = switch ($Platform) {
    'linux-x64' { 'linux-x64' }
    'linux-arm64' { 'linux-arm64' }
    default {
        if ($null -eq $platformEntry) { throw "Platform '$Platform' is not defined in test-plan.json." }
        [string]$platformEntry.Value.rid
    }
}
if (-not $expectedRid) { throw "Platform '$Platform' has no 'rid'; the bitness checks need one." }
if ($null -ne $platformEntry -and [string]$platformEntry.Value.rid -ne $expectedRid) {
    throw "Platform '$Platform' declares RID '$($platformEntry.Value.rid)', but the bitness contract expects '$expectedRid'."
}

$expectedArchitecture = switch -Regex ($expectedRid) {
    'arm64$' { 'Arm64'; break }
    'x64$' { 'X64'; break }
    'x86$' { 'X86'; break }
    default { throw "Cannot derive an architecture from RID '$expectedRid'." }
}

if (-not $PayloadRoot) { $PayloadRoot = Join-Path $repoRoot 'src/Main/SharpDevelop/bin/Debug/net11.0-windows' }
if (Test-Path $PayloadRoot) { $PayloadRoot = (Resolve-Path $PayloadRoot).Path }

if (-not $RunnerOnly) {
    if (-not (Test-Path $PayloadRoot)) { throw "Payload directory '$PayloadRoot' does not exist; the build did not produce a host payload." }

    . (Join-Path $repoRoot 'build/payload-architecture.ps1')
    Test-PayloadAssemblyArchitecture -PayloadRoot $PayloadRoot
    Write-Host "verify-bitness: every IL-only assembly outside the per-RID trees is AnyCPU."

    if ($Platform -like 'windows-*') {
        # Both Windows RIDs must be present because the one payload serves both lanes. Since the closure stopped
        # shipping a per-RID managed copy under runtimes/<rid>/lib (openavalon's audit rejects it), the per-architecture
        # part of the payload is its native tree.
        $own = Join-Path $PayloadRoot "runtimes/$expectedRid/native"
        $dlls = @(Get-ChildItem -LiteralPath $own -Filter *.dll -File -ErrorAction SilentlyContinue).Count
        if ($dlls -eq 0) { throw "The payload has no '$expectedRid' assets at runtimes/$expectedRid/native." }
        $siblingRid = if ($expectedRid -eq 'win-x64') { 'win-arm64' } else { 'win-x64' }
        $sibling = Join-Path $PayloadRoot "runtimes/$siblingRid/native"
        $siblingCount = @(Get-ChildItem -LiteralPath $sibling -Filter *.dll -File -ErrorAction SilentlyContinue).Count
        if ($siblingCount -eq 0) { throw "The unified Windows payload is missing its '$siblingRid' assets at runtimes/$siblingRid/native." }
        # The closure stopped shipping its per-RID managed copy (PresentationFramework, PresentationCore,
        # WindowsBase, ...) under runtimes/<rid>/lib; one left over from an older closure shadows the
        # RID-neutral managed payload in deps resolution (the failure launch.ps1 documents: an old
        # PresentationCore beside a new PresentationFramework). Only those framework assemblies are
        # rejected: the release payload legitimately stages mixed-mode ProGPU/WinForms assets in the same
        # directory, and this check never runs against a release payload anyway.
        $staleFramework = foreach ($rid in @('win-x64', 'win-arm64')) {
            foreach ($assembly in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
                $candidate = Join-Path $PayloadRoot "runtimes/$rid/lib/net10.0/$assembly"
                if (Test-Path -LiteralPath $candidate) { $candidate }
            }
        }
        if (@($staleFramework).Count -gt 0) {
            throw "The payload still carries a per-RID managed copy from an older closure: $(@($staleFramework) -join ', ')."
        }
        Write-Host "verify-bitness: payload carries $dlls $expectedRid and $siblingCount $siblingRid native assemblies."
    }
    elseif ($Platform -like 'macos-*') {
        # dist.macos.sh ships osx-arm64 only and the portable bootstrap loads the payload from the app base.
        foreach ($name in 'PresentationFramework.dll', 'WindowsBase.dll') {
            if (-not (Test-Path (Join-Path $PayloadRoot $name))) {
                throw "The macOS payload is missing '$name' beside OpenDevelop.dll; the portable bootstrap cannot preload it."
            }
        }
        Write-Host "verify-bitness: macOS payload carries the app-base LibreWPF payload."
    }
    elseif ($Platform -like 'linux-*') {
        # Linux releases are one native RID per archive.  The portable bootstrap must load the
        # LibreWPF managed runtime from the app base, while terminal/native dependencies resolve
        # from the matching Linux RID tree; retaining another architecture here would make an
        # apparently-native lane capable of exercising the wrong payload.
        foreach ($name in 'PresentationFramework.dll', 'WindowsBase.dll') {
            if (-not (Test-Path (Join-Path $PayloadRoot $name))) {
                throw "The Linux payload is missing '$name' beside OpenDevelop.dll; the portable bootstrap cannot preload it."
            }
        }
        $otherRid = if ($expectedRid -eq 'linux-x64') { 'linux-arm64' } else { 'linux-x64' }
        # SPDX and notice generation preserves the NuGet package layout under THIRD-PARTY-NOTICES,
        # including its runtimes directories. Those are text-only attribution copies, not loadable
        # payload assets, so limiting this scan to the actual deployed tree avoids treating a notice
        # for the other RID as a foreign native binary.
        foreach ($runtimesDir in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Directory -Filter runtimes -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '(^|[\\/])THIRD-PARTY-NOTICES([\\/]|$)' }) {
            if (Test-Path -LiteralPath (Join-Path $runtimesDir.FullName $otherRid)) {
                throw "The Linux payload retains foreign native assets at $($runtimesDir.FullName)/$otherRid. Linux release archives must be architecture-specific."
            }
        }
        Write-Host "verify-bitness: Linux payload carries app-base LibreWPF and only its $expectedRid native trees."
    }
}

if (-not $PayloadOnly) {
    $info = (& dotnet --info 2>&1 | Out-String)
    if ($info -notmatch "(?m)^\s*RID:\s+$([regex]::Escape($expectedRid))\s*$") {
        $found = ([regex]::Match($info, '(?m)^\s*RID:\s+(\S+)\s*$')).Groups[1].Value
        throw "This runner's .NET RID is '$found', but platform '$Platform' expects '$expectedRid'. The lane would test the wrong architecture."
    }
    $processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    if ($processArchitecture -ne $expectedArchitecture) {
        throw "This shell runs as $processArchitecture, but platform '$Platform' expects $expectedArchitecture."
    }
    Write-Host "verify-bitness: runner RID is $expectedRid and this process runs as $processArchitecture."
}
