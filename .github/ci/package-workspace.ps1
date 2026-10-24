# Packs the whole workspace (sources and every build output, without git metadata) into one archive that test jobs
# unpack at the same absolute path.  The paths inside the build outputs are absolute on the build machine, so the
# archive is only valid on a runner of the same OS with the same workspace location; the workflow guarantees that.
#
#   pwsh .github/ci/package-workspace.ps1 -Directory $env:RUNNER_TEMP/archive          # writes workspace.tar.zst (or .tar.gz)
#   pwsh .github/ci/package-workspace.ps1 -Extract -Directory $env:RUNNER_TEMP/archive # unpacks it into -Root
#
# zstd is used when this machine's tar supports it (bsdtar on the runners does), gzip otherwise; -Extract finds
# whichever file is there.
param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [switch]$Extract,
    [string]$Root = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

function Get-Compression([string]$file) {
    if ($file -like '*.zst') { return [string[]]@('--zstd') }
    return [string[]]@('-z')
}

New-Item -ItemType Directory -Force -Path $Directory | Out-Null

Push-Location $Root
try {
    if ($Extract) {
        $archive = @(Get-ChildItem -Path $Directory -Filter 'workspace.tar.*' -File)
        if ($archive.Count -ne 1) { throw "Expected exactly one workspace.tar.* in $Directory, found $($archive.Count)." }
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

    $tarVersion = (& tar --version 2>&1 | Out-String)
    $name = if ($tarVersion -match 'zstd') { 'workspace.tar.zst' } else { 'workspace.tar.gz' }
    $output = Join-Path $Directory $name
    [string[]]$compression = Get-Compression $name

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & tar -c @compression -f $output --exclude=.git --exclude=node_modules --exclude=.vs --exclude=.nuget .
    if ($LASTEXITCODE -ne 0) { throw "tar -c failed with exit code $LASTEXITCODE" }
    $watch.Stop()
    $megabytes = [int][math]::Round((Get-Item $output).Length / 1MB)
    $line = "Packed the workspace into ${output}: $megabytes MB in $([int]$watch.Elapsed.TotalSeconds)s"
    Write-Host $line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "### Workspace archive`n`n$line" }
}
finally { Pop-Location }
