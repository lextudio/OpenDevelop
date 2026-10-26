# Payload bitness: one Windows download serves x64 and ARM64, so every IL-only assembly in it has to be AnyCPU.
#
#   . build/payload-architecture.ps1
#   Test-PayloadAssemblyArchitecture -PayloadRoot <dir>
#
# Exempt is anything the host selects per architecture: a `runtimes/` tree and any folder named after a RID
# (`win-x64`, `win-arm64`, `win-x86`, `osx-arm64`, `linux-x64`, ...) - those exist precisely to carry several
# architectures side by side - plus mixed-mode C++/CLI assemblies (not ILOnly, e.g. DirectWriteForwarder.dll), which are
# architecture-specific by nature and selected through the patched deps.json.
#
# dist.ps1 uses this for the distribution payload; .github/ci/verify-bitness.ps1 uses it for the builder output, so a
# build that bakes a RID into a managed assembly fails in CI instead of shipping an assembly the other architecture
# cannot load ("The assembly architecture is not compatible with the current process architecture").

function Test-PayloadAssemblyArchitecture {
    param([Parameter(Mandatory)][string]$PayloadRoot)

    $violations = foreach ($file in Get-ChildItem -LiteralPath $PayloadRoot -Recurse -File -Include '*.dll', '*.exe') {
        $relative = $file.FullName.Substring($PayloadRoot.Length).TrimStart('\', '/')
        if ($relative -match '(^|[\\/])runtimes[\\/]') { continue }
        if ($relative -match '(^|[\\/])(win|osx|linux|android|ios|tvos|maccatalyst|browser)[-a-z0-9]*[\\/]') { continue }
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            $cor = $pe.PEHeaders.CorHeader
            if ($null -eq $cor) { continue }
            if (($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::ILOnly) -eq 0) { continue }
            $machine = $pe.PEHeaders.CoffHeader.Machine
            $requires32Bit = ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::Requires32Bit) -ne 0 -and
                ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::Prefers32Bit) -eq 0
            if ($machine -ne [System.Reflection.PortableExecutable.Machine]::I386 -or $requires32Bit) {
                "$relative ($machine$(if ($requires32Bit) { ', 32-bit required' }))"
            }
        } catch [System.BadImageFormatException] {
        } finally { $stream.Dispose() }
    }
    if ($violations) {
        throw "Payload contains $(@($violations).Count) architecture-specific managed assemblies; it must be AnyCPU:`n  " + ($violations -join "`n  ")
    }
}
