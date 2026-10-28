function Get-OpenDevelopPackageProject {
    param([string]$Project)
    if ([string]::IsNullOrWhiteSpace($Project)) { $Project = $env:OPENDEVELOP_PACKAGE_PROJECT }
    if ([string]::IsNullOrWhiteSpace($Project)) {
        throw 'No package project is selected. Pass -Project <path-to-csproj>.'
    }
    return $Project
}

function Install-Package {
    param([Parameter(Mandatory, Position = 0)][string]$Id, [Parameter(Position = 1)][string]$Version, [string]$Project)
    $args = @('add', (Get-OpenDevelopPackageProject $Project), 'package', $Id)
    if ($Version) { $args += @('--version', $Version) }
    & dotnet @args
}

function Update-Package {
    param([Parameter(Mandatory, Position = 0)][string]$Id, [Parameter(Position = 1)][string]$Version, [string]$Project)
    Install-Package -Id $Id -Version $Version -Project $Project
}

function Uninstall-Package {
    param([Parameter(Mandatory, Position = 0)][string]$Id, [string]$Project)
    & dotnet remove (Get-OpenDevelopPackageProject $Project) package $Id
}

function Get-Package {
    param([string]$Project)
    & dotnet list (Get-OpenDevelopPackageProject $Project) package
}

Write-Host 'OpenDevelop Package Manager Console (pwsh)'
Write-Host 'Commands: Get-Package; Install-Package <id> [version]; Update-Package <id> [version]; Uninstall-Package <id>.'
