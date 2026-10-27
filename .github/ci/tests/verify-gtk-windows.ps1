$ErrorActionPreference = 'Stop'
$verify = Join-Path $PSScriptRoot '../verify-gtk-windows.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('od-gtk-check-' + [guid]::NewGuid())
try {
    foreach ($case in @(@('windows-x64', 'ucrt64', 0x8664), @('windows-arm64', 'clangarm64', 0xaa64))) {
        $platform, $environment, $machine = $case
        $bin = Join-Path $root "$environment/bin"
        $gir = Join-Path $root "$environment/share/gir-1.0"
        New-Item -ItemType Directory -Path $bin, $gir -Force | Out-Null
        $bytes = [byte[]]::new(128)
        $bytes[0] = 0x4d; $bytes[1] = 0x5a; $bytes[0x3c] = 0x40; $bytes[0x40] = 0x50; $bytes[0x41] = 0x45
        $bytes[0x44] = $machine -band 0xff; $bytes[0x45] = $machine -shr 8
        foreach ($name in @('libgtk-4-1.dll', 'libadwaita-1-0.dll')) { [System.IO.File]::WriteAllBytes((Join-Path $bin $name), $bytes) }
        foreach ($name in @('Gtk-4.0.gir', 'Adw-1.gir')) { Set-Content -LiteralPath (Join-Path $gir $name) -Value 'fixture' }
        $env:GTK_MSYS_ROOT = $root; $env:GTK_PLATFORM = $platform
        & $verify
        $bytes[0x44] = 0; [System.IO.File]::WriteAllBytes((Join-Path $bin 'libgtk-4-1.dll'), $bytes)
        try { & $verify; throw 'Wrong architecture was accepted.' } catch {
            if ($_.Exception.Message -notmatch 'Wrong PE architecture') { throw }
        }
        [System.IO.File]::WriteAllBytes((Join-Path $bin 'libgtk-4-1.dll'), [byte[]]::new(128))
        try { & $verify; throw 'Invalid PE was accepted.' } catch {
            if ($_.Exception.Message -notmatch 'Invalid PE header') { throw }
        }
        $bytes[0x44] = $machine -band 0xff
        [System.IO.File]::WriteAllBytes((Join-Path $bin 'libgtk-4-1.dll'), $bytes)
        Remove-Item -LiteralPath (Join-Path $gir 'Adw-1.gir')
        try { & $verify; throw 'Missing metadata was accepted.' } catch {
            if ($_.Exception.Message -notmatch 'Missing GTK metadata') { throw }
        }
    }
    Write-Host 'Windows GTK validation checks passed.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:GTK_MSYS_ROOT, Env:GTK_PLATFORM -ErrorAction SilentlyContinue
}
