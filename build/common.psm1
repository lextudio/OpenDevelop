# OpenDevelop build module — shared by launch.ps1 / dist.ps1 (and usable from
# a future Windows wrapper, e.g. launch.cmd). Ported from dotnet-env.sh + the inline
# logic that used to live in launch.sh / dist.macos.sh; keep behavior in sync with
# those thin shell wrappers' expectations.
#
# Conventions:
#  - Every native command goes through Invoke-Native so a non-zero exit aborts the
#    script the way `set -e` did in the old bash scripts.
#  - All repo-relative paths are derived by callers and passed in explicitly.

Set-StrictMode -Version Latest

function Find-DotNetHost {
    # Locate the dotnet host the same way launch.sh used to: well-known install dirs
    # first (Homebrew arm64/intel), then PATH fallback. Works on macOS and Windows.
    $candidates = @()
    if ($IsWindows) {
        $candidates += @(
            (Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'),
            (Join-Path $env:LocalAppData 'Microsoft/dotnet/dotnet.exe')
        )
    }
    else {
        $candidates += @(
            '/opt/homebrew/bin/dotnet',
            '/usr/local/share/dotnet/dotnet'
        )
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            return (Resolve-Path $c).Path
        }
    }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw "cannot find dotnet (checked well-known locations and PATH)"
}

function Resolve-Symlink {
    # readlink -f equivalent: follow symlink chains to the final target.
    param([Parameter(Mandatory)][string]$Path)
    $current = $Path
    for ($i = 0; $i -lt 16; $i++) {
        $item = Get-Item $current
        if (-not $item.LinkTarget) { return $item.FullName }
        $t = $item.LinkTarget
        if (-not [System.IO.Path]::IsPathRooted($t)) {
            $t = Join-Path (Split-Path -Parent $current) $t
        }
        $current = $t
    }
    throw "symlink chain too deep: $Path"
}

function Invoke-Native {
    # Run a native command; throw on non-zero exit (set -e semantics).
    # Usage: Invoke-Native <exe> <args...>
    $exe = $args[0]
    $rest = @()
    if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }
    & $exe @rest
    if ($LASTEXITCODE -ne 0) {
        throw "'$exe $($rest -join ' ') ' exited with code $LASTEXITCODE"
    }
}

function Set-DotNetEnv {
    # Port of dotnet-env.sh's setup_dotnet_env: export the MSBuild env vars that
    # SharpDevelop's IN-PROCESS MSBuild hosting needs. These must NOT leak into
    # `dotnet build`/`dotnet restore` invocations (which honor global.json on their
    # own) - only set them right before launching the app itself.
    param([Parameter(Mandatory)][string]$DotNetHost)

    if (-not (Test-Path $DotNetHost)) {
        throw "dotnet host '$DotNetHost' not found"
    }
    $dotnet = Resolve-Symlink $DotNetHost
    $binDir = Split-Path -Parent $dotnet

    $env:PATH = "$binDir$([System.IO.Path]::PathSeparator)$env:PATH"

    # Homebrew's dotnet is a bin/dotnet symlink whose real SDK/runtime tree lives in a
    # sibling libexec/ dir; bundled layouts have sdk/ directly under the binary's dir.
    if (Test-Path (Join-Path $binDir 'sdk')) {
        $env:DOTNET_ROOT = $binDir
    }
    elseif (Test-Path (Join-Path (Split-Path -Parent $binDir) 'libexec/sdk')) {
        $env:DOTNET_ROOT = Join-Path (Split-Path -Parent $binDir) 'libexec'
    }
    else {
        throw "cannot locate an 'sdk' dir for host '$dotnet'"
    }

    $env:DOTNET_HOST_PATH = $dotnet

    # Resolve the SDK version exactly like `dotnet build` will (honors global.json).
    # Picking the lexicographically-highest installed SDK is wrong when a newer preview
    # SDK coexists with the pinned one - MSBuildSDKsPath must match the resolving SDK,
    # or Sdk="LibreWPF.Sdk" resolution breaks ("... is not a valid project file").
    $version = (& $dotnet --version).Trim()
    $sdkDir = Join-Path $env:DOTNET_ROOT "sdk/$version"
    if (-not (Test-Path $sdkDir)) {
        throw "resolved SDK version '$version' has no directory under $env:DOTNET_ROOT/sdk"
    }

    $env:MSBuildSDKsPath = Join-Path $sdkDir 'Sdks'
    $env:MSBuildExtensionsPath = $sdkDir
    $env:MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET = Join-Path $sdkDir 'SdkResolvers'
    $env:MSBUILD_NUGET_PATH = $sdkDir
    # In-process MSBuild hosting does not need workload resolution for these projects;
    # disabling it avoids SDK resolver noise from optional workload manifests.
    $env:MSBuildEnableWorkloadResolver = 'false'
}

