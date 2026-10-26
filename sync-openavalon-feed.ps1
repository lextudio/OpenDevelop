[CmdletBinding()]
param(
    [string]$FeedPath = (Join-Path $PSScriptRoot 'artifacts/openavalon-feed'),
    [switch]$Force,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'

# This is the exact closure OpenAvalon publishes to lextudio GitHub Packages. Keep it explicit:
# packages that remain publicly available from nuget.org must not accidentally become a local-feed
# dependency just because their names also start with ProGPU.
$openAvalonPackages = @(
    'LibreWPF.Interop', 'LibreWPF.ProGPU', 'LibreWPF.Sdk', 'LibreWPF.Transport',
    'LibreWinForms.ProGPU', 'LibreWinForms.Sdk', 'LibreWinForms.System.Windows.Forms', 'LibreWinForms.WindowsFormsIntegration',
    'ProGPU.Avalonia', 'ProGPU.Backend', 'ProGPU.Backend.Dawn', 'ProGPU.Backend.Dx12', 'ProGPU.Backend.Native', 'ProGPU.Native.Sdk', 'ProGPU.Compute',
    'ProGPU.DirectX', 'ProGPU.Layout', 'ProGPU.Media', 'ProGPU.Media.Scene', 'ProGPU.Scene', 'ProGPU.SkiaSharp',
    'ProGPU.System.Drawing.Common', 'ProGPU.Text', 'ProGPU.Text.Shaping', 'ProGPU.Transpiler', 'ProGPU.Vector',
    'ProGPU.Virtualization', 'ProGPU.WinRT', 'ProGPU.WinUI'
)

function Get-XmlPropertyValue([string]$Path, [string]$PropertyName) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $node = $xml.SelectSingleNode("//*[local-name()='$PropertyName']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Could not find a non-empty $PropertyName property in $Path."
    }
    return $node.InnerText.Trim()
}

# The version a package is pinned to in Directory.Packages.props: a literal Version="x" or a $(Property)
# that Get-XmlPropertyValue can resolve.
function Get-CentralPackageVersion([string]$Path, [string]$PackageId) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $node = $xml.SelectSingleNode("//*[local-name()='PackageVersion'][@Include='$PackageId']")
    $version = if ($null -ne $node) { $node.GetAttribute('Version').Trim() } else { '' }
    if ($version -match '^\$\((?<name>[^)]+)\)$') { $version = Get-XmlPropertyValue $Path $Matches['name'] }
    if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not find a Version for $PackageId in $Path." }
    return $version
}

function Test-NuGetPackage([string]$Path, [string]$PackageId, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $nuspec = $archive.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
            if ($null -eq $nuspec) { return $false }
            $reader = [IO.StreamReader]::new($nuspec.Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $actualId = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='id']").InnerText.Trim()
            $actualVersion = $manifest.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='version']").InnerText.Trim()
            return $actualId -eq $PackageId -and $actualVersion -eq $Version
        } finally { $archive.Dispose() }
    } catch { return $false }
}

function Test-VerifiedNuGetPackage([string]$Path, [string]$PackageId, [string]$Version) {
    $receipt = "$Path.verified"
    return (Test-NuGetPackage $Path $PackageId $Version) -and
        (Test-Path -LiteralPath $receipt -PathType Leaf) -and
        ((Get-Content -LiteralPath $receipt -Raw).Trim() -eq "$PackageId`n$Version")
}

$globalJson = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.'msbuild-sdks'.'LibreWPF.Sdk'
if ([string]::IsNullOrWhiteSpace($sdkVersion)) { throw 'global.json does not pin LibreWPF.Sdk.' }

$libreWinFormsVersion = Get-XmlPropertyValue (Join-Path $PSScriptRoot 'Directory.Build.props') 'ProGpuWpfLibreWinFormsPackageVersion'
$proGpuVersion = Get-XmlPropertyValue (Join-Path $PSScriptRoot 'Directory.Build.props') 'ProGpuPackageVersion'
if ($sdkVersion -ne $libreWinFormsVersion -or $sdkVersion -ne $proGpuVersion) {
    throw "The OpenAvalon package closure requires one version, but this checkout requests LibreWPF.Sdk $sdkVersion, LibreWinForms $libreWinFormsVersion, and ProGPU $proGpuVersion. Update the package pins or extend this script with an explicit multi-version closure."
}
$libGhosttyVersion = Get-XmlPropertyValue (Join-Path $PSScriptRoot 'Directory.Packages.props') 'LibGhosttyNetVersion'
# The AvalonEdit and AvalonDock forks, which used to be submodules here, publish to the same GitHub
# Packages endpoint from their own workflows; their versions are the central pins.
$forkPackages = @(
    'ICSharpCode.AvalonEdit.LibreWPF',
    'Dirkster.AvalonDock.Core', 'Dirkster.AvalonDock.LibreWPF', 'Dirkster.AvalonDock.Serializer.Xml',
    # Themes.VS2013 depends on the VS theme package: synchronize the complete restore closure.
    'Dirkster.AvalonDock.Themes.VS.LibreWPF', 'Dirkster.AvalonDock.Themes.VS2013.LibreWPF'
) | ForEach-Object { [pscustomobject]@{ Id = $_; Version = Get-CentralPackageVersion (Join-Path $PSScriptRoot 'Directory.Packages.props') $_ } }
$packages = @($openAvalonPackages | ForEach-Object { [pscustomobject]@{ Id = $_; Version = $sdkVersion } }) +
    [pscustomobject]@{ Id = 'LibGhostty.Net'; Version = $libGhosttyVersion } +
    $forkPackages

