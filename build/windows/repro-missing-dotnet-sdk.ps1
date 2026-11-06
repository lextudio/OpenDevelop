#!/usr/bin/env pwsh
#
# Reproduces the v5.7.2 project-load crash reported in issue #21 (comment 6689151041):
#
#   ICSharpCode.SharpDevelop.Project.ProjectLoadException: The SDK 'Microsoft.NET.Sdk'
#   specified could not be found.  D:\...\new.csproj
#
# The dump's "Runtime: 10.0.14" line is Environment.Version from ExceptionBox.BuildDetailsText -
# the CLR the IDE itself runs on, not an SDK version. The shipped payload is framework-dependent
# (dist.ps1 publishes the host with --self-contained false), so a .NET 10 *runtime* is all it takes
# to start the IDE; opening any SDK-style project then needs a .NET SDK 10 or newer that
# DotNetSdkService can discover for this process's architecture. Without one,
# MSBuildInternals.InitializeMSBuildEnvironment never sets MSBuildSDKsPath and MSBuild reports
# exactly that message.
#
# So the crash needs a machine whose SDK is absent, older than 10, or of another architecture.
# This script builds that machine state on a throwaway Windows runner (it DELETES SDK folders),
# drives the published payload through the same DevFlow actions the packaged-app check uses, and
# fails unless the failing scenarios really produce that message. Scenarios, in this order:
#
#   control       the runner's own SDK 10 stays: proves the journey works on this machine, so a
#                 failure in the other two is attributable to the missing SDK, not to the harness
#   sdk9-only     only a 9.x SDK is left (the machine in issue #19 had 9.0.316 installed)
#   runtime-only  no SDK at all, only shared/ and templates/ (the reported machine)
#
# The app runs with OD_TEST_MODE=1 so the modal exception box is suppressed and logged instead of
# parking the UI thread (WpfMessageService.ShowException), which is what makes the failure
# observable from DevFlow at all. The packaged payload has no .config, so its logger writes
# %TEMP%\OpenDevelop.log with ImmediateFlush (log4netLoggingService.ConfigureDefaultLogging): that
# file, the Build pad's own ".NET SDK check" report and od.sdk.list are the evidence collected.
#
# Usage:
#   ./build/windows/repro-missing-dotnet-sdk.ps1 -PayloadRoot OpenDevelop-win
#   ./build/windows/repro-missing-dotnet-sdk.ps1 -PayloadRoot OpenDevelop-win -Scenario runtime-only
#
[CmdletBinding()]
param(
    # The extracted payload folder: the one holding OpenDevelop.dll and OpenDevelop.exe.
    [Parameter(Mandatory)][string]$PayloadRoot,
    # Machine states to reproduce. They only ever remove SDKs, so 'control' has to come first.
    [ValidateSet('control', 'sdk9-only', 'runtime-only')][string[]]$Scenario = @('control', 'sdk9-only', 'runtime-only'),
    [int]$Port = 9317,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$payload = (Resolve-Path -LiteralPath $PayloadRoot).Path
$baseUrl = "http://127.0.0.1:$Port"
$leaseId = [guid]::NewGuid().ToString()
# MSBuild's own InvalidProjectFileException text, wrapped in ProjectLoadException by
# MSBuildBasedProject.OpenConfiguration. This is what the reporter pasted.
$expectedFailure = "The SDK 'Microsoft.NET.Sdk' specified could not be found"
# Issue #21's packaging-caused failure, in both forms this app can produce: the stale manifest a
# payload shipped, and the resolver folder left behind when the manifest is removed (MSBuild's
# SdkResolverLoader rejects a folder holding neither a DLL nor a manifest). Neither may come back:
# with a missing SDK the report has to be about the SDK, not about this folder.
$resolverFailures = @(
    'Could not load SDK Resolver',
    'SDK Resolver folder exists but without an SDK Resolver DLL or manifest file'
)
$sdkCheckHeading = '.NET SDK 10 or newer: NOT FOUND'
$appLogPath = Join-Path ([System.IO.Path]::GetTempPath()) 'OpenDevelop.log'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'od-sdk-repro'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Fail([string]$message) {
    Write-Host "::error::$message"
    throw $message
}

function Read-LogFile([string[]]$paths) {
    $text = ''
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try { $text += (Get-Content -LiteralPath $path -Raw -ErrorAction Stop) } catch { }
        }
    }
    return $text
}

