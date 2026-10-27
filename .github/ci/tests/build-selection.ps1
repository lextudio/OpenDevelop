$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '../build-binaries.ps1'
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('od-build-selection-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $planPath = Join-Path $directory 'plan.json'
    @'
{
  "throughBatch": 4,
  "platforms": {
    "windows-x64": {},
    "windows-arm64": { "buildFrom": "windows-x64" },
    "consumer": { "buildFrom": "windows-arm64" },
    "macos-arm64": {}
  },
  "lanes": { "archive": {}, "unit": { "buildsInLane": true } },
  "entries": [
    { "id": "early", "batch": 1, "lane": "archive", "project": "early.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "arm", "batch": 4, "lane": "archive", "project": "arm.csproj", "platforms": { "windows-arm64": "observe" }, "additionalProjects": [{ "project": "fixture.csproj", "platforms": ["windows-x64"] }] },
    { "id": "transitive", "batch": 4, "lane": "archive", "project": "transitive.csproj", "platforms": { "consumer": "required" } },
    { "id": "mac", "batch": 4, "lane": "archive", "project": "mac.csproj", "platforms": { "macos-arm64": "observe" } },
    { "id": "unit-x64", "batch": 1, "lane": "unit", "project": "unit-x64.csproj", "platforms": { "windows-x64": "required" } },
    { "id": "unit-arm", "batch": 1, "lane": "unit", "project": "unit-arm.csproj", "platforms": { "windows-arm64": "observe" } }
  ]
}
'@ | Set-Content -LiteralPath $planPath
    $archive = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive selection failed.' }
    foreach ($name in @('arm.csproj', 'fixture.csproj', 'transitive.csproj')) {
        if (-not ($archive | Where-Object { $_ -like "*$name*" })) { throw "Missing $name from archive build." }
    }
    foreach ($name in @('mac.csproj', 'unit-x64.csproj', 'unit-arm.csproj')) {
        if ($archive | Where-Object { $_ -like "*$name*" }) { throw "Unexpected $name in archive build." }
    }
    $mac = & pwsh -NoProfile -File $builder -Platform macos-arm64 -ArchiveLanesOnly -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($mac | Where-Object { $_ -like '*mac.csproj*' }) -or ($mac | Where-Object { $_ -like '*arm.csproj*' -or $_ -like '*transitive.csproj*' -or $_ -like '*fixture.csproj*' })) {
        throw 'macOS archive must include its own projects and exclude Windows-only projects.'
    }
    $early = & pwsh -NoProfile -File $builder -Platform windows-x64 -ArchiveLanesOnly -ThroughBatch 3 -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($early | Where-Object { $_ -like '*early.csproj*' }) -or ($early | Where-Object { $_ -like '*arm.csproj*' })) {
        throw 'Archive selection did not respect the batch limit.'
    }
    $unit = & pwsh -NoProfile -File $builder -Platform windows-x64 -Lane unit -SkipHostBuilds -List -PlanPath $planPath
    if ($LASTEXITCODE -ne 0 -or -not ($unit | Where-Object { $_ -like '*unit-x64.csproj*' }) -or ($unit | Where-Object { $_ -like '*unit-arm.csproj*' })) {
        throw 'In-place lane must build only its own platform entries.'
    }
    Write-Host 'Build selection checks passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
