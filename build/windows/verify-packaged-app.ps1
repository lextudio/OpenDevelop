#!/usr/bin/env pwsh
#
# Verifies the *packaged* Windows payload the way a user drives it, not the development tree:
# start the released launcher, then run the same journeys as the macOS and Linux checks (solution and build, Roslyn child
# host, terminal, ILSpy, XAML language server, WPF designer, debugger breakpoint; see build/verify-packaged-common.sh,
# whose DevFlow actions and assertions this mirrors), then the journeys that the payload's per-RID assets exist for.
#
# The bitness journeys are the reason the payload keeps add-in `runtimes/<rid>` trees at all. Each
# one builds a framework-dependent fixture for one RID, starts it under the packaged debugger and
# requires the session to stop at a breakpoint - so a missing (or wrongly pruned) `dbgshim.dll` or
# mixed-mode runtime turns the release red instead of reaching users. The runner needs the target
# architecture's .NET runtime installed; the workflow does that, mirroring a user who debugs an
# x86 application from an x64 install.
#
# Usage:
#   ./build/windows/verify-packaged-app.ps1 -PayloadRoot OpenDevelop-win -Targets win-x64,win-x86
#
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    # One journey per RID. The runner can only execute what its own architecture supports
    # natively or through Windows' emulation layers (x64 runner: x64 + x86; ARM64 runner:
    # arm64 + x64 + x86).
    [Parameter(Mandatory)][string[]]$Targets,
    [int]$Port = 9307,
    # Skips only the per-RID bitness journeys: they still start BitnessFixture.dll (dotnet <dll>), so the process is
    # always the runner's own dotnet and the RID never matters; they are being rebuilt around an apphost. The debugger
    # breakpoint journey on the runner's own architecture and the per-RID dbgshim check still run.
    [switch]$SkipDebugJourneys
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$payload = (Resolve-Path $PayloadRoot).Path
$baseUrl = "http://127.0.0.1:$Port"
$leaseId = [guid]::NewGuid().ToString()
$configDir = Join-Path ([System.IO.Path]::GetTempPath()) ("od-bitness-" + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $repoRoot 'tests/fixtures/BitnessFixture/BitnessFixture.csproj'
$breakpointSource = Join-Path $repoRoot 'tests/fixtures/BitnessFixture/Program.cs'
$appProcess = $null
$debugFixture = Join-Path $repoRoot 'tests/fixtures/DebugTestApp'

function Fail([string]$message) {
    Write-Host "::error::packaged Windows verification: $message"
    throw $message
}

function Invoke-DevFlow([string]$action, [object[]]$Arguments = @()) {
    # The agent uses a short mutation lease; claim immediately before every request.
    $leaseBody = @{ action = 'claim'; leaseId = $leaseId; holderKind = 'release-bitness-check'; label = 'packaged Windows bitness verification'; force = $true } | ConvertTo-Json -Compress
    $lease = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/agent/lease" -ContentType 'application/json' -Body $leaseBody -TimeoutSec 15
    if (-not $lease.allowed) { Fail "the DevFlow agent refused the lease for $action" }
    $body = @{ args = $Arguments } | ConvertTo-Json -Depth 6 -Compress
    $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/invoke/actions/$action" -Headers @{ 'X-DevFlow-Lease' = $leaseId } -ContentType 'application/json' -Body $body -TimeoutSec 120
    if (-not $response.success) { Fail "$action returned an unsuccessful envelope: $($response | ConvertTo-Json -Depth 6 -Compress)" }
    if ($null -eq $response.returnValue) { return $null }
    return ($response.returnValue | ConvertFrom-Json)
}

# Polls an action until the predicate accepts its result or the attempts run out; returns the last result either way.
function Wait-DevFlow([string]$action, [object[]]$Arguments, [scriptblock]$Accept, [int]$Attempts = 30, [int]$DelaySeconds = 2) {
    $result = $null
    for ($i = 0; $i -lt $Attempts; $i++) {
        $result = Invoke-DevFlow $action $Arguments
        if (& $Accept $result) { return $result }
        Start-Sleep -Seconds $DelaySeconds
    }
    return $result
}

function Get-PeMachine([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40) { Fail "not a PE file: $Path" }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    return [BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

$machineNames = @{ 0x014c = 'win-x86'; 0x8664 = 'win-x64'; 0xAA64 = 'win-arm64' }

function Assert-PeMatches([string]$Path, [string]$Rid) {
    if (-not (Test-Path -LiteralPath $Path)) { Fail "the $Rid fixture was not built: $Path" }
    $machine = Get-PeMachine $Path
    $actual = if ($machineNames.ContainsKey([int]$machine)) { $machineNames[[int]$machine] } else { ('0x{0:X4}' -f $machine) }
    if ($actual -ne $Rid) { Fail "the $Rid fixture produced a $actual executable ($Path): inspect the build, not the payload" }
}

function Wait-Agent {
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $status = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/v1/agent/status" -TimeoutSec 2
            if ($status.running) { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    # Say what the launcher did: a launcher that exited with a code never started its host; one still running started it
    # but the agent never answered.
    $state = if ($null -eq $appProcess) { 'was not started' } elseif ($appProcess.HasExited) { "exited with code $($appProcess.ExitCode)" } else { 'is still running' }
    Fail "the DevFlow agent did not start in the packaged application (the launcher $state)"
}

try {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    # The payload carries both launchers; run the one for this machine's architecture, as dist.ps1's own smoke phase does.
    # An ARM64 runner has no x64 .NET runtime, so the x64 launcher's child host never starts there and the agent never answers.
    $launcherName = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'OpenDevelopARM64.exe' } else { 'OpenDevelop.exe' }
    $launcher = Join-Path $payload $launcherName
    $arguments = @("-devflow:$Port", "-configdir:$configDir")
    Write-Host "==> Starting the packaged application ($launcher)..."
    if (Test-Path -LiteralPath $launcher) {
        $appProcess = Start-Process -FilePath $launcher -ArgumentList $arguments -WorkingDirectory $payload -PassThru
    } else {
        $appProcess = Start-Process -FilePath 'dotnet' -ArgumentList (@('exec', (Join-Path $payload 'OpenDevelop.dll')) + $arguments) -WorkingDirectory $payload -PassThru
    }
    Wait-Agent
    $null = Invoke-DevFlow 'od.active-view'
    Start-Sleep -Seconds 2
    $null = Invoke-DevFlow 'od.active-view'

    Write-Host '==> Opening and building the solution fixture...'
    $solution = Join-Path $repoRoot 'tests/fixtures/SolutionExplorerFixture/SolutionExplorerFixture.slnx'
    $opened = Invoke-DevFlow 'od.open-solution' @($solution)
    if (-not $opened.success -or $opened.currentSolution -ne $solution) { Fail 'the solution fixture did not open' }
    $built = Invoke-DevFlow 'od.build-solution'
    if (-not $built.success -or $built.result -ne 'Success' -or $built.errorCount -ne 0) { Fail "the solution fixture did not build cleanly: $($built | ConvertTo-Json -Compress)" }

    Write-Host '==> Checking the packaged Roslyn child host and project workspace...'
    $csharpSource = Join-Path $repoRoot 'tests/fixtures/SolutionExplorerFixture/SampleApp/Program.cs'
    if (-not (Invoke-DevFlow 'od.open-file' @($csharpSource)).opened) { Fail 'the C# fixture source did not open' }
    $roslyn = Wait-DevFlow 'od.language-workspace.status' @($csharpSource) { param($r) $r.supported -and $r.tracked -and $r.metadataReferenceCount -gt 0 }
    if (-not ($roslyn.supported -and $roslyn.tracked -and $roslyn.metadataReferenceCount -gt 0)) { Fail "the Roslyn child host did not provide a project workspace: $($roslyn | ConvertTo-Json -Compress)" }
    $legacy = Invoke-DevFlow 'od.roslyn-legacy-workspace.status'
    if (-not $legacy.remoteHostMode -or $legacy.workspaceCreated) { Fail 'Roslyn fell back to the deprecated in-process workspace' }

    Write-Host '==> Checking terminal shell, output, and both split directions...'
    $terminal = Invoke-DevFlow 'od.terminal.new' @($env:ComSpec)
    if (-not $terminal.success -or $terminal.pid -le 0) { Fail 'the terminal shell did not start' }
    # cmd expands %OS% itself: the echoed command line contains "terminal-%OS%", only the output contains
    # "terminal-Windows_NT", so the check cannot pass on the typed input alone.
    if (-not (Invoke-DevFlow 'od.terminal.send' @("echo terminal-%OS%`r")).success) { Fail 'terminal input failed' }
    $terminalText = Wait-DevFlow 'od.terminal.text' @() { param($r) $r.text -like '*terminal-Windows_NT*' } -Attempts 25 -DelaySeconds 1
    if ($terminalText.text -notlike '*terminal-Windows_NT*') { Fail 'the terminal did not render shell output' }
    if (-not (Invoke-DevFlow 'od.terminal.split').success) { Fail 'the horizontal terminal split failed' }
    if (-not (Invoke-DevFlow 'od.terminal.split-vertical').success) { Fail 'the vertical terminal split failed' }
    if (@((Invoke-DevFlow 'od.terminal.list').sessions).Count -lt 3) { Fail 'the terminal splits did not create three sessions' }

    Write-Host '==> Checking the hosted ILSpy add-in...'
    if (-not (Invoke-DevFlow 'od.ilspy.open-assembly' @((Join-Path $payload 'OpenDevelop.dll'))).opened) { Fail 'ILSpy could not open the packaged assembly' }
    $ilspy = Invoke-DevFlow 'od.ilspy.status'
    if (@($ilspy.loadedAssemblies) -notcontains 'OpenDevelop' -or @($ilspy.panes).Count -lt 3) { Fail "ILSpy panes or decompiler dependencies are unavailable: $($ilspy | ConvertTo-Json -Compress)" }

    Write-Host '==> Checking the packaged WPF designer child host...'
    $wpfSolution = Join-Path $repoRoot 'tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.slnx'
    $wpfXaml = Join-Path $repoRoot 'tests/fixtures/WpfHotReloadFixture/MainWindow.xaml'
    if (-not (Test-Path -LiteralPath $wpfXaml)) { Fail "the linked WPF fixture source is missing: $wpfXaml" }
    # The profile-aware XAML language server answers "restore required" (no outline) for a project that was never restored.
    # Run from the repository root: global.json (the LibreWPF.Sdk pin) is resolved from the working directory.
    Push-Location $repoRoot
    try { & dotnet restore 'tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.csproj' | Out-Null; $restoreExit = $LASTEXITCODE } finally { Pop-Location }
    if ($restoreExit -ne 0) { Fail 'the WPF fixture restore failed' }
    $null = Invoke-DevFlow 'od.open-solution' @($wpfSolution)
    $openedXaml = Invoke-DevFlow 'od.open-file' @($wpfXaml)
    if (-not $openedXaml.opened) { Fail 'the WPF fixture XAML did not open' }
    $xamlServer = Invoke-DevFlow 'od.xaml.language-server' @($wpfXaml)
    $expectedWorkspace = [System.IO.Path]::GetFullPath((Split-Path $wpfSolution -Parent)).TrimEnd('\', '/')
    $actualWorkspace = if ($xamlServer.workspace) { [System.IO.Path]::GetFullPath([string]$xamlServer.workspace).TrimEnd('\', '/') } else { '' }
    if (-not $xamlServer.success -or $xamlServer.key -ne 'LibreWpf' -or $xamlServer.server -ne 'WinUiXaml.LanguageServer.dll' -or @($xamlServer.arguments) -notcontains 'librewpf' -or $actualWorkspace -ne $expectedWorkspace) {
        Fail "the XAML file was not routed to the packaged LibreWPF language server: $($xamlServer | ConvertTo-Json -Compress)"
    }
    $outlineAccepted = { param($r) $r.active -and $r.rootChildCount -gt 0 -and @($r.outlineNames | Where-Object { $_ -like 'Button (PrimaryButton)*' }).Count -gt 0 }
    $outline = Wait-DevFlow 'od.xaml-outline.status' @() $outlineAccepted
    if (-not (& $outlineAccepted $outline)) { Fail "the LibreWPF XAML language server did not return the fixture outline: $($outline | ConvertTo-Json -Compress)" }
    $null = Invoke-DevFlow 'od.wpf-designer.activate-design'
    $designerStatus = $null
    for ($i = 0; $i -lt 30; $i++) {
        $designerStatus = Invoke-DevFlow 'od.wpf-designer.status'
        if ($designerStatus.hostAlive -and $designerStatus.designerLoaded -and $designerStatus.hasRenderFrame) { break }
        Start-Sleep -Seconds 2
    }
    if (-not ($designerStatus.hostAlive -and $designerStatus.designerLoaded -and $designerStatus.hasRenderFrame)) {
        # Print the whole status: the field set is the add-in's, and it is what says whether the
        # host never started, started but did not load, or loaded without rendering.
        Fail "the packaged WPF designer did not load a rendered frame: $($designerStatus | ConvertTo-Json -Depth 5 -Compress)"
    }

    # The shared-closure change removed each host's private copy of the managed closure and hands
    # it an absolute probing path instead. A host that lost that flag still starts, then fails to
    # resolve its first dependency, so assert the wiring on the live child rather than on files.
    $sharedManaged = Join-Path $payload 'shared/managed'
    if (Test-Path -LiteralPath $sharedManaged) {
        $hostProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*WpfDesign.SurfaceHost.dll*' })
        if ($hostProcesses.Count -eq 0) { Fail 'no WpfDesign surface host process is running after activating design view' }
        foreach ($hostProcess in $hostProcesses) {
            if ($hostProcess.CommandLine -notlike "*--additionalprobingpath*$sharedManaged*") {
                Fail "the designer host was not started with the shared probing path: $($hostProcess.CommandLine)"
            }
        }
        Write-Host "    shared closure: designer host started with --additionalprobingpath '$sharedManaged'"
    }

    # The per-RID debugger shim is what the payload keeps `runtimes/<rid>` trees for: a pruned one used to surface only when a
    # user debugged an x86 or ARM64 application, so check it for every target without needing that architecture's runtime.
    foreach ($rid in $Targets) {
        $shim = Join-Path $payload "AddIns/Debugger/runtimes/$rid/native/dbgshim.dll"
        if (-not (Test-Path -LiteralPath $shim)) { Fail "the payload has no $rid debugger shim: $shim" }
    }

    Write-Host '==> Checking debugger launch and breakpoint stop...'
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'AddIns/Debugger/SharpDbg.Cli.dll'))) { Fail 'the debugger add-in is missing SharpDbg.Cli.dll' }
    # The adapter's DAP transport is SharpDbg's own MIT DapProtocol. Microsoft's debug protocol package is under a
    # license that cannot be redistributed, and a stale development output folder can still hold a copy (the copy
    # targets never delete).
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'AddIns/Debugger/SharpDbg.DapProtocol.dll'))) { Fail 'the debugger add-in is missing SharpDbg.DapProtocol.dll' }
    if (Get-ChildItem -LiteralPath (Join-Path $payload 'AddIns/Debugger') -Recurse -Filter 'Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll' -File -ErrorAction SilentlyContinue | Select-Object -First 1) { Fail 'the debugger add-in ships Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.dll (not redistributable; the adapter uses SharpDbg.DapProtocol)' }
    foreach ($retired in @('OmniSharp.Extensions.*.dll', 'System.Reactive.dll')) {
        if (Get-ChildItem -LiteralPath (Join-Path $payload 'AddIns/Debugger') -Recurse -Filter $retired -File -ErrorAction SilentlyContinue | Select-Object -First 1) { Fail "the debugger add-in ships a retired DAP transport file: $retired" }
    }
    foreach ($abi in @('arm64-v8a', 'x86_64', 'armeabi-v7a')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload "AddIns/Debugger/android/$abi/libSharpDbg.AndroidAgent.so"))) { Fail "the debugger add-in is missing the Android agent for $abi" }
        foreach ($runtime in @('dotnet', 'android')) {
            foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
                if (-not (Test-Path -LiteralPath (Join-Path $payload "AddIns/Debugger/android/$abi/licenses/$runtime/$notice"))) { Fail "the Android agent for $abi is missing $runtime/$notice" }
            }
        }
    }
    $debugSource = Join-Path $debugFixture 'Program.cs'
    $debugProject = Join-Path $debugFixture 'DebugTestApp.csproj'
    $debugAppLine = (Select-String -LiteralPath $debugSource -Pattern 'var message = ComputeGreeting\("World"\);').LineNumber
    if (-not $debugAppLine) { Fail 'the debug fixture breakpoint source line was not found' }
    # The check starts the fixture binary, not the project, so a checkout that never built the test tree has nothing to run.
    if (-not (Test-Path -LiteralPath (Join-Path $debugFixture 'bin/Debug/net10.0/DebugTestApp.dll'))) {
        & dotnet build $debugProject -c Debug -v q --nologo
        if ($LASTEXITCODE -ne 0) { Fail 'the debug fixture did not build' }
    }
    if (-not (Invoke-DevFlow 'od.open-solution' @((Join-Path $debugFixture 'DebugTestApp.slnx'))).success) { Fail 'the debug fixture solution did not open' }
    $null = Invoke-DevFlow 'od.debug.clear-breakpoints'
    $debugBreakpoint = Invoke-DevFlow 'od.debug.set-breakpoint' @($debugSource, $debugAppLine)
    if (-not $debugBreakpoint.success -or ($debugBreakpoint.lines -notcontains $debugAppLine)) { Fail 'the debug breakpoint was not set' }
    $debugStarted = $null
    try { $debugStarted = Invoke-DevFlow 'od.debug.start' @($debugProject, $true, 60) } catch { Write-Host "    debug start failed: $($_.Exception.Message)" }
    $debugOutput = $null
    try { $debugOutput = Invoke-DevFlow 'od.debug.output' } catch { }
    $null = try { Invoke-DevFlow 'od.debug.stop' } catch { }
    if (-not ($debugStarted.started -and $debugStarted.stopped -and $debugStarted.currentLine -eq $debugAppLine)) {
        Fail "the debugger did not stop at the fixture breakpoint: $($debugStarted | ConvertTo-Json -Compress); output: $($debugOutput | ConvertTo-Json -Compress)"
    }

    $debugLine = (Select-String -LiteralPath $breakpointSource -Pattern 'var message = ComputeGreeting\("World"\);').LineNumber
    if (-not $debugLine) { Fail 'the bitness fixture breakpoint line was not found' }

    if ($SkipDebugJourneys) {
        Write-Host '==> Skipping the per-RID bitness journeys (being rebuilt around an apphost).'
        Write-Host 'Packaged Windows verification passed.'
        return
    }

    foreach ($rid in $Targets) {
        Write-Host "==> Debugging the $rid fixture..."
        if ($rid -ne 'win-x64' -and $rid -ne 'win-x86' -and $rid -ne 'win-arm64') { Fail "unsupported target RID: $rid" }
        $shim = Join-Path $payload "AddIns/Debugger/runtimes/$rid/native/dbgshim.dll"
        if (-not (Test-Path -LiteralPath $shim)) { Fail "the payload has no $rid debugger shim: $shim" }

        # Framework-dependent, like the product: we do not ship a runtime, and a user who debugs an
        # x86 application has the x86 .NET runtime installed beside the x64 one. The workflow
        # installs exactly those runtimes, so this journey tests the resolution users get rather
        # than a self-contained copy.
        & dotnet build $fixture -c Debug -p:RuntimeIdentifier=$rid -p:SelfContained=false -v q --nologo
        if ($LASTEXITCODE -ne 0) { Fail "the $rid fixture did not build" }
        # The IDE finds a debuggee from the project's own properties, so it looks in
        # bin/Debug/net10.0 while -p:RuntimeIdentifier moved the output one level down. Copy the
        # RID build into place (bytes unchanged, so the debuggee is still that architecture) after
        # clearing anything a previous journey left there.
        $outputDirectory = Join-Path $repoRoot 'tests/fixtures/BitnessFixture/bin/Debug/net10.0'
        $ridDirectory = Join-Path $outputDirectory $rid
        Assert-PeMatches (Join-Path $ridDirectory 'BitnessFixture.exe') $rid
        Get-ChildItem -LiteralPath $outputDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
        Copy-Item -Path (Join-Path $ridDirectory '*') -Destination $outputDirectory -Recurse -Force
        Assert-PeMatches (Join-Path $outputDirectory 'BitnessFixture.exe') $rid

        $null = Invoke-DevFlow 'od.debug.clear-breakpoints'
        $breakpoint = Invoke-DevFlow 'od.debug.set-breakpoint' @($breakpointSource, $debugLine)
        if (-not $breakpoint.success -or ($breakpoint.lines -notcontains $debugLine)) { Fail "the $rid breakpoint was not set" }

        $started = $null
        $stopped = $false
        try {
            $started = Invoke-DevFlow 'od.debug.start' @($fixture, $true, 60)
            $stopped = [bool]($started.started -and $started.stopped -and $started.currentLine -eq $debugLine)
        } catch {
            Write-Host "    $rid debug start failed: $($_.Exception.Message)"
        }
        $output = $null
        try { $output = Invoke-DevFlow 'od.debug.output' } catch { }
        $null = try { Invoke-DevFlow 'od.debug.stop' } catch { }
        if (-not $stopped) {
            Fail "the $rid debugger did not stop at the fixture breakpoint: $($started | ConvertTo-Json -Compress); output: $($output | ConvertTo-Json -Compress)"
        }
        Write-Host "    ${rid}: stopped at line $debugLine"
    }

    Write-Host 'Packaged Windows verification passed.'
} finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        try { & taskkill /T /F /PID $appProcess.Id | Out-Null } catch { }
    }
    Remove-Item -LiteralPath $configDir -Recurse -Force -ErrorAction SilentlyContinue
    # taskkill exits non-zero for a child that has already gone, and the workflow step takes the last native exit code for
    # the script's own: a run in which every journey passed was reported as a failure. A real failure still throws above.
    $global:LASTEXITCODE = 0
}
