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

function Find-VsMsBuild {
    # Locate Visual Studio's own MSBuild.exe via vswhere. Some AddIn projects cannot be built by
    # `dotnet build` at all - WinUIXamlDesigner.MicrosoftHost's UseWinUI pulls in
    # MrtCore.PriGen.targets, whose tasks (Microsoft.Build.Packaging.Pri.Tasks.dll) ship only with
    # Visual Studio (MSB4062 otherwise). Windows-only; do not call this from macOS packaging.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "cannot find vswhere.exe at $vswhere - is Visual Studio installed?"
    }
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' |
        Select-Object -First 1
    if (-not $msbuild -or -not (Test-Path $msbuild)) {
        throw "vswhere could not locate MSBuild.exe - is the 'MSBuild component' installed with Visual Studio?"
    }
    return $msbuild
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
    #
    # Streams live exactly like the original plain "& $exe @rest" - a Start-Process-based rewrite
    # tried here first was reverted because it hung a `dotnet build -m:1` invocation outright: an
    # MSBuild.exe worker node it spawned sat at 0% CPU for 5+ minutes with no progress, apparently
    # because Start-Process's different process-creation flags (new process group, no inherited
    # console) broke MSBuild's node-reuse pipe/console handshake. Piping through "&" itself, which
    # is how this always worked, does not have that problem.
    #
    # STDOUT is additionally teed to a log file so a failure's actual error text survives long after
    # it has scrolled out of the visible console history - the reason this exists at all is a build
    # that failed with nothing but "'dotnet build ...' exited with code 1" because the real MSBuild
    # error had already scrolled away by the time anyone looked. STDERR is deliberately left
    # un-teed (no "2>&1"): every caller sets $ErrorActionPreference = 'Stop' (dist.ps1:45), and
    # merging a native command's stderr into the PowerShell pipeline under that preference converts
    # each stderr LINE into a terminating error the moment it's read - dotnet/MSBuild routinely
    # write informational text to stderr, so that combination can abort the script on ordinary
    # output, not only a real failure. Leaving stderr unredirected means it still reaches the real
    # console exactly as before; nothing meaningful is lost from the log because MSBuild/dotnet's
    # default console logger writes its diagnostics, errors included, to stdout.
    $exe = $args[0]
    $rest = @()
    if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }

    $logPath = Join-Path ([System.IO.Path]::GetTempPath()) ("opendevelop-native-" + [System.Guid]::NewGuid().ToString('N') + ".log")
    # Write-Host, not a bare pipeline and not Out-Host. A bare pipeline leaves Tee-Object's
    # pass-through in the SUCCESS stream, where it becomes part of the calling function's return
    # value (a function ending in "return $payloadRoot" then hands back the whole build transcript
    # plus the path). Out-Host fixes that but writes straight to the console, so "dist.ps1 *> log"
    # captures nothing. Write-Host goes to the information stream: still shown live, still captured
    # by a redirect, and never part of the success stream.
    & $exe @rest | Tee-Object -FilePath $logPath | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Write-Host ''
        Write-Host "dist: '$exe $($rest -join ' ')' failed (exit $exitCode)." -ForegroundColor Red
        $errorLines = if (Test-Path $logPath) { Select-String -Path $logPath -Pattern 'error' -SimpleMatch -CaseSensitive:$false -ErrorAction SilentlyContinue } else { $null }
        if ($errorLines) {
            Write-Host 'Error lines from the captured stdout:' -ForegroundColor Red
            $errorLines | Select-Object -First 60 | ForEach-Object { Write-Host "  $($_.Line.Trim())" -ForegroundColor Red }
        } else {
            Write-Host "No line containing 'error' was found in the captured stdout (the failure may be on stderr, printed above)." -ForegroundColor Yellow
        }
        Write-Host "Full stdout kept at: $logPath" -ForegroundColor Yellow
        throw "'$exe $($rest -join ' ')' exited with code $exitCode. Full stdout: $logPath"
    }

    Remove-Item -Force $logPath -ErrorAction SilentlyContinue
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
    #
    # ExtraProperties matters for RID consistency: once a project's obj/project.assets.json is
    # restored for an EXPLICIT -p:RuntimeIdentifier, a later --no-restore build/publish for a
    # DIFFERENT (or absent) RuntimeIdentifier fails with NETSDK1047 ("doesn't have a target for
    # ...") because that RID's target section was never written. Callers doing a multi-RID
    # matrix build (dist.ps1's -RuntimeIdentifiers) must pass the SAME -p:RuntimeIdentifier here
    # that the matching Build-Solution/publish call for that pass will use, so restore always
    # produces (or refreshes) the exact target the following --no-restore step expects.
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$Solution,
        [string[]]$ExtraProperties = @()
    )
    Write-Host '==> Restoring packages and refreshing package content hashes...'
    Invoke-Native $DotNet restore $Solution --force-evaluate -v minimal @ExtraProperties
}

