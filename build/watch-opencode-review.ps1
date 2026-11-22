<#
  Watches one already-launched opencode-review target without starting, retrying, or committing a review.
  It reports the most recent progress-log line between safe `Read` attempts and exits with Read's
  terminal status: 0 approved, 2 changes requested, or 4 unusable. Ctrl+C stops only this watcher.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')] [string] $Name,
    [string] $Directory = '.',
    [ValidateRange(5, 300)] [int] $IntervalSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'tmp/opencode'
$log = Join-Path $work "$Name-review.log"
$review = Join-Path $PSScriptRoot 'opencode-review.ps1'

while ($true) {
    # opencode-review uses `exit` for its documented status codes, so it must run in a child pwsh.
    # Invoking the script in this process would terminate the watcher at its first polling result.
    & pwsh -File $review -Action Read -Name $Name -Directory $Directory -MaxWaitSeconds 0
    $status = $LASTEXITCODE
    if ($status -ne 3) { exit $status }

    $lastLine = Get-Content -LiteralPath $log -Tail 1 -ErrorAction SilentlyContinue
    $stamp = Get-Date -Format 'HH:mm:ss'
    if ([string]::IsNullOrWhiteSpace($lastLine)) {
        Write-Host "[$stamp] Review is still running; progress log has no line yet."
    }
    else {
        Write-Host "[$stamp] $lastLine"
    }
    Start-Sleep -Seconds $IntervalSeconds
}