function Invoke-DevFlow([string]$action, [object[]]$Arguments = @()) {
    # The agent uses a short mutation lease, and claims it immediately before every request. Under
    # OD_TEST_MODE the lease is not required (App.xaml.cs), so a lease endpoint that is missing or
    # refuses this holder must not abort the run.
    $headers = @{}
    try {
        $leaseBody = @{ action = 'claim'; leaseId = $leaseId; holderKind = 'sdk-repro'; label = 'missing .NET SDK reproduction'; force = $true } | ConvertTo-Json -Compress
        $lease = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/agent/lease" -ContentType 'application/json' -Body $leaseBody -TimeoutSec 15
        if ($lease.allowed) { $headers['X-DevFlow-Lease'] = $leaseId }
    } catch { }
    $body = @{ args = $Arguments } | ConvertTo-Json -Depth 6 -Compress
    $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/invoke/actions/$action" -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 180
    if (-not $response.success) {
        Fail "$action returned an unsuccessful envelope: $($response | ConvertTo-Json -Depth 6 -Compress)"
    }
    if ($null -eq $response.returnValue) { return $null }
    return ($response.returnValue | ConvertFrom-Json)
}

# Polls an action until the predicate accepts its result or the attempts run out; returns the last
# result either way.
function Wait-DevFlow([string]$action, [object[]]$Arguments, [scriptblock]$Accept, [int]$Attempts = 30, [int]$DelaySeconds = 2) {
    $result = $null
    for ($i = 0; $i -lt $Attempts; $i++) {
        $result = Invoke-DevFlow $action $Arguments
        if (& $Accept $result) { return $result }
        Start-Sleep -Seconds $DelaySeconds
    }
    return $result
}

# --- the machine state -----------------------------------------------------

# Every root DotNetSdkService.DiscoverSdks() can see, filtered to folders that really hold a
# dotnet host and an sdk/ directory - the same two conditions DescribeRoot requires.
function Get-DotnetRoots {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:DOTNET_ROOT) { $candidates.Add($env:DOTNET_ROOT) }
    foreach ($entry in ($env:PATH -split ';')) {
        if ($entry) { $candidates.Add($entry) }
    }
    if ($env:USERPROFILE) { $candidates.Add((Join-Path $env:USERPROFILE '.dotnet')) }
    $programFilesDotnet = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet'
    $candidates.Add($programFilesDotnet)
    # The installer's side-by-side layout for another architecture (see DiscoverSdks's
    # systemCandidates); an ARM64 runner commonly carries x64 SDKs under Program Files\dotnet\x64.
    foreach ($architecture in 'x64', 'arm64', 'x86') {
        $candidates.Add((Join-Path $programFilesDotnet $architecture))
    }
    # actions/setup-dotnet installs <tool cache>\dotnet\<version>\<arch>. This workflow does not use
    # it, but a root an image step left behind must not survive the removal either.
    if ($env:RUNNER_TOOL_CACHE) {
        $cache = Join-Path $env:RUNNER_TOOL_CACHE 'dotnet'
        if (Test-Path -LiteralPath $cache -PathType Container) {
            foreach ($version in Get-ChildItem -LiteralPath $cache -Directory) {
                $candidates.Add($version.FullName)
                foreach ($architecture in Get-ChildItem -LiteralPath $version.FullName -Directory) {
                    $candidates.Add($architecture.FullName)
                }
            }
        }
    }

    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $full = $null
        try { $full = [System.IO.Path]::GetFullPath($candidate) } catch { continue }
        # PATH commonly holds the same root with a trailing separator ("C:\Program Files\dotnet\"),
        # which GetFullPath keeps, so without this the root is listed twice and removing its SDKs
        # once per copy fails the second time on a folder that is already gone.
        if ($full.Length -gt 3) { $full = $full.TrimEnd('\', '/') }
        if (-not (Test-Path -LiteralPath (Join-Path $full 'dotnet.exe') -PathType Leaf)) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $full 'sdk') -PathType Container)) { continue }
        if (-not ($roots -contains $full)) { $roots.Add($full) }
    }
    return $roots
}