function Build-Solution {
    # Clear-RepoAddIns already wipes the shared AddIns/ tree before each build, so
    # incremental builds correctly rebuild only what changed. --no-incremental was
    # removed because it triggers NETSDK1047 on ARM64 hosts: LibreWPF.Sdk's Sdk.props
    # falls back to NETCoreSdkRuntimeIdentifier (win-arm64) when RuntimeIdentifier is
    # empty during the full re-evaluation, breaking cross-compilation. Passing
    # ProGpuWpfUseCurrentRuntimeIdentifier=false disables this fallback.
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$Solution,
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [string[]]$ExtraProperties = @()
    )
    Write-Host '==> Building OpenDevelop.Mvp.sln and all addins...'
    Invoke-Native $DotNet build $Solution -c $Configuration --no-restore '-m:1' -v minimal @ExtraProperties
}

function Get-PinnedGitVersionProperties {
    <#
      src/Main/GlobalAssemblyInfo.cs is regenerated by Directory.Build.targets'
      OpenDevelopGenerateGlobalAssemblyInfo target, which invokes the GitVersion.MsBuild task
      independently IN EVERY PROJECT THAT LINKS THE FILE (~60 of them), including inside the
      later solution-wide "Build-Solution" AddIns pass. GitVersion.MsBuild is not immune to
      producing a different CommitsSinceVersionSource between two separate top-level `dotnet`
      invocations a few seconds apart (observed: host published as revision 1, then the AddIns
      build silently rewrote the shared GlobalAssemblyInfo.cs to revision 2 partway through -
      WriteOnlyWhenDifferent still overwrites when the content DOES differ). Since the host DLL
      was already compiled and published against the OLD value, any AddIn compiled against the
      NEW value references a host assembly version that no longer exists on disk, throwing
      FileNotFoundException at runtime (e.g. GitAddIn's "ICSharpCode.SharpDevelop, Version=X"),
      which used to cascade into a hard crash showing the error dialog.
      Fix: read back the exact values already committed to disk, and pass them as explicit
      -p:GitVersion_* MSBuild global properties. Global properties set via the command line
      cannot be overridden by a property assignment inside the build (GitVersion.MsBuild's own
      output is silently ignored once these are set), so every one of the ~60 projects is
      guaranteed to compute byte-identical text and the shared file is never rewritten
      mid-pipeline.

      Used by dist.ps1 (pinning to what the host publish just wrote) and by build.ps1 (pinning a
      single targeted rebuild to whatever the rest of the tree was already built against).
    #>
    param([Parameter(Mandatory)][string]$GlobalAssemblyInfoPath)

    if (-not (Test-Path $GlobalAssemblyInfoPath)) {
        throw "cannot pin GitVersion - $GlobalAssemblyInfoPath does not exist (build the host once first)"
    }
    $content = Get-Content -LiteralPath $GlobalAssemblyInfoPath -Raw
    $extract = { param($name)
        $m = [regex]::Match($content, "public const string $name = `"([^`"]*)`"")
        if (-not $m.Success) { throw "cannot find RevisionClass.$name in $GlobalAssemblyInfoPath" }
        return $m.Groups[1].Value
    }
    $shaMatch = [regex]::Match($content, 'AssemblyInformationalVersion\(RevisionClass\.FullVersion \+ "\+([^"]*)"\)')
    if (-not $shaMatch.Success) { throw "cannot find the informational-version sha suffix in $GlobalAssemblyInfoPath" }

    return @(
        "-p:GitVersion_Major=$(& $extract 'Major')",
        "-p:GitVersion_Minor=$(& $extract 'Minor')",
        "-p:GitVersion_Patch=$(& $extract 'Build')",
        "-p:GitVersion_CommitsSinceVersionSource=$(& $extract 'Revision')",
        "-p:GitVersion_FullSemVer=$(& $extract 'FullVersion')",
        "-p:GitVersion_ShortSha=$($shaMatch.Groups[1].Value)"
    )
}

function Build-MicrosoftDesignerHosts {
    <#
      Builds the genuine-Microsoft-framework designer backends that are NOT referenced by
      OpenDevelop.Mvp.slnx and therefore never get built by Build-Solution:
        - FormsDesigner\MicrosoftHost\Host (Windows Forms against the .NET Desktop Runtime)
        - WinUIXamlDesigner.MicrosoftHost  (real WinUI 3, multi-targets net9.0/net10.0)

      Each one is its own DeployToAddIns target (AfterTargets="Build") that copies straight into
      the shared AddIns/ tree, mirroring how the solution-referenced AddIns deploy themselves - so
      building these two projects on the side, before the AddIns copy step, is sufficient; nothing
      else needs to know they exist.

      WinUIXamlDesigner.MicrosoftHost specifically CANNOT be built by `dotnet build`: UseWinUI
      pulls in MrtCore.PriGen.targets, whose tasks ship only with Visual Studio (MSB4062
      otherwise) - see "Building WinUIXamlDesigner.MicrosoftHost" in CLAUDE.md. Both projects are
      therefore built with Visual Studio's MSBuild.exe.

      The Forms host is an ordinary managed child launched via "dotnet exec", so it builds RID-less.
      The WinUI host is a real unpackaged WinUI 3 app and cannot: one platform-neutral distribution
      must therefore carry a child per supported Windows architecture, each deployed under
      MicrosoftHost\<CLR major>\<rid>\ and selected by MicrosoftWinUIDesignRuntimeHostBootstrap.

      Windows-only - every caller must guard with $IsWindows (or equivalent) before invoking this;
      it is what silently regressed in launch.ps1 (dotnet build ran fine for everything else, so
      the WinUI 3 backend never got built/deployed during a normal debug launch, and every WinUI
      document showed "WinUI 3 runtime host is not installed.").
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [string[]]$PinnedGitVersionProperties = @()
    )

    $msbuild = Find-VsMsBuild
    Write-Host "==> Using Visual Studio MSBuild for Microsoft designer hosts: $msbuild"

    $formsDesignerHost = Join-Path $RepoRoot 'src/AddIns/DisplayBindings/FormsDesigner/MicrosoftHost/Host/MicrosoftFormsDesigner.Host.csproj'
    Write-Host '==> Building Microsoft Windows Forms design host (RID-less)...'
    Invoke-Native $msbuild $formsDesignerHost '-restore' "-p:Configuration=$Configuration" '-p:DisableGitVersionTask=true' '-p:ProGpuWpfUseCurrentRuntimeIdentifier=false' '-v:m' @PinnedGitVersionProperties

    $winUiHost = Join-Path $RepoRoot 'src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.MicrosoftHost/WinUIXamlDesigner.MicrosoftHost.csproj'
    foreach ($rid in 'win-x64', 'win-arm64') {
        Write-Host "==> Building Microsoft WinUI design host (RuntimeIdentifier=$rid)..."
        $winUiArgs = @('-restore', "-p:Configuration=$Configuration", '-p:DisableGitVersionTask=true', "-p:RuntimeIdentifier=$rid", '-v:m') + $PinnedGitVersionProperties
        Invoke-Native $msbuild $winUiHost @winUiArgs
    }
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
    'Find-VsMsBuild',
    'Invoke-Native',
    'Resolve-Symlink',
    'Set-DotNetEnv',
    'Clear-RepoAddIns',
    'Restore-Solution',
    'Build-Solution',
    'Build-MicrosoftDesignerHosts',
    'Get-PinnedGitVersionProperties',
    'Remove-StaleMsBuildAssets'
)
