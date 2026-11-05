$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('android-agent-verify-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($entry in @(@{ Abi = 'arm64-v8a'; Class = 2; Machine = 183 }, @{ Abi = 'x86_64'; Class = 2; Machine = 62 }, @{ Abi = 'armeabi-v7a'; Class = 1; Machine = 40 })) {
        $directory = Join-Path $scratch $entry.Abi
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $bytes = [byte[]]::new(20)
        $bytes[0] = 0x7f; $bytes[1] = 0x45; $bytes[2] = 0x4c; $bytes[3] = 0x46
        $bytes[4] = $entry.Class; $bytes[5] = 1; $bytes[18] = $entry.Machine
        [IO.File]::WriteAllBytes((Join-Path $directory 'libSharpDbg.AndroidAgent.so'), $bytes)
        foreach ($runtime in @('dotnet', 'android')) {
            $notices = Join-Path $directory "licenses/$runtime"
            New-Item -ItemType Directory -Path $notices -Force | Out-Null
            foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) { Set-Content -LiteralPath (Join-Path $notices $name) -Value 'test' }
        }
    }
    $verify = Join-Path $root 'build/verify-android-debug-agent.ps1'
    & $verify -AgentRoot $scratch
    $wrong = Join-Path $scratch 'x86_64/libSharpDbg.AndroidAgent.so'
    $bytes = [IO.File]::ReadAllBytes($wrong)
    $bytes[18] = 183
    [IO.File]::WriteAllBytes($wrong, $bytes)
    $failed = $false
    try { & $verify -AgentRoot $scratch } catch { $failed = $true }
    if (-not $failed) { throw 'The verifier accepted a library for the wrong architecture.' }
    Write-Host 'Android debug-agent architecture verification tests passed.'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
