# Verifies that the LibGhostty.Net package restored for a native Terminal CI lane contains the
# runner's native payload AND the complete five-RID supported cross-platform closure. A same-version cache
# from nuget.org is not acceptable evidence: it can satisfy restore while lacking the fork's
# ARM64/Linux assets. Checking the whole closure on every build turns a missing publish asset into
# an immediate package-provenance failure rather than a later, platform-specific surprise.
param(
    [Parameter(Mandatory = $true)][ValidateSet('windows-x64', 'windows-arm64', 'macos-arm64', 'linux-x64', 'linux-arm64')][string]$Platform
)

$ErrorActionPreference = 'Stop'
$cache = if ($env:NUGET_CACHE_PATH) { $env:NUGET_CACHE_PATH } else { Join-Path $PSScriptRoot '../../artifacts/nuget-packages' }
$propsPath = Join-Path $PSScriptRoot '../../Directory.Packages.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = ([string]$props.Project.PropertyGroup.LibGhosttyNetVersion).Trim()
if (-not $version) { throw "LibGhosttyNetVersion is not defined in $propsPath" }
$root = Join-Path $cache "libghostty.net/$version"
$expectedSha512ByVersion = @{ '1.0.2' = 'L1fkxSxFgqWZ1yEuBz7P5vPbhJeKSoeIW71L6lyDO88tzw4ivQZE7A5vUWSj10chZRZQyGat8lbd8O/R8Iif/g==' }
if (-not $expectedSha512ByVersion.ContainsKey($version)) { throw "No approved LibGhostty.Net package hash is recorded for version $version." }
$expectedSha512 = $expectedSha512ByVersion[$version]
$shaPath = Join-Path $root "libghostty.net.$version.nupkg.sha512"
if (-not (Test-Path -LiteralPath $shaPath -PathType Leaf) -or (Get-Content -LiteralPath $shaPath -Raw).Trim() -ne $expectedSha512) {
    throw "LibGhostty.Net $version does not match the required lextudio GitHub Packages package provenance: $shaPath"
}
$expectedByPlatform = [ordered]@{
    'windows-x64' = @{ Rid = 'win-x64'; Files = @('OpenConsole.exe', 'conpty.dll', 'ghostty-vt.dll') }
    'windows-arm64' = @{ Rid = 'win-arm64'; Files = @('OpenConsole.exe', 'conpty.dll', 'ghostty-vt.dll') }
    'macos-arm64' = @{ Rid = 'osx-arm64'; Files = @('libghostty-vt.dylib', 'libmuxer-pty.dylib') }
    'linux-x64' = @{ Rid = 'linux-x64'; Files = @('libghostty-vt.so', 'libmuxer-pty.so') }
    'linux-arm64' = @{ Rid = 'linux-arm64'; Files = @('libghostty-vt.so', 'libmuxer-pty.so') }
}
$verified = @()
foreach ($expected in $expectedByPlatform.Values) {
    $rid = $expected.Rid
    $native = Join-Path $root "runtimes/$rid/native"
    if (-not (Test-Path -LiteralPath $native -PathType Container)) {
        throw "LibGhostty.Net $version is missing its ${rid} native directory: $native"
    }
    foreach ($name in $expected.Files) {
        $file = Join-Path $native $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -le 0) {
            throw "LibGhostty.Net $version is missing the required non-empty $rid native file: $file"
        }
    }
    $verified += $rid
}

Write-Host "Verified LibGhostty.Net $version complete native payload ($($verified -join ', ')); this $Platform lane will use $($expectedByPlatform[$Platform].Rid)."