function Get-SdkVersions([string]$root) {
    $sdkDirectory = Join-Path $root 'sdk'
    if (-not (Test-Path -LiteralPath $sdkDirectory -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $sdkDirectory -Directory | ForEach-Object { $_.Name })
}

function Write-SdkState([string]$label) {
    Write-Host "    [$label]"
    $roots = Get-DotnetRoots
    if ($roots.Count -eq 0) {
        Write-Host '      no dotnet root with an sdk folder is visible'
    }
    foreach ($root in $roots) {
        $versions = Get-SdkVersions $root
        $list = if ($versions.Count -eq 0) { '(empty sdk folder)' } else { ($versions | Sort-Object) -join ', ' }
        Write-Host "      $root -> $list"
    }
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($dotnet) {
        $listed = @(& $dotnet.Source --list-sdks 2>&1) -join ' | '
        if (-not $listed) { $listed = '(none)' }
        Write-Host "      dotnet --list-sdks: $listed"
    }
}

function Remove-AllSdks {
    foreach ($root in Get-DotnetRoots) {
        $sdkDirectory = Join-Path $root 'sdk'
        if (-not (Test-Path -LiteralPath $sdkDirectory -PathType Container)) { continue }
        Write-Host "    removing $sdkDirectory"
        Remove-Item -LiteralPath $sdkDirectory -Recurse -Force
    }
}

# The control run's build leaves MSBuild/VBCSCompiler servers alive that have SDK assemblies loaded
# from the folder we are about to delete; a running server would either keep a file locked or fail
# later for the wrong reason. `dotnet build-server shutdown` is the supported way to stop them.
function Stop-BuildServers {
    try {
        $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
        if ($dotnet) { & $dotnet.Source build-server shutdown 2>&1 | Out-Null }
    } catch { }
    $global:LASTEXITCODE = 0
}

# Leaves one 9.x SDK per root and drops every other version. A root with no 9.x keeps none, so it
# cannot quietly keep the SDK 10 the scenario is trying to remove. Returns false when the image has
# no 9.x SDK at all, which makes this scenario impossible here.
function LimitToSdk9 {
    $roots = Get-DotnetRoots
    $nineRoots = @($roots | Where-Object { @(Get-SdkVersions $_ | Where-Object { $_ -match '^9\.' }).Count -gt 0 })
    if ($nineRoots.Count -eq 0) { return $false }

    foreach ($root in $roots) {
        $versions = Get-SdkVersions $root
        $keep = $versions |
            Where-Object { $_ -match '^9\.' } |
            Sort-Object { [version]($_ -replace '-.*$', '') } |
            Select-Object -Last 1
        foreach ($version in $versions) {
            if ($version -eq $keep) { continue }
            Write-Host "    removing $(Join-Path (Join-Path $root 'sdk') $version)"
            Remove-Item -LiteralPath (Join-Path (Join-Path $root 'sdk') $version) -Recurse -Force
        }
    }
    return $true
}

# The manifest MSBuildInternals.PointNuGetSdkResolverManifestAtSdk writes at startup, naming this
# machine's SDK by absolute path. Issue #21 was a payload shipping a foreign one.
function Get-ResolverFolderPath {
    return [System.IO.Path]::Combine($payload, 'SdkResolvers', 'Microsoft.Build.NuGetSdkResolver')
}

function Get-ResolverManifestPath {
    return [System.IO.Path]::Combine((Get-ResolverFolderPath), 'Microsoft.Build.NuGetSdkResolver.xml')
}

function Get-ResolverManifestText {
    $path = Get-ResolverManifestPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return '(absent)' }
    try { return ((Get-Content -LiteralPath $path -Raw) -replace '\s+', ' ').Trim() } catch { return '(unreadable)' }
}

# --- the application -------------------------------------------------------

