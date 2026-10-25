# Packs what test jobs need into archives that they unpack at the same absolute paths.
#
#   pwsh .github/ci/package-workspace.ps1 -Platform windows-x64 -Directory $env:RUNNER_TEMP/archive
#   pwsh .github/ci/package-workspace.ps1 -Platform windows-x64 -Kind NuGetCache -Directory $env:RUNNER_TEMP/nuget-archive
#   pwsh .github/ci/package-workspace.ps1 -Extract [-Kind NuGetCache] -Directory $env:RUNNER_TEMP/archive
#
# Kind Workspace (default) is the repository with every build output, without git metadata and without
# externals/monodevelop (whose sources are only needed to compile), unpacked into -Root (the workspace).  Kind NuGetCache
# is the global packages folder ($env:NUGET_PACKAGES or ~/.nuget/packages): the IDE host is not self-contained, it
# resolves the LibreWPF assemblies from there at run time, so a lane that starts the IDE needs it.  Both are only valid on
# a runner of the same OS with the same workspace location and user profile; the workflow guarantees that.
#
# -Platform drops what the platform can never load: on windows-* the native libraries of the other systems (.so, .dylib,
# .a, .lib and runtimes/<linux|osx|android|ios|tvos|maccatalyst|browser>*), and the .pdb files under AddIns/ (the
# fixtures' own .pdb files stay: the debugger tests read them).  Other platforms exclude nothing yet.
#
# zstd is used when this machine's tar supports it (bsdtar on the runners does), gzip otherwise; -Extract finds
# whichever file is there.  Packing refuses a NuGet.config that holds a clear-text credential.
param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [ValidateSet('Workspace', 'NuGetCache')][string]$Kind = 'Workspace',
    [string]$Platform = '',
    [switch]$Extract,
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

function Get-Compression([string]$file) {
    if ($file -like '*.zst') { return [string[]]@('--zstd') }
    return [string[]]@('-z')
}

function Get-NuGetCache-Path {
    if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
    return (Join-Path $HOME '.nuget/packages')
}

function Get-Size-Megabytes([string]$path) {
    $bytes = (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    return [int][math]::Round($bytes / 1MB)
}

if (-not $Root) { $Root = if ($Kind -eq 'NuGetCache') { Get-NuGetCache-Path } else { (Get-Location).Path } }
$archiveName = if ($Kind -eq 'NuGetCache') { 'nuget' } else { 'workspace' }

New-Item -ItemType Directory -Force -Path $Directory | Out-Null
New-Item -ItemType Directory -Force -Path $Root | Out-Null

Push-Location $Root
try {
    if ($Extract) {
        $archive = @(Get-ChildItem -Path $Directory -Filter "$archiveName.tar.*" -File)
        if ($archive.Count -ne 1) { throw "Expected exactly one $archiveName.tar.* in $Directory, found $($archive.Count)." }
        [string[]]$compression = Get-Compression $archive[0].Name
        & tar -x @compression -f $archive[0].FullName
        if ($LASTEXITCODE -ne 0) { throw "tar -x failed with exit code $LASTEXITCODE" }
        Write-Host "Extracted $($archive[0].FullName) into $Root"
        return
    }

    # A credential must never travel in the archive.
    foreach ($config in Get-ChildItem -Path . -Filter NuGet.config -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](\.git|node_modules)[\\/]' }) {
        if ((Get-Content $config.FullName -Raw) -match 'ClearTextPassword') {
            throw "$($config.FullName) holds a clear-text credential; scrub it before packaging."
        }
    }

    [string[]]$exclusions = @()
    if ($Kind -eq 'Workspace') { $exclusions += @('--exclude=.git', '--exclude=node_modules', '--exclude=.vs', '--exclude=.nuget', '--exclude=externals/monodevelop') }
    if ($Platform -like 'windows-*') {
        $exclusions += @('--exclude=*.so', '--exclude=*.so.*', '--exclude=*.dylib', '--exclude=*.a', '--exclude=*.lib')
        foreach ($rid in @('linux', 'osx', 'android', 'ios', 'tvos', 'maccatalyst', 'browser')) { $exclusions += "--exclude=runtimes/$rid*" }
        if ($Kind -eq 'Workspace') { $exclusions += '--exclude=AddIns/*.pdb' }
    }

    $before = Get-Size-Megabytes '.'
    $tarVersion = (& tar --version 2>&1 | Out-String)
    $name = if ($tarVersion -match 'zstd') { "$archiveName.tar.zst" } else { "$archiveName.tar.gz" }
    $output = Join-Path $Directory $name
    [string[]]$compression = Get-Compression $name

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & tar -c @compression -f $output @exclusions .
    if ($LASTEXITCODE -ne 0) { throw "tar -c failed with exit code $LASTEXITCODE" }
    $watch.Stop()
    $megabytes = [int][math]::Round((Get-Item $output).Length / 1MB)
    $line = "Packed the $Kind into ${output}: $before MB on disk, $megabytes MB archived, in $([int]$watch.Elapsed.TotalSeconds)s (platform '$Platform')"
    Write-Host $line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### $Kind archive`n`n$line" }
}
finally { Pop-Location }
