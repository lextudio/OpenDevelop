#requires -Version 7
<#
.SYNOPSIS
    Renders the notes of a release from .github/release-notes.template.md.

.DESCRIPTION
    The published releases (v5.6.0 onward) share one body: a downloads badge, "Changes since <previous>" with a few
    hand-written bullets, the Highlight and Known issues boilerplate, and a Full Changelog link. The boilerplate lives in
    the template; this script fills in the rest.

    The bullets come from doc/releases/<tag>.md when it exists (write them there before tagging). Otherwise the bullets
    GitHub generates for the range are used, and when there are none, a TODO line, so a draft never looks finished when it
    is not. The previous version is the highest stable tag below this one, whether or not this tag is a prerelease.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$Output = 'release-notes.md',
    [string]$TemplatePath = (Join-Path $PSScriptRoot '../release-notes.template.md'),
    [string]$NotesDirectory = 'doc/releases'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') { throw "Tag '$Tag' is not vMAJOR.MINOR.PATCH[-suffix]." }
$current = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
$prerelease = [bool]$Matches[4]

$previous = git tag --list 'v*' |
    Where-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' } |
    Where-Object { [version]$_.Substring(1) -lt $current } |
    Sort-Object { [version]$_.Substring(1) } -Descending |
    Select-Object -First 1

$notesFile = Join-Path $NotesDirectory "$Tag.md"
$source = 'placeholder'
$bullets = ''
if (Test-Path $notesFile) {
    $bullets = (Get-Content $notesFile -Raw).Trim()
    $source = 'file'
}
elseif ($previous -and $Repo) {
    $generated = gh api -X POST "repos/$Repo/releases/generate-notes" -f tag_name=$Tag -f previous_tag_name=$previous --jq .body
    if ($LASTEXITCODE -eq 0) {
        $lines = @($generated -split "`n" | Where-Object { $_ -match '^\* ' })
        if ($lines.Count -gt 0) { $bullets = $lines -join "`n"; $source = 'generated' }
    }
}
if (-not $bullets) {
    $since = if ($previous) { " since $previous" } else { '' }
    $bullets = "* TODO: describe what changed$since. This draft was not published automatically; edit it, then publish. (Next time, write the bullets in $notesFile before tagging.)"
}

$heading = if ($previous) { "## Changes since $previous" } else { '## Changes' }
$changelog = if ($previous) { "**Full Changelog**: https://github.com/$Repo/compare/$previous...$Tag" } else { '' }

$body = (Get-Content $TemplatePath -Raw).
    Replace('{{REPO_LOWER}}', ($Repo ?? '').ToLowerInvariant()).
    Replace('{{TAG}}', $Tag).
    Replace('{{CHANGES_HEADING}}', $heading).
    Replace('{{CHANGES}}', $bullets).
    Replace('{{FULL_CHANGELOG}}', $changelog)
if ($body -match '\{\{[A-Z_]+\}\}') { throw "The template still has an unfilled placeholder: $($Matches[0])" }
Set-Content -Path $Output -Value $body.TrimEnd() -Encoding utf8NoBOM

Write-Host "notes: $Output  tag=$Tag  previous=$previous  prerelease=$prerelease  bullets=$source"
if ($env:GITHUB_OUTPUT) {
    "prerelease=$($prerelease.ToString().ToLowerInvariant())" | Add-Content $env:GITHUB_OUTPUT
    "previous=$previous" | Add-Content $env:GITHUB_OUTPUT
    "bullets=$source" | Add-Content $env:GITHUB_OUTPUT
}
