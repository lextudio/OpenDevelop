#!/usr/bin/env pwsh
#
# Verifies the *packaged* Windows payload the way a user drives it, not the development tree:
# start the released launcher, then run the journeys that the payload's per-RID assets exist for.
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
    [int]$Port = 9307
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
    Fail 'the DevFlow agent did not start in the packaged application'
}

try {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    $launcher = Join-Path $payload 'OpenDevelop.exe'
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

    Write-Host '==> Checking the packaged WPF designer child host...'
    $wpfSolution = Join-Path $repoRoot 'tests/fixtures/WpfHotReloadFixture/WpfHotReloadFixture.slnx'
    $wpfXaml = Join-Path $repoRoot 'externals/vscode-wpf/sample/net6.0/MainWindow.xaml'
    if (-not (Test-Path -LiteralPath $wpfXaml)) { Fail "the linked WPF fixture source is missing: $wpfXaml" }
    $null = Invoke-DevFlow 'od.open-solution' @($wpfSolution)
    $openedXaml = Invoke-DevFlow 'od.open-file' @($wpfXaml)
    if (-not $openedXaml.opened) { Fail 'the WPF fixture XAML did not open' }
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

    $debugLine = (Select-String -LiteralPath $breakpointSource -Pattern 'var message = ComputeGreeting\("World"\);').LineNumber
    if (-not $debugLine) { Fail 'the bitness fixture breakpoint line was not found' }

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
}