function Start-OpenDevelop([string]$configDirectory, [string]$outLog, [string]$errLog) {
    # The payload carries both launchers; run the one for this machine's architecture, as dist.ps1's
    # own smoke phase does.
    $launcherName = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'OpenDevelopARM64.exe' } else { 'OpenDevelop.exe' }
    $launcher = Join-Path $payload $launcherName
    $arguments = @("-devflow:$Port", "-configdir:$configDirectory")
    $startArgs = @{
        RedirectStandardOutput = $outLog
        RedirectStandardError  = $errLog
        PassThru               = $true
        WorkingDirectory       = $payload
    }
    if (Test-Path -LiteralPath $launcher -PathType Leaf) {
        $startArgs.FilePath = $launcher
        $startArgs.ArgumentList = $arguments
    } else {
        $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
        $startArgs.FilePath = if ($dotnet) { $dotnet.Source } else { 'dotnet' }
        $startArgs.ArgumentList = @('exec', (Join-Path $payload 'OpenDevelop.dll')) + $arguments
    }

    # OD_TEST_MODE turns the modal exception box into a log line (WpfMessageService.ShowException);
    # without it the New Solution dialog's continuation would sit inside ShowDialog's nested loop and
    # nothing after the crash could be observed. OPENDEVELOP_STARTUP_MARKER is the
    # workbench-rendered readiness signal dist.ps1's smoke phase uses.
    $previousTestMode = $env:OD_TEST_MODE
    $previousMarker = $env:OPENDEVELOP_STARTUP_MARKER
    $env:OD_TEST_MODE = '1'
    $env:OPENDEVELOP_STARTUP_MARKER = '1'
    try {
        return Start-Process @startArgs
    } finally {
        $env:OD_TEST_MODE = $previousTestMode
        $env:OPENDEVELOP_STARTUP_MARKER = $previousMarker
    }
}

# Returns $true when the workbench rendered, $false when the process left first. A payload that
# refuses to start without a .NET SDK does that on purpose (ProjectService
# .ReportMissingSdkAtStartup), so "exited" is a result to report, not a harness failure.
function Wait-Rendered($process, [string]$outLog, [string]$errLog) {
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        Start-Sleep -Seconds 1
        if ($process.HasExited) { return $false }
        # WorkbenchStartup emits this only after the main window has loaded and rendered; the
        # launcher forwards its "dotnet exec" child's output when it was itself started with
        # standard handles (as here), so the same marker covers the launcher path.
        if ((Read-LogFile @($outLog, $errLog)) -match 'startup/main-window-rendered') { return $true }
    }
    Fail 'the packaged application neither rendered its main window nor exited within 90 seconds.'
}

