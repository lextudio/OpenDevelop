param([string] $AgentRoot = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $AgentRoot) {
    $version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw)).Project.PropertyGroup.SharpDbgAndroidAgentVersion
    if (-not $version) { throw 'Directory.Packages.props does not define SharpDbgAndroidAgentVersion.' }
    # NuGet.config pins the package folder to artifacts/nuget-packages, so restoring the debugger
    # add-in leaves the agents there.
    $AgentRoot = Join-Path $root "artifacts/nuget-packages/lextudio.sharpdbg.androidagent/$version/agent"
}
if (-not (Test-Path -LiteralPath $AgentRoot -PathType Container)) {
    throw "The Android debug agents are not restored at $AgentRoot. Build the debugger add-in (or run ./sync-openavalon-feed.ps1) first."
}
foreach ($entry in @(@{ Abi = 'arm64-v8a'; Class = 2; Machine = 183 }, @{ Abi = 'x86_64'; Class = 2; Machine = 62 }, @{ Abi = 'armeabi-v7a'; Class = 1; Machine = 40 })) {
    $path = Join-Path (Join-Path $AgentRoot $entry.Abi) 'libSharpDbg.AndroidAgent.so'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Android debugger agent missing: $path" }
    $stream = [IO.File]::OpenRead($path)
    try {
        $header = [byte[]]::new(20)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length) { throw "Truncated Android debugger agent: $path" }
    } finally { $stream.Dispose() }
    if ($header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or $header[4] -ne $entry.Class -or $header[5] -ne 1 -or [BitConverter]::ToUInt16($header, 18) -ne $entry.Machine) {
        throw "Android debugger agent has the wrong ELF architecture: $path"
    }
    foreach ($runtime in @('dotnet', 'android')) {
        foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $noticePath = Join-Path (Join-Path (Join-Path (Join-Path $AgentRoot $entry.Abi) 'licenses') $runtime) $notice
            if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) { throw "Android debugger runtime notice missing: $noticePath" }
        }
    }
    Write-Host "Verified Android debugger agent: $($entry.Abi)"
}
