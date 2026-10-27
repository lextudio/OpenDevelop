$ErrorActionPreference = 'Stop'
if (-not $env:GTK_MSYS_ROOT) { throw 'GTK_MSYS_ROOT is missing.' }
$environment = switch ($env:GTK_PLATFORM) {
    'windows-x64' { 'ucrt64' }
    'windows-arm64' { 'clangarm64' }
    default { throw "Unsupported GTK platform '$env:GTK_PLATFORM'." }
}
$expectedMachine = if ($env:GTK_PLATFORM -eq 'windows-arm64') { 0xaa64 } else { 0x8664 }
$prefix = Join-Path $env:GTK_MSYS_ROOT $environment
foreach ($name in @('libgtk-4-1.dll', 'libadwaita-1-0.dll')) {
    $path = Join-Path $prefix "bin/$name"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing GTK runtime: $path" }
    $stream = [System.IO.File]::OpenRead($path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE header: $path" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or $offset + 6 -gt $stream.Length) { throw "Invalid PE offset: $path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $expectedMachine) {
            throw "Wrong PE architecture for $env:GTK_PLATFORM`: $path"
        }
    } finally { $reader.Dispose() }
}
foreach ($name in @('Gtk-4.0.gir', 'Adw-1.gir')) {
    $path = Join-Path $prefix "share/gir-1.0/$name"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing GTK metadata: $path" }
}
Write-Host "GTK runtime and GIR metadata verified for $env:GTK_PLATFORM."