Write-Host "OpenAvalon local feed: $FeedPath"
Write-Host "OpenAvalon package version: $sdkVersion ($($openAvalonPackages.Count) packages); LibGhostty.Net $libGhosttyVersion; $($forkPackages.Count) AvalonEdit/AvalonDock fork packages"
if ($Plan) {
    $packages | ForEach-Object { Write-Host "  $($_.Id) $($_.Version)" }
    exit 0
}

if (-not $Force -and (@($packages | Where-Object {
    -not (Test-VerifiedNuGetPackage (Join-Path $FeedPath "$($_.Id).$($_.Version).nupkg") $_.Id $_.Version)
}).Count -eq 0)) {
    Write-Host 'Local package feed is already complete.'
    # This script is dot-sourced by build.ps1/dist.ps1.  `exit` here would terminate the
    # invoking build instead of merely skipping an unnecessary authenticated download.
    return
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) {
    throw 'GitHub CLI (gh) is required. Install it, run gh auth login, then grant package read access with: gh auth refresh -h github.com -s read:packages'
}

# Keep the credential entirely in this process: it is never placed in NuGet.config, an environment
# variable, a command argument, or stdout. GitHub Packages' NuGet endpoint uses HTTP Basic auth.
$token = $null
$client = $null
try {
    $token = (& $gh.Source auth token --hostname github.com 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'gh has no usable GitHub credential. Run gh auth login, then grant package read access with: gh auth refresh -h github.com -s read:packages'
    }
    $login = (& $gh.Source api user --jq '.login' 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($login)) {
        throw 'gh could not determine the authenticated GitHub user. Run gh auth login, then grant package read access with: gh auth refresh -h github.com -s read:packages'
    }

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $credentialBytes = [Text.Encoding]::UTF8.GetBytes("${login}:$token")
    $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Basic', [Convert]::ToBase64String($credentialBytes))
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('OpenDevelop-openavalon-feed-sync')

    $index = $client.GetStringAsync('https://nuget.pkg.github.com/lextudio/index.json').GetAwaiter().GetResult() | ConvertFrom-Json
    $flatContainer = @($index.resources | Where-Object { $_.'@type' -like 'PackageBaseAddress/*' } | Select-Object -First 1).'@id'
    if ([string]::IsNullOrWhiteSpace($flatContainer)) {
        throw 'The lextudio GitHub Packages service index did not expose a NuGet flat-container endpoint.'
    }
    if (-not $flatContainer.EndsWith('/')) { $flatContainer += '/' }

    New-Item -ItemType Directory -Force -Path $FeedPath | Out-Null
    foreach ($package in $packages) {
        $packageId = [string]$package.Id
        $packageVersion = [string]$package.Version
        $id = $packageId.ToLowerInvariant()
        $version = $packageVersion.ToLowerInvariant()
        $fileName = "$packageId.$packageVersion.nupkg"
        $destination = Join-Path $FeedPath $fileName
        if (-not $Force -and (Test-VerifiedNuGetPackage $destination $packageId $packageVersion)) {
            Write-Host "Already present: $fileName"
            continue
        }

        $versionsUri = "$flatContainer$id/index.json"
        $publishedVersions = ($client.GetStringAsync($versionsUri).GetAwaiter().GetResult() | ConvertFrom-Json).versions
        if ($publishedVersions -notcontains $packageVersion) {
            throw "GitHub Packages does not contain $packageId $packageVersion. Available versions include: $(($publishedVersions | Select-Object -Last 5) -join ', '). Update this checkout to a published package closure before syncing."
        }
        $base = "$flatContainer$id/$version/$id.$version.nupkg"
        $temporary = "$destination.partial"
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        try {
            # GitHub Packages exposes a NuGet flat container but, unlike nuget.org, does not
            # publish its optional .sha512 sidecar. HTTPS authenticates the transfer; opening the
            # archive below verifies that the downloaded bytes form a complete NuGet ZIP. Stream
            # large native packages to disk and retry transient network failures.
            $downloaded = $false
            for ($attempt = 1; $attempt -le 3 -and -not $downloaded; $attempt++) {
                try {
                    $response = $client.GetAsync($base, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                    try {
                        $response.EnsureSuccessStatusCode() | Out-Null
                        $source = $response.Content.ReadAsStream()
                        $target = [IO.File]::Open($temporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
                        $downloaded = $true
                    } finally { $response.Dispose() }
                } catch {
                    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                    if ($attempt -eq 3) { throw }
                    Start-Sleep -Seconds $attempt
                }
            }
            if (-not (Test-NuGetPackage $temporary $packageId $packageVersion)) { throw "Downloaded file is not the expected NuGet package: $fileName." }
            Move-Item -LiteralPath $temporary -Destination $destination -Force
            [IO.File]::WriteAllText("$destination.verified", "$packageId`n$packageVersion`n", [Text.UTF8Encoding]::new($false))
            Write-Host "Synced: $fileName"
        } finally {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
} catch {
    $detail = $_.Exception.Message
    if ($detail -match '401|403|Unauthorized|Forbidden') {
        throw "Could not read lextudio GitHub Packages. Grant gh package access with: gh auth refresh -h github.com -s read:packages"
    }
    throw
} finally {
    if ($null -ne $client) { $client.Dispose() }
    $token = $null
}
