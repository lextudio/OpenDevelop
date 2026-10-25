# One-off diagnostic for a lane that starts the IDE: launches the compiled host by hand with the .NET host's own
# probing trace switched on, waits a few seconds, kills it, and prints what the trace says about the assembly the
# integration run failed to load.  It changes nothing; it exists to turn "could not load file or assembly" into
# the exact paths the host probed.  The child is killed as a tree and waited for, so no IDE instance outlives the step.
#
#   pwsh .github/ci/diagnose-host.ps1 [-Assembly PresentationFramework] [-Seconds 25]
param(
    [string]$Assembly = 'PresentationFramework',
    [int]$Seconds = 25,
    [string]$HostDirectory = 'src/Main/SharpDevelop/bin/Debug/net10.0-windows'
)

$ErrorActionPreference = 'Continue'
if (-not (Test-Path (Join-Path $HostDirectory 'OpenDevelop.dll'))) {
    Write-Host "::warning title=diagnose-host::The host assembly '$HostDirectory/OpenDevelop.dll' does not exist; nothing to diagnose (the archive did not carry the host)."
    exit 0
}
$HostDirectory = (Resolve-Path $HostDirectory).Path
$trace = Join-Path ([System.IO.Path]::GetTempPath()) 'corehost-trace.txt'
$stderr = Join-Path ([System.IO.Path]::GetTempPath()) 'corehost-stderr.txt'
$stdout = Join-Path ([System.IO.Path]::GetTempPath()) 'corehost-stdout.txt'
Remove-Item $trace, $stderr, $stdout -ErrorAction SilentlyContinue

Write-Host "::group::host directory"
Write-Host "HostDirectory=$HostDirectory"
Get-ChildItem $HostDirectory -Filter "$Assembly*.dll" | ForEach-Object { Write-Host ("{0,12:N0}  {1}" -f $_.Length, $_.Name) }
Get-ChildItem $HostDirectory -Filter 'OpenDevelop.*' | ForEach-Object { Write-Host ("{0,12:N0}  {1}" -f $_.Length, $_.Name) }
Write-Host "NUGET_PACKAGES=$env:NUGET_PACKAGES  HOME=$HOME  USERPROFILE=$env:USERPROFILE"
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
Write-Host "global packages folder '$packages' exists: $(Test-Path $packages); librewpf.transport: $(Test-Path (Join-Path $packages 'librewpf.transport'))"
dotnet --list-runtimes
Write-Host '::endgroup::'

# The integration fixture starts the IDE as 'dotnet run --project SharpDevelop.csproj -f net10.0-windows --no-build'; the
# first variant starts the compiled assembly directly.  Both are traced, so a difference between them shows up.
function Invoke-TracedLaunch([string]$name, [string[]]$arguments, [string]$workingDirectory) {
    $trace = Join-Path ([System.IO.Path]::GetTempPath()) "corehost-trace-$name.txt"
    $stderr = Join-Path ([System.IO.Path]::GetTempPath()) "corehost-stderr-$name.txt"
    $stdout = Join-Path ([System.IO.Path]::GetTempPath()) "corehost-stdout-$name.txt"
    Remove-Item $trace, $stderr, $stdout -ErrorAction SilentlyContinue
    $environment = @{ COREHOST_TRACE = '1'; COREHOST_TRACE_VERBOSITY = '4'; COREHOST_TRACEFILE = $trace; OD_TEST_MODE = '1' }
    foreach ($key in $environment.Keys) { Set-Item -Path "env:$key" -Value $environment[$key] }
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $workingDirectory -PassThru `
        -RedirectStandardError $stderr -RedirectStandardOutput $stdout
    $null = $process.WaitForExit($Seconds * 1000)
    $exited = $process.HasExited
    # 'dotnet run' starts the IDE as a child of the muxer: kill the whole tree and wait, so nothing survives into the lane.
    if (-not $exited) { try { $process.Kill($true) } catch { Write-Host "Kill failed: $($_.Exception.Message)" }; $null = $process.WaitForExit(15000) }
    foreach ($key in $environment.Keys) { Remove-Item -Path "env:$key" -ErrorAction SilentlyContinue }
    Write-Host "[$name] dotnet $($arguments -join ' ')"
    Write-Host "[$name] host $(if ($exited) { "exited with code $($process.ExitCode)" } else { "was still running after ${Seconds}s (killed)" })."

    Write-Host "::group::[$name] stderr"
    if (Test-Path $stderr) { Get-Content $stderr -TotalCount 40 | ForEach-Object { Write-Host $_ } }
    Write-Host '::endgroup::'
    Write-Host "::group::[$name] host trace lines about '$Assembly'"
    if (Test-Path $trace) {
        Write-Host "(trace has $((Get-Content $trace | Measure-Object -Line).Lines) lines)"
        Select-String -Path $trace -Pattern $Assembly -SimpleMatch | Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
    } else { Write-Host 'no trace file was written' }
    Write-Host '::endgroup::'
    Write-Host "::group::[$name] host trace: how the host was invoked and where it probes"
    if (Test-Path $trace) {
        Select-String -Path $trace -Pattern 'Invoked hostfxr|Executing as|app_dir|deps file|runtimeconfig|probe|Probe|additional|Using dotnet root|Resolved' |
            Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
    }
    Write-Host '::endgroup::'
}

$configDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'od-diagnose-config'
Invoke-TracedLaunch 'direct' @((Join-Path $HostDirectory 'OpenDevelop.dll'), "-configdir:$configDirectory") $HostDirectory
$projectPath = (Resolve-Path 'src/Main/SharpDevelop/SharpDevelop.csproj').Path
Invoke-TracedLaunch 'dotnet-run' @('run', '--project', $projectPath, '-f', 'net10.0-windows', '--no-build', "-configdir:$configDirectory") (Split-Path $projectPath -Parent)
exit 0
