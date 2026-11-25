# build-binaries.ps1: an additional project marked "optional": true may fail to build without failing the build job; one that is not
# optional (or that another entry needs without the flag) still fails it. Builds two tiny real projects, one broken.
$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '../build-binaries.ps1'
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('od-optional-build-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $major = (& dotnet --version).Split('.')[0]
    $good = Join-Path $directory 'good/good.csproj'
    $bad = Join-Path $directory 'bad/bad.csproj'
    New-Item -ItemType Directory -Path (Split-Path $good), (Split-Path $bad) | Out-Null
    Set-Content $good "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net$major.0</TargetFramework></PropertyGroup></Project>"
    Set-Content (Join-Path (Split-Path $good) 'Class1.cs') 'class Class1 { }'
    Set-Content $bad "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net$major.0</TargetFramework></PropertyGroup></Project>"
    Set-Content (Join-Path (Split-Path $bad) 'Broken.cs') 'this is not C#'

    function Run-Plan([string]$name, [string]$entries) {
        $planPath = Join-Path $directory "$name.json"
        @"
{ "throughBatch": 1, "platforms": { "linux-x64": {} }, "entries": [ $entries ] }
"@ | Set-Content $planPath
        $output = & pwsh -NoProfile -File $builder -Platform linux-x64 -PlanPath $planPath 2>&1 | Out-String
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output }
    }
    $entry = { param($extra) "{ `"id`": `"a`", `"batch`": 1, `"lane`": `"unit`", `"project`": `"$($good -replace '\\','/')`", `"platforms`": { `"linux-x64`": `"observe`" }, `"additionalProjects`": [ $extra ] }" }
    $badItem = "{ `"project`": `"$($bad -replace '\\','/')`"@@OPT@@ }"

    $optional = Run-Plan 'optional' (& $entry ($badItem -replace '@@OPT@@', ', "optional": true'))
    if ($optional.Exit -ne 0) { throw "An optional additional project that fails to build failed the build job:`n$($optional.Output)" }
    if ($optional.Output -notmatch 'optional build failed') { throw "No warning for the failed optional build:`n$($optional.Output)" }

    $required = Run-Plan 'required' (& $entry ($badItem -replace '@@OPT@@', ''))
    if ($required.Exit -eq 0) { throw "A failing additional project that is not optional did not fail the build job:`n$($required.Output)" }

    # Two entries need the same broken project, only one marks it optional: the one without the flag wins.
    $second = "{ `"id`": `"b`", `"batch`": 1, `"lane`": `"unit`", `"project`": `"$($good -replace '\\','/')`", `"platforms`": { `"linux-x64`": `"observe`" }, `"additionalProjects`": [ $($badItem -replace '@@OPT@@', '') ] }"
    $mixed = Run-Plan 'mixed' ((& $entry ($badItem -replace '@@OPT@@', ', "optional": true')) + ', ' + $second)
    if ($mixed.Exit -eq 0) { throw "A project one entry needs without 'optional' was treated as optional:`n$($mixed.Output)" }
    Write-Host 'Optional additional project checks passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
}