function Wait-Agent {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/agent/status" -TimeoutSec 2
            if ($status.running) { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    Fail "the DevFlow agent never answered on $baseUrl"
}

function Stop-OpenDevelop($process) {
    if (-not $process) { return }
    if (-not $process.HasExited) {
        # /T: the launcher waits on a child "dotnet exec OpenDevelop.dll" that owns the window and
        # the log file, so killing only the launcher would leave the app running.
        try { & taskkill /T /F /PID $process.Id | Out-Null } catch { }
        try { $null = $process.WaitForExit(15000) } catch { }
    }
    # taskkill exits non-zero for a child that has already gone; a run in which every scenario passed
    # would otherwise inherit that as the script's own exit code.
    $global:LASTEXITCODE = 0
}

# --- one scenario ----------------------------------------------------------

function Invoke-Scenario([string]$name) {
    $runDirectory = Join-Path $OutputDirectory $name
    Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $configDirectory = Join-Path $runDirectory 'config'
    $projectRoot = Join-Path $runDirectory 'projects'
    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    $outLog = Join-Path $runDirectory 'stdout.log'
    $errLog = Join-Path $runDirectory 'stderr.log'

    Write-Host ''
    Write-Host "==> $name"
    Write-SdkState 'before launch'
    $manifestBefore = Get-ResolverManifestText
    Write-Host "    NuGet SDK-resolver manifest before launch: $manifestBefore"

    Remove-Item -LiteralPath $appLogPath -Force -ErrorAction SilentlyContinue
    $process = Start-OpenDevelop $configDirectory $outLog $errLog
    $sdks = $null
    $padText = ''
    $loaded = $false
    $buildOk = $null
    $exited = $false
    try {
        $rendered = Wait-Rendered $process $outLog $errLog
        $exited = -not $rendered
        if ($exited) {
            # A payload that refuses to start without a .NET SDK: the report is in the log, and
            # there is no session to drive. Older payloads instead start and fail the first load.
            Write-Host '    the application left during startup'
        } else {
            Wait-Agent

            $sdks = Invoke-DevFlow 'od.sdk.list'
            ($sdks | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $runDirectory 'sdk-list.json') -Encoding utf8
            $padText = [string](Invoke-DevFlow 'od.output-text' @('Build')).text
            Set-Content -LiteralPath (Join-Path $runDirectory 'build-pad.txt') -Value $padText -Encoding utf8

            # The reporter's own journey: File > New > Solution, Console App, Create.
            $null = Invoke-DevFlow 'od.new-solution-dialog'
            $dialog = Wait-DevFlow 'od.new-template-dialog.status' @() { param($r) $r.open -and $r.kind -eq 'project' -and $r.templateCount -gt 0 } -Attempts 60 -DelaySeconds 1
            if (-not ($dialog.open -and $dialog.kind -eq 'project' -and $dialog.templateCount -gt 0)) {
                Fail "[$name] the New Solution dialog never listed a project template: $($dialog | ConvertTo-Json -Compress)"
            }
            $submitted = Invoke-DevFlow 'od.new-solution-dialog.create' @('Console App', 'ReproApp', $projectRoot, 'ReproSolution')
            if (-not $submitted.submitted) {
                Fail "[$name] the New Solution dialog did not accept the create request: $($submitted | ConvertTo-Json -Compress)"
            }

            $projectFile = Join-Path (Join-Path (Join-Path $projectRoot 'ReproSolution') 'ReproApp') 'ReproApp.csproj'
            for ($attempt = 0; $attempt -lt 60; $attempt++) {
                Start-Sleep -Seconds 1
                $tree = Invoke-DevFlow 'od.solution-tree'
                if (@($tree.projects | Where-Object { $_.fileName -eq $projectFile }).Count -gt 0) {
                    $loaded = $true
                    break
                }
                # Stop as soon as the failure is on disk: waiting out the full minute proves nothing more.
                if ((Read-LogFile @($appLogPath, $outLog, $errLog)) -match [regex]::Escape($expectedFailure)) { break }
            }
            if (-not $loaded) {
                Write-Host '    the created project never reached the live solution model'
            }

            # Only the control run has an SDK to build with; it is what proves the harness itself works.
            if ($loaded -and $name -eq 'control') {
                $build = Invoke-DevFlow 'od.build-solution' @('ReproApp')
                $buildOk = [bool]($build.success -and $build.result -eq 'Success')
                ($build | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $runDirectory 'build.json') -Encoding utf8
            }
        }
    } finally {
        Stop-OpenDevelop $process
        Copy-Item -LiteralPath $appLogPath -Destination (Join-Path $runDirectory 'OpenDevelop.log') -Force -ErrorAction SilentlyContinue
    }

    $appLog = Read-LogFile @((Join-Path $runDirectory 'OpenDevelop.log'), $outLog, $errLog)
    Set-Content -LiteralPath (Join-Path $runDirectory 'combined.log') -Value $appLog -Encoding utf8
    $manifestAfter = Get-ResolverManifestText

    $sdkNotFound = $appLog -match [regex]::Escape($expectedFailure)
    # A payload that refuses to start without a .NET SDK says so at startup instead (issue #23).
    $startupRefused = $appLog -match [regex]::Escape('No usable .NET SDK was found for this process')
    $resolverError = $false
    foreach ($pattern in $resolverFailures) {
        if ($appLog -match [regex]::Escape($pattern)) { $resolverError = $true }
    }
    $sdkReported = $padText -match [regex]::Escape($sdkCheckHeading)
    $effective = $null
    if ($sdks -and $sdks.effective) { $effective = [string]$sdks.effective.HighestSdkVersion }
    $roots = @()
    if ($sdks) { $roots = @($sdks.sdks) }
    $rootSummary = if ($roots.Count -eq 0) {
        'none'
    } else {
        ($roots | ForEach-Object { "$($_.RootPath) ($($_.HighestSdkVersion))" }) -join '; '
    }

    $problems = New-Object System.Collections.Generic.List[string]
    if ($name -eq 'control') {
        if ($exited) { $problems.Add('the control run left during startup although an SDK is installed') }
        if (-not $effective) { $problems.Add('the control run found no effective .NET SDK 10 or newer on this runner') }
        if (-not $loaded) { $problems.Add('the control run did not load the project it created') }
        if ($buildOk -ne $true) { $problems.Add('the control run could not build the project it created') }
        if ($sdkNotFound -or $startupRefused) { $problems.Add('the control run logged the missing-SDK failure') }
        if ($sdkReported) { $problems.Add('the control run reported a missing SDK although one is installed') }
    } else {
        if ($loaded) { $problems.Add('the project loaded although no usable .NET SDK 10 or newer exists') }
        # Either shape is the report: an older payload starts and fails the first project load, a
        # current one refuses to start at all.
        if (-not ($sdkNotFound -or $startupRefused)) {
            $problems.Add("neither the project-load failure nor the startup refusal appeared: $expectedFailure")
        }
        if ($resolverError) { $problems.Add('a resolver-deployment failure came back instead of the missing-SDK message (issue #21 regression)') }
        if (-not $sdkReported -and -not $exited) { $problems.Add("the Build pad did not report: $sdkCheckHeading") }
        if ($manifestAfter -ne '(absent)') { $problems.Add("the stale SDK-resolver manifest survived: $manifestAfter") }
        if (Test-Path -LiteralPath (Get-ResolverFolderPath)) { $problems.Add("the emptied SDK-resolver folder survived: $(Get-ResolverFolderPath)") }
    }

    $verdict = 'as expected'
    if ($problems.Count -gt 0) { $verdict = 'FAILED' }
    $result = [pscustomobject]@{
        Scenario         = $name
        SdkRoots         = $rootSummary
        EffectiveSdk     = $effective
        Exited           = $exited
        ProjectLoaded    = $loaded
        SdkNotFound      = $sdkNotFound
        StartupRefused   = $startupRefused
        BuildPadReport   = $sdkReported
        ResolverManifest = $manifestAfter
        Verdict          = $verdict
    }
    $results.Add($result)

    Write-Host ("    effective SDK: {0}; exited at startup: {1}; project loaded: {2}; missing-SDK error: {3}; startup refusal: {4}; Build pad report: {5}; resolver manifest: {6}" -f `
        $(if ($effective) { $effective } else { '(none)' }), $exited, $loaded, $sdkNotFound, $startupRefused, $sdkReported, $manifestAfter)
    if ($problems.Count -gt 0) {
        Fail "[$name] did not behave as the report says: $($problems -join '; ')"
    }
}

# --- main ------------------------------------------------------------------

# A fixed order, not the caller's: only ever removing SDKs means the control run must come first.
$ordered = @('control', 'sdk9-only', 'runtime-only') | Where-Object { $Scenario -contains $_ }

foreach ($name in $ordered) {
    if ($name -eq 'sdk9-only') {
        Write-Host ''
        Write-Host '==> sdk9-only: leaving one 9.x SDK and removing every other version'
        Stop-BuildServers
        if (-not (LimitToSdk9)) {
            Write-Host '::warning::this image has no 9.x SDK, so the sdk9-only scenario cannot be built here and is skipped'
            continue
        }
    } elseif ($name -eq 'runtime-only') {
        Write-Host ''
        Write-Host '==> runtime-only: removing every SDK folder (shared/ and templates/ stay)'
        Stop-BuildServers
        Remove-AllSdks
    }
    Invoke-Scenario $name
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('| Scenario | Discovered SDK roots | Effective SDK | Exited at startup | Project loaded | Missing-SDK error | Startup refusal | Build pad report | Resolver manifest | Verdict |')
$lines.Add('|---|---|---|---|---|---|---|---|---|---|')
foreach ($result in $results) {
    $effectiveLabel = if ($result.EffectiveSdk) { $result.EffectiveSdk } else { '(none)' }
    $lines.Add("| $($result.Scenario) | $($result.SdkRoots) | $effectiveLabel | $($result.Exited) | $($result.ProjectLoaded) | $($result.SdkNotFound) | $($result.StartupRefused) | $($result.BuildPadReport) | $($result.ResolverManifest) | $($result.Verdict) |")
}
$table = $lines -join [Environment]::NewLine

Write-Host ''
Write-Host $table
Write-Host ''
Write-Host "Artifacts: $OutputDirectory"
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $table -Encoding utf8
}
