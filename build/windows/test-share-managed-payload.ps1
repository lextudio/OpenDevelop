#!/usr/bin/env pwsh
#
# Regression test for the shared managed closure rules in dist.ps1 (Windows payload only).
#
# `Share-ManagedPayloadAssets` deletes a host's private copy of a managed asset and leaves one
# copy in shared/managed/<library>/<version>/<asset>, which every designer child reaches through
# --additionalprobingpath. Two rules keep that safe, and both are cheap to get wrong because the
# damage only appears later, when a packaged designer child fails to load an assembly:
#
#   * an identity that resolves to more than one content hash across hosts is never shared - the
#     store path is the identity, so one host's reference assembly would otherwise be loaded by a
#     host that needs the implementation (the packaged WPF designer died with
#     "Could not load ICSharpCode.Designer.Remote, Version=5.7.1.0" for exactly that reason);
#   * an identity with the same bytes everywhere is shared, i.e. its copies are removed.
#
# The function is exercised from dist.ps1 itself (sliced out by its section header), not from a copy,
# so the test cannot drift away from the packaging code.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$distPath = Join-Path $repoRoot 'dist.ps1'
$source = Get-Content -LiteralPath $distPath -Raw
$start = $source.IndexOf('# Shared managed closure for the out-of-process hosts')
$end = $source.IndexOf('function Remove-LinkTimeDepsAssets')
if ($start -lt 0 -or $end -lt $start) {
    throw "could not find the payload sharing functions in $distPath"
}
Invoke-Expression $source.Substring($start, $end - $start)

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('od-share-test-' + [guid]::NewGuid().ToString('N'))

function New-TestHost {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Library,
        [Parameter(Mandatory)][string]$Content
    )
    $directory = Join-Path $root "AddIns/$Name/Host"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $asset = "$Library.dll"
    $deps = [ordered]@{
        targets = [ordered]@{
            'net10.0' = [ordered]@{ "$Library/1.0.0" = [ordered]@{ runtime = [ordered]@{ $asset = @{} } } }
        }
    }
    $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory "$Name.deps.json")
    Set-Content -LiteralPath (Join-Path $directory "$Name.runtimeconfig.json") -Value '{}'
    Set-Content -LiteralPath (Join-Path $directory $asset) -Value $Content
}

try {
    # One library identity, two contents, and each content duplicated in a second host - so the
    # removal rule would apply to both and, before the conflict rule, one content would land in the
    # store and every host's own copy would disappear.
    New-TestHost -Name 'A' -Library 'Lib.Identity' -Content 'reference-5.7.0.0'
    New-TestHost -Name 'B' -Library 'Lib.Identity' -Content 'reference-5.7.0.0'
    New-TestHost -Name 'C' -Library 'Lib.Identity' -Content 'implementation-5.7.1.0'
    New-TestHost -Name 'D' -Library 'Lib.Identity' -Content 'implementation-5.7.1.0'
    # One library identity with identical bytes everywhere: this one must still be shared.
    New-TestHost -Name 'E' -Library 'Shared.Identity' -Content 'same-bytes'
    New-TestHost -Name 'F' -Library 'Shared.Identity' -Content 'same-bytes'

    $env:OD_SHARE_MANAGED_CLOSURE = '1'
    $shared = Share-ManagedPayloadAssets -PayloadRoot $root -RootDepsName 'OpenDevelop.deps.json'
    Test-SharedManagedPayload -PayloadRoot $root -SharedIdentities $shared

    $store = Join-Path $root 'shared/managed'
    $problems = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath (Join-Path $store 'Lib.Identity/1.0.0/Lib.Identity.dll')) {
        $problems.Add('the conflicting identity was stored, so one host would load the other content')
    }
    $lost = @('A', 'B', 'C', 'D') | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $root "AddIns/$_/Host/Lib.Identity.dll"))
    }
    if ($lost) {
        $problems.Add("the conflicting identity lost its per-host copies: $($lost -join ',')")
    }
    if (-not (Test-Path -LiteralPath (Join-Path $store 'Shared.Identity/1.0.0/Shared.Identity.dll'))) {
        $problems.Add('an identity with identical content everywhere was not stored')
    }
    $kept = @('E', 'F') | Where-Object {
        Test-Path -LiteralPath (Join-Path $root "AddIns/$_/Host/Shared.Identity.dll")
    }
    if ($kept) {
        $problems.Add("the identical identity kept its per-host copies: $($kept -join ',')")
    }

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Host "::error::share-managed payload test: $problem" }
        exit 1
    }
    Write-Host 'The payload sharing rules passed.'
    exit 0
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
