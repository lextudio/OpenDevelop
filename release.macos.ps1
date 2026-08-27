# Produce and publish a macOS OpenDevelop release. The composable Addin SDK is
# installed inside the application bundle; it is intentionally not pushed to NuGet.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$ReleaseTag = "v$Version",
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$PrepareOnly,
    [switch]$AllowDirtyWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $repoRoot 'build/common.psm1') -Force
$dotnet = Find-DotNetHost
$artifactDir = Join-Path $repoRoot "artifacts/release/$Version"

if (-not $AllowDirtyWorktree) {
    $changes = & git -C $repoRoot status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git worktree.' }
    if ($changes) { throw 'Refusing to release a dirty worktree. Commit/stash changes, or use -AllowDirtyWorktree deliberately.' }
}
if (Test-Path $artifactDir) { throw "Release artifact directory already exists: $artifactDir" }
New-Item -ItemType Directory -Path $artifactDir | Out-Null

try {
    Write-Host "==> Packaging macOS application ($Configuration)..."
    $distArgs = @{ Configuration = $Configuration }
    if ($SkipPublish) { $distArgs.SkipPublish = $true }
    & (Join-Path $repoRoot 'dist.macos.ps1') @distArgs

    $dmg = Join-Path $repoRoot 'OpenDevelop-macos.dmg'
    if (-not (Test-Path $dmg)) { throw "macOS package was not produced: $dmg" }
    $releaseDmg = Join-Path $artifactDir "OpenDevelop-$Version-macos.dmg"
    Copy-Item $dmg $releaseDmg

    if ($PrepareOnly) {
        Write-Host "Prepared (not published): $releaseDmg"
        return
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required to publish the release.' }
    $repo = (& gh repo view --json nameWithOwner --jq .nameWithOwner).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'Unable to determine the GitHub repository.' }
    & gh release view $ReleaseTag --repo $repo 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "GitHub release '$ReleaseTag' already exists." }

    # A failure below leaves a recoverable draft, never a partial public release.
    Write-Host "==> Creating GitHub draft release $ReleaseTag..."
    Invoke-Native gh release create $ReleaseTag --repo $repo --draft --title "OpenDevelop $Version" --generate-notes
    Invoke-Native gh release upload $ReleaseTag $releaseDmg --repo $repo
    Write-Host '==> Publishing GitHub release...'
    Invoke-Native gh release edit $ReleaseTag --repo $repo --draft=false
    Write-Host "Published OpenDevelop $Version with its bundled Addin SDK."
}
catch {
    Write-Error "Release failed. Prepared artifacts (and any GitHub draft) were left intact: $artifactDir"
    throw
}
