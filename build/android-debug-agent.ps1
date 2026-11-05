param(
    [string] $DotNet = 'dotnet',
    [string] $AndroidSdkDirectory = $env:ANDROID_HOME,
    [string] $AndroidNdkDirectory = '',
    [string[]] $RuntimeIdentifiers = @('android-arm64', 'android-x64', 'android-arm')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'externals/sharpdbg'
$versions = & $DotNet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'Could not list .NET SDKs.' }
$version = @($versions | ForEach-Object { if ($_ -match '^(11\.[^ ]+)\s') { $Matches[1] } } | Sort-Object { [System.Management.Automation.SemanticVersion]::Parse($_) } -Descending) | Select-Object -First 1
if (-not $version) { throw 'Building the Android debugger requires a .NET 11 SDK with the Android workload.' }
$sdkLine = @($versions | Where-Object { $_.StartsWith("$version ") }) | Select-Object -First 1
if ($sdkLine -notmatch '\[(.+)\]') { throw 'Could not determine the .NET SDK root.' }
$dotnetRoot = Split-Path -Parent $Matches[1]
if (-not $AndroidSdkDirectory) { $AndroidSdkDirectory = $env:ANDROID_SDK_ROOT }
if (-not $AndroidSdkDirectory) {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ($IsMacOS) { $AndroidSdkDirectory = Join-Path $userProfile 'Library/Android/sdk' }
    elseif ($IsWindows) { $AndroidSdkDirectory = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    else { $AndroidSdkDirectory = Join-Path $userProfile 'Android/Sdk' }
}
if (-not (Test-Path -LiteralPath $AndroidSdkDirectory -PathType Container)) { throw "Android SDK not found: $AndroidSdkDirectory" }
if (-not $AndroidNdkDirectory) {
    $ndks = Join-Path $AndroidSdkDirectory 'ndk'
    if (Test-Path -LiteralPath $ndks) {
        $AndroidNdkDirectory = (Get-ChildItem -LiteralPath $ndks -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    }
}
if (-not $AndroidNdkDirectory -or -not (Test-Path -LiteralPath $AndroidNdkDirectory -PathType Container)) { throw 'An Android NDK is required.' }

$abis = @{ 'android-arm64' = 'arm64-v8a'; 'android-x64' = 'x86_64'; 'android-arm' = 'armeabi-v7a' }
foreach ($rid in $RuntimeIdentifiers) { if (-not $abis.ContainsKey($rid)) { throw "Unsupported Android RID: $rid" } }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('opendevelop-android-agent-' + [Guid]::NewGuid().ToString('N'))
$originalDirectory = Get-Location
try {
    New-Item -ItemType Directory -Path (Join-Path $scratch 'src') -Force | Out-Null
    foreach ($project in @('SharpDbg.AndroidAgent', 'SharpDbg.Application', 'SharpDbg.Infrastructure')) {
        $projectRoot = Join-Path (Join-Path $source 'src') $project
        $destination = Join-Path (Join-Path $scratch 'src') $project
        Get-ChildItem -LiteralPath $projectRoot -File -Recurse | Where-Object {
            $_.FullName -notmatch '[/\\](bin|obj)[/\\]'
        } | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
            $target = Join-Path $destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }
    Copy-Item -LiteralPath (Join-Path $source 'Directory.Build.props') -Destination $scratch
    # The OmniSharp DAP fork comes from the checkout's local feed (sync-openavalon-feed.ps1 downloads it with the
    # gh CLI's credential), never from GitHub Packages directly: no token reaches this build.
    $dapFeed = Join-Path $root 'artifacts/openavalon-feed'
    $dapVersionNode = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw)).SelectSingleNode("//*[local-name()='LextudioDapForkVersion']")
    if ($null -eq $dapVersionNode -or [string]::IsNullOrWhiteSpace($dapVersionNode.InnerText)) { throw 'Directory.Packages.props does not define LextudioDapForkVersion.' }
    $dapVersion = $dapVersionNode.InnerText.Trim()
    $dapFeedXml = [System.Security.SecurityElement]::Escape($dapFeed)
    foreach ($id in 'OmniSharp.Extensions.DebugAdapter', 'OmniSharp.Extensions.DebugAdapter.Server', 'OmniSharp.Extensions.DebugAdapter.Shared', 'OmniSharp.Extensions.JsonRpc', 'OmniSharp.Extensions.JsonRpc.Generators') {
        if (-not (Test-Path -LiteralPath (Join-Path $dapFeed "$id.$dapVersion.nupkg") -PathType Leaf)) { throw "$id $dapVersion is not in $dapFeed. Run ./sync-openavalon-feed.ps1 first." }
    }
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
    <add key="dap-local" value="$dapFeedXml" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="dap-local"><package pattern="OmniSharp.Extensions.*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'nuget.config') -Encoding utf8
    @{ sdk = @{ version = $version; allowPrerelease = $true; rollForward = 'disable' } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'global.json') -Encoding utf8
    Set-Location $scratch
    $projectFile = Join-Path $scratch 'src/SharpDbg.AndroidAgent/SharpDbg.AndroidAgent.csproj'
    foreach ($rid in $RuntimeIdentifiers) {
        Write-Host "Publishing Android debugger for $rid..."
        & $DotNet publish $projectFile -c Release -r $rid '-p:UseArtifactsOutput=false' "-p:LextudioDapForkVersion=$dapVersion" "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:AndroidNdkDirectory=$AndroidNdkDirectory"
        if ($LASTEXITCODE -ne 0) { throw "Android debugger publish failed for $rid." }
        $library = Join-Path $scratch "src/SharpDbg.AndroidAgent/bin/Release/net11.0-android/$rid/native/libSharpDbg.AndroidAgent.so"
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw "Publish produced no Android debugger library: $library" }
        Copy-Item -LiteralPath (Join-Path $scratch 'src/SharpDbg.AndroidAgent/obj/project.assets.json') -Destination (Join-Path $scratch "$rid.assets.json")
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $scratch "src/SharpDbg.AndroidAgent/bin/Release/net11.0-android/$rid/SharpDbg.AndroidAgent.runtimeconfig.json") -Raw | ConvertFrom-Json
        $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        $properties = & $DotNet msbuild $projectFile '-getProperty:_AndroidRuntimePackId,AndroidNETSdkVersion' "-p:RuntimeIdentifier=$rid" '-p:Configuration=Release' "-p:AndroidSdkDirectory=$AndroidSdkDirectory" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $runtimeVersion -or -not $properties.Properties._AndroidRuntimePackId -or -not $properties.Properties.AndroidNETSdkVersion) { throw "Could not determine runtime pack versions for $rid." }
        @{ RuntimeVersion = $runtimeVersion; AndroidPackId = $properties.Properties._AndroidRuntimePackId; AndroidVersion = $properties.Properties.AndroidNETSdkVersion } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch "$rid.runtime.json") -Encoding utf8
    }
    foreach ($rid in $RuntimeIdentifiers) {
        $destination = Join-Path (Join-Path $root 'artifacts/android-debug-agent') $abis[$rid]
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $scratch "src/SharpDbg.AndroidAgent/bin/Release/net11.0-android/$rid/native/libSharpDbg.AndroidAgent.so") -Destination $destination -Force
        # Distribution's SPDX collector scans src/AddIns for project.assets.json; retain the agent's
        # restore graph for each ABI there so embedded dependencies receive the same notice treatment.
        $assetsDirectory = Join-Path $root "src/AddIns/Debugger/AndroidDebugAgent/obj/$rid"
        New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $scratch "$rid.assets.json") -Destination (Join-Path $assetsDirectory 'project.assets.json') -Force
        $runtime = Get-Content -LiteralPath (Join-Path $scratch "$rid.runtime.json") -Raw | ConvertFrom-Json
        $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages' }
        $runtimeId = "Microsoft.NETCore.App.Runtime.NativeAOT.$rid"
        $runtimeDirectory = Join-Path (Join-Path $packageRoot $runtimeId.ToLowerInvariant()) $runtime.RuntimeVersion.ToLowerInvariant()
        $androidId = "Microsoft.Android.Runtime.NativeAOT.$($runtime.AndroidPackId).$rid"
        $androidDirectory = Join-Path (Join-Path $dotnetRoot "packs/$androidId") $runtime.AndroidVersion
        foreach ($pack in @(@{ Id = $runtimeId; Version = $runtime.RuntimeVersion; Directory = $runtimeDirectory; Name = 'dotnet' }, @{ Id = $androidId; Version = $runtime.AndroidVersion; Directory = $androidDirectory; Name = 'android' })) {
            $noticeDirectory = Join-Path $destination "licenses/$($pack.Name)"
            New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
            foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
                $noticeSource = Join-Path $pack.Directory $notice
                if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) { throw "Missing $notice in runtime pack $($pack.Id)." }
                Copy-Item -LiteralPath $noticeSource -Destination $noticeDirectory -Force
            }
            $metadata = Join-Path (Join-Path (Join-Path $root 'artifacts/android-debug-agent-package-metadata') $pack.Id.ToLowerInvariant()) $pack.Version.ToLowerInvariant()
            New-Item -ItemType Directory -Path $metadata -Force | Out-Null
            $nuspec = Get-ChildItem -LiteralPath $pack.Directory -Filter '*.nuspec' -File | Select-Object -First 1
            if (-not $nuspec) { throw "Missing NuGet metadata for runtime pack $($pack.Id)." }
            Copy-Item -LiteralPath $nuspec.FullName -Destination $metadata -Force
            Copy-Item -LiteralPath (Join-Path $pack.Directory 'LICENSE.TXT') -Destination $metadata -Force
            $runtimeAssets = @{ targets = @{ native = @{ "$($pack.Id)/$($pack.Version)" = @{ type = 'package' } } } }
            $runtimeAssetsDirectory = Join-Path $assetsDirectory "runtime-$($pack.Name)"
            New-Item -ItemType Directory -Path $runtimeAssetsDirectory -Force | Out-Null
            $runtimeAssets | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeAssetsDirectory 'project.assets.json') -Encoding utf8
        }
    }
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages' }
    $metadataRoot = Join-Path $root 'artifacts/android-debug-agent-package-metadata'
    foreach ($rid in $RuntimeIdentifiers) {
        $assets = Get-Content -LiteralPath (Join-Path $scratch "$rid.assets.json") -Raw | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $parts = $library.Name -split '/', 2
            $packageDirectory = Join-Path (Join-Path $packageRoot $parts[0].ToLowerInvariant()) $parts[1].ToLowerInvariant()
            $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $nuspec) { throw "Missing NuGet metadata for $($library.Name)." }
            $destination = Join-Path (Join-Path $metadataRoot $parts[0].ToLowerInvariant()) $parts[1].ToLowerInvariant()
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force
            [xml]$package = Get-Content -LiteralPath $nuspec.FullName -Raw
            $license = $package.SelectSingleNode("//*[local-name()='license']")
            if ($license -and $license.type -eq 'file') {
                $licenseSource = Join-Path $packageDirectory $license.InnerText.Trim()
                if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) { throw "Missing license file for $($library.Name)." }
                $licenseTarget = Join-Path $destination $license.InnerText.Trim()
                New-Item -ItemType Directory -Path (Split-Path -Parent $licenseTarget) -Force | Out-Null
                Copy-Item -LiteralPath $licenseSource -Destination $licenseTarget -Force
            }
        }
    }
    if (@($RuntimeIdentifiers | Select-Object -Unique).Count -eq $abis.Count) {
        & (Join-Path $PSScriptRoot 'verify-android-debug-agent.ps1')
    }
} finally {
    Set-Location $originalDirectory
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