function Clear-RepoAddIns {
    # Several AddIn projects (UnitTesting, Debugger.AddIn, ...) build directly INTO this
    # shared repo-root AddIns/<Category>/<Name> tree via their own <OutputPath> (an
    # old-style SharpDevelop convention, not a per-project bin folder), and
    # SharpDevelop.csproj's DeployAddInsToRepoRoot target copies the two top-level
    # *.addin files here too. A normal incremental build only adds/updates files - it
    # never removes ones an addin project stopped producing (a renamed .addin fragment,
    # a deleted helper .dll, a dropped satellite-resource culture folder) - so this
    # directory silently accumulates leftovers from earlier revisions of whatever addin
    # you're actively reworking, and AddInTree loads whatever it finds at startup,
    # indiscriminately. Wipe it before every full build so only what the CURRENT project
    # set actually produces is ever present.
    param([Parameter(Mandatory)][string]$RepoRoot)
    $addIns = Join-Path $RepoRoot 'AddIns'
    Write-Host '==> Clearing AddIns/ to drop stale output from previous builds...'
    if (Test-Path $addIns) { Remove-Item -Recurse -Force $addIns }
}

function Restore-Solution {
    # Some upstream projects use packages.lock.json. NuGet packages can be re-signed or
    # republished by a feed without changing their ID/version; in that case the lock
    # file's old content hash causes NU1403 even after the global package cache is
    # cleared. Re-evaluate lock files against the configured feeds, then keep actual
    # builds offline (--no-restore) so every project uses one consistent dependency graph.
    param([Parameter(Mandatory)][string]$DotNet, [Parameter(Mandatory)][string]$Solution)
    Write-Host '==> Restoring packages and refreshing package content hashes...'
    Invoke-Native $DotNet restore $Solution --force-evaluate -v minimal
}

function Build-Solution {
    # AddIn projects write directly to the shared AddIns/ tree. Since Clear-RepoAddIns
    # removed it, an incremental build is not sufficient: MSBuild may consider a project
    # up-to-date based on obj/ and skip recreating its shared output. --no-incremental
    # forces all projects to rebuild and republish every current addin file.
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$Solution,
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [string[]]$ExtraProperties = @()
    )
    Write-Host '==> Rebuilding OpenDevelop.Mvp.sln and all addins...'
    # Several projects share physical output directories (notably SharpTreeView and the
    # host/addin graph). Parallel project builds can clean or replace those files while a
    # sibling is copying them, producing intermittent MSB3030 failures after AddIns/ was
    # cleared. Serialize this full republish; individual project builds remain parallel.
    Invoke-Native $DotNet build $Solution -c $Configuration --no-restore --no-incremental '-m:1' -v minimal @ExtraProperties
}

function Remove-StaleMsBuildAssets {
    # Microsoft.Build.Runtime 18.0.2 copies MSBuild .targets/.props files to every
    # project's output directory via contentFiles/CopyToOutputDirectory=PreserveNewest.
    # These stale copies confuse SharpDevelop's in-process MSBuild evaluation, which can
    # load the wrong Microsoft.Common.CrossTargeting.targets and mis-resolve
    # $(MSBuildToolsPath) to the output directory instead of the SDK directory.
    # Remove them after build so only the SDK's own versions are visible.
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$Configuration = 'Debug'
    )
    Get-ChildItem (Join-Path $RepoRoot 'src') -Recurse -File -Include '*.targets', '*.props' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*bin/$Configuration/*" } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Export-ModuleMember -Function @(
    'Find-DotNetHost',
    'Invoke-Native',
    'Resolve-Symlink',
    'Set-DotNetEnv',
    'Clear-RepoAddIns',
    'Restore-Solution',
    'Build-Solution',
    'Remove-StaleMsBuildAssets'
)
