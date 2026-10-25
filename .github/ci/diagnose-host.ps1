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
        Select-String -Path $trace -Pattern $Assembly -SimpleMatch | Where-Object { $_.Line -notmatch 'resources' } | Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
        Write-Host "--- trusted platform assemblies containing '$Assembly':"
        Select-String -Path $trace -Pattern 'TRUSTED_PLATFORM_ASSEMBLIES' | Select-Object -First 1 | ForEach-Object { ($_.Line -split ';') | Where-Object { $_ -like "*$Assembly*" } | Select-Object -First 10 | ForEach-Object { Write-Host $_ } }
        Write-Host '--- last 25 lines of the trace:'
        Get-Content $trace -Tail 25 | ForEach-Object { Write-Host $_ }
    } else { Write-Host 'no trace file was written' }
    Write-Host '::endgroup::'
    Write-Host "::group::[$name] host trace: how the host was invoked and where it probes"
    if (Test-Path $trace) {
        Select-String -Path $trace -Pattern 'Invoked hostfxr|Executing as|app_dir|deps file|runtimeconfig|probe|Probe|additional|Using dotnet root|Resolved' |
            Select-Object -First 40 | ForEach-Object { Write-Host $_.Line }
    }
    Write-Host '::endgroup::'
}

# What does the runtime itself say when it is asked to load the assembly by path?  dotnet fsi (part of the SDK) runs on the
# installed .NET 10 runtime, so the exception chain names a missing dependency, a bad image or a version problem if there is one.
function Invoke-LoadProbe {
    $script = Join-Path ([System.IO.Path]::GetTempPath()) 'load-probe.fsx'
    $out = Join-Path ([System.IO.Path]::GetTempPath()) 'load-probe-out.txt'
    $err = Join-Path ([System.IO.Path]::GetTempPath()) 'load-probe-err.txt'
    Remove-Item $out, $err -ErrorAction SilentlyContinue
    $hostFolder = $HostDirectory -replace '\\', '/'
    $names = @('WindowsBase', 'PresentationCore', 'PresentationFramework')
    $lines = @(
        'open System', 'open System.IO', 'open System.Reflection', 'open System.Runtime.Loader',
        'let show (e: exn) = (let mutable x = e in while not (isNull x) do (printfn "  %s: %s (HResult 0x%08X)" (x.GetType().FullName) x.Message x.HResult; x <- x.InnerException))',
        "let hostFolder = @""$hostFolder""",
        '// a name the default context cannot find is looked up in the host folder, the way a deps.json entry would',
        'AssemblyLoadContext.Default.add_Resolving(fun _ name -> (let p = Path.Combine(hostFolder, name.Name + ".dll") in if File.Exists p then AssemblyLoadContext.Default.LoadFromAssemblyPath p else null))'
    )
    foreach ($name in $names) {
        $lines += "printfn ""--- $name"""
        $lines += "try (let a = Assembly.LoadFrom(Path.Combine(hostFolder, ""$name.dll"")) in printfn ""  LoadFrom ok: %s"" a.FullName) with e -> show e"
        $lines += "try (let a = Assembly.Load(""$name, Version=11.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"") in (printfn ""  by name ok: %s"" a.Location; (try (a.GetTypes() |> ignore; printfn ""  all types load"") with :? ReflectionTypeLoadException as r -> (printfn ""  %d types failed to load, distinct reasons:"" (r.LoaderExceptions.Length); r.LoaderExceptions |> Array.map (fun x -> x.Message) |> Array.distinct |> Array.truncate 8 |> Array.iter (printfn ""    %s""))))) with e -> show e"
    }
    Set-Content -Path $script -Value $lines
    Write-Host '::group::load probe (dotnet fsi: LoadFrom, by name with a host-folder resolver, GetTypes)'
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @('fsi', '--quiet', $script) -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    # Bounded: the probe runs before the lane and must not be able to use up the job's time.
    if (-not $process.WaitForExit(120000)) {
        Write-Host 'dotnet fsi did not finish within 120 s; killing it.'
        try { $process.Kill($true) } catch { Write-Host "Kill failed: $($_.Exception.Message)" }
        $null = $process.WaitForExit(15000)
    } else { $process.WaitForExit() }
    foreach ($file in @($out, $err)) { if (Test-Path $file) { Get-Content $file -TotalCount 80 | ForEach-Object { Write-Host $_ } } }
    Write-Host '::endgroup::'
}
try { Invoke-LoadProbe } catch { Write-Host "load probe failed to run: $($_.Exception.Message)" }

$configDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'od-diagnose-config'
Invoke-TracedLaunch 'direct' @((Join-Path $HostDirectory 'OpenDevelop.dll'), "-configdir:$configDirectory") $HostDirectory
# Is the failure about a ReadyToRun (precompiled) image?  Same launch with precompiled code switched off.
$env:DOTNET_ReadyToRun = '0'
try { Invoke-TracedLaunch 'direct-no-r2r' @((Join-Path $HostDirectory 'OpenDevelop.dll'), "-configdir:$configDirectory") $HostDirectory }
finally { Remove-Item env:DOTNET_ReadyToRun -ErrorAction SilentlyContinue }
$projectPath = (Resolve-Path 'src/Main/SharpDevelop/SharpDevelop.csproj').Path
Invoke-TracedLaunch 'dotnet-run' @('run', '--project', $projectPath, '-f', 'net10.0-windows', '--no-build', "-configdir:$configDirectory") (Split-Path $projectPath -Parent)
exit 0
