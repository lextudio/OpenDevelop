<#
  OpenCode review and commit runner. AGENTS.md ("OpenCode review and commits") requires every review and every commit that
  needs one to go through this script, so the verification rules are applied the same way every time.

    pwsh build/opencode-review.ps1 -Action Launch -Name <unique> [-Directory <repo or submodule dir>]
    pwsh build/opencode-review.ps1 -Action Read   -Name <unique> [-Directory <the same directory>] [-MaxWaitSeconds 540]
    pwsh build/opencode-review.ps1 -Action Commit -Name <unique> [-Directory <the same directory>]

  <unique> names one review target (for example `od-b1-agent-ci-source`). Everything lives in tmp/opencode:
    <unique>-prompt.txt          you write it first (intent, paths, tree hash, invariants, risks, tests and results)
    <unique>-tree.txt            Launch records `git write-tree` of the staged target
    <unique>-sessions-before.txt Launch snapshots `opencode session list` before the run
    <unique>-review.json/.log    the run's stdout (json events) and stderr (live progress log)
    <unique>-export.json         Read's `opencode session export`
    <unique>-final.txt           the reviewer's final message, from the export
    <unique>-message.txt         the commit message, extracted verbatim when the review approves
    <unique>-commit.json/.log    Commit's run output

  Launch preflights the staged target (no index.lock, `git diff --cached --check`, a non-empty diff, a tree hash), records
  it and runs the reviewer in the foreground (stdin is /dev/null equivalent). It never retries; a failed or non-terminal run
  is reported and left to the user.

  Read polls `opencode session export` (it prints nothing or "Session not found" for 5-20 minutes after a run ends) and then
  verifies the export, not the streamed output: the session id is absent from the pre-launch snapshot, its title is the exact
  `<unique> <tree8>`, its directory/agent/model are the intended ones, it was created after the launch and succeeded, its last
  assistant message finished normally, it carries one verdict (the same line repeated is one verdict; APPROVED and
  CHANGES_REQUESTED together are unusable) and, on approval, a COMMIT_MESSAGE_BEGIN/END block. It also fails when the staged
  tree is no longer the reviewed one. Exit codes: 0 approved and valid, 2 changes requested, 3 export not available yet
  (run Read again), 4 unusable or stale.

  Commit refuses unless the staged tree still equals the reviewed tree, then has the OpenCode `build` agent run
  `git commit -F <unique>-message.txt` and checks that the new commit's tree equals the reviewed tree. The invoking agent never
  writes or edits the message.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Launch', 'Read', 'Commit')] [string] $Action,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')] [string] $Name,
    [string] $Directory = '.',
    [string] $PromptFile,
    [int] $MaxWaitSeconds = 540,
    [string] $Model = 'opencode-go/deepseek-v4.1-flash#high'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'tmp/opencode'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$target = if ([IO.Path]::IsPathRooted($Directory)) { $Directory } else { Join-Path $root $Directory }
$target = (Resolve-Path -LiteralPath $target).Path
function Artifact([string] $suffix) { Join-Path $work "$Name$suffix" }

function Invoke-Git([string[]] $Arguments) {
    $output = & git -C $target @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $output" }
    return ($output | Out-String).TrimEnd()
}

function Get-GitDirectory {
    $dir = Invoke-Git @('rev-parse', '--git-dir')
    if ([IO.Path]::IsPathRooted($dir)) { return $dir }
    return (Join-Path $target $dir)
}

function Get-StagedTree {
    if (Test-Path -LiteralPath (Join-Path (Get-GitDirectory) 'index.lock')) {
        throw 'There is an index.lock. Check that no git process is running, remove the stale lock, and start again.'
    }
    $null = Invoke-Git @('diff', '--cached', '--check')
    if (-not (Invoke-Git @('diff', '--cached', '--name-only'))) { throw 'Nothing is staged in this directory.' }
    return Invoke-Git @('write-tree')
}

function Get-SessionList { (($null | & opencode session list 2>&1) | Out-String) }

function Get-FinalText($export) {
    $assistant = @($export.messages | Where-Object { $_.type -eq 'assistant' }) | Select-Object -Last 1
    if (-not $assistant) { return $null, $null }
    $text = if ($assistant.content -is [string]) { $assistant.content } else { -join @($assistant.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) }
    return $assistant, $text
}

function Invoke-OpenCodeRun([string[]] $Arguments) {
    if ($IsWindows) {
        $null | & opencode @Arguments
        return
    }

    $zsh = Get-Command zsh -ErrorAction Stop
    & $zsh.Source -c 'exec "$0" "$@" </dev/null' opencode @Arguments
}

switch ($Action) {
    'Launch' {
        $prompt = if ($PromptFile) { $PromptFile } else { Artifact '-prompt.txt' }
        if (-not (Test-Path -LiteralPath $prompt)) { throw "Write the review prompt first: $prompt" }
        $tree = Get-StagedTree
        # Nothing from an earlier run of this name may be mistaken for this review (a stale message, export or approval).
        foreach ($stale in '-message.txt', '-final.txt', '-export.json', '-export.err', '-approved-tree.txt', '-sessionid.txt', '-commit.json', '-commit.log', '-review.json', '-review.log', '-review-exit.txt') {
            Remove-Item -LiteralPath (Artifact $stale) -Force -ErrorAction SilentlyContinue
        }
        Set-Content -LiteralPath (Artifact '-tree.txt') -Value $tree -NoNewline
        Set-Content -LiteralPath (Artifact '-sessions-before.txt') -Value (Get-SessionList)
        $launched = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Set-Content -LiteralPath (Artifact '-launch.txt') -Value $launched -NoNewline
        $title = "$Name $($tree.Substring(0, 8))"
        Write-Host "Reviewing tree $tree in $target"
        Write-Host "  stdout: $(Artifact '-review.json')"
        Write-Host "  progress log: $(Artifact '-review.log')   (service log: ~/.local/share/opencode/log/opencode.log)"
        Push-Location $target
        try {
            Invoke-OpenCodeRun @('run', '--format', 'json', '--print-logs', '--log-level', 'info', '--agent', 'reviewer', '-m', $Model, '--title', $title, (Get-Content -LiteralPath $prompt -Raw)) > (Artifact '-review.json') 2> (Artifact '-review.log')
            $exit = $LASTEXITCODE
        } finally { Pop-Location }
        Write-Host "opencode exit code: $exit"
        Set-Content -LiteralPath (Artifact '-review-exit.txt') -Value $exit -NoNewline
        $sessionId = ''
        foreach ($line in (Get-Content -LiteralPath (Artifact '-review.json') -ErrorAction SilentlyContinue)) {
            if ($line -match '"sessionID":"(?<id>[^"]+)"') { $sessionId = $Matches['id']; break }
        }
        if (-not $sessionId) { Write-Host 'No session was created: the review is NOT verified (see the .log). Do not retry; report it.'; exit 4 }
        Set-Content -LiteralPath (Artifact '-sessionid.txt') -Value $sessionId -NoNewline
        Write-Host "Session $sessionId. Next: pwsh build/opencode-review.ps1 -Action Read -Name $Name"
        exit 0
    }

    'Read' {
        $sessionFile = Artifact '-sessionid.txt'
        $sessionId = if (Test-Path -LiteralPath $sessionFile) { (Get-Content -LiteralPath $sessionFile -Raw).Trim() } else { '' }
        if (-not $sessionId) {
            foreach ($line in (Get-Content -LiteralPath (Artifact '-review.json') -ErrorAction SilentlyContinue)) {
                if ($line -match '"sessionID":"(?<id>[^"]+)"') { $sessionId = $Matches['id']; break }
            }
        }
        $sessionId = [regex]::Match($sessionId, 'ses_[A-Za-z0-9]+').Value
        if (-not $sessionId) { throw "No session id for $Name (run -Action Launch first)." }
        $exportFile = Artifact '-export.json'
        $deadline = [DateTime]::UtcNow.AddSeconds($MaxWaitSeconds)
        $export = $null
        while ($true) {
            $null = Get-SessionList
            $null | & opencode session export $sessionId > $exportFile 2> (Artifact '-export.err')
            $raw = Get-Content -LiteralPath $exportFile -Raw -ErrorAction SilentlyContinue
            if ($raw -and $raw.Contains('"info"')) { $export = ($raw.Substring($raw.IndexOf('{')) | ConvertFrom-Json -Depth 100); break }
            if ([DateTime]::UtcNow -gt $deadline) { break }
            Start-Sleep -Seconds 30
        }
        if (-not $export) { Write-Host "The export is not available yet ($([DateTime]::Now.ToString('HH:mm:ss'))). Run Read again; do not start another review."; exit 3 }

        $problems = [System.Collections.Generic.List[string]]::new()
        $info = $export.info
        # Every check below needs what Launch recorded; a missing record is a failed verification, never a skipped one.
        $treeText = Get-Content -LiteralPath (Artifact '-tree.txt') -Raw -ErrorAction SilentlyContinue
        $tree = if ($treeText) { $treeText.Trim() } else { '' }
        if ($tree.Length -lt 8) { $problems.Add("the recorded tree is missing (Launch did not run for $Name)") }
        $before = Get-Content -LiteralPath (Artifact '-sessions-before.txt') -Raw -ErrorAction SilentlyContinue
        if ($null -eq $before) { $problems.Add('the pre-launch session snapshot is missing') }
        elseif ($before.Contains($sessionId)) { $problems.Add('the session already existed before the launch') }
        if ($tree.Length -ge 8 -and $info.title -ne "$Name $($tree.Substring(0, 8))") { $problems.Add("unexpected title '$($info.title)'") }
        $expectedDirectory = [IO.Path]::GetFullPath($target).TrimEnd('\', '/')
        if (-not $info.location.directory -or [IO.Path]::GetFullPath([string]$info.location.directory).TrimEnd('\', '/') -ne $expectedDirectory) { $problems.Add("unexpected directory '$($info.location.directory)'") }
        $modelName, $variant = $Model.Split('#')
        $provider, $modelId = $modelName.Split('/', 2)
        if ($info.agent -ne 'reviewer') { $problems.Add("unexpected agent '$($info.agent)'") }
        if ($info.model.providerID -ne $provider -or $info.model.id -ne $modelId -or ($variant -and $info.model.variant -ne $variant)) { $problems.Add("unexpected model '$($info.model.providerID)/$($info.model.id)#$($info.model.variant)'") }
        $launchText = Get-Content -LiteralPath (Artifact '-launch.txt') -Raw -ErrorAction SilentlyContinue
        if (-not $launchText -or -not $info.time.created) { $problems.Add('the launch time or the session creation time is missing') }
        else {
            $launched = [int64]$launchText.Trim()
            if ($launched -lt 100000000000) { $launched *= 1000 }   # seconds written by older runs
            if ([int64]$info.time.created -lt $launched - 5000) { $problems.Add('the session was created before the launch') }
        }
        $assistant, $text = Get-FinalText $export
        # `session export` is available while OpenCode is still executing tools. In that state the
        # outcome and last assistant message are deliberately incomplete; it is not a failed review.
        # Return the documented polling result so callers retain this session instead of starting a
        # second reviewer for the same tree.
        if ([string]::IsNullOrWhiteSpace([string]$info.outcome) -and
            (-not $assistant -or $assistant.finish -ne 'stop' -or -not $text)) {
            $exitText = Get-Content -LiteralPath (Artifact '-review-exit.txt') -Raw -ErrorAction SilentlyContinue
            # Launch writes this receipt only after its foreground OpenCode process has returned.
            # An export can legitimately expose a live tool-call session before then; that is a
            # polling state, not an unusable review.  In particular, never turn it into exit 4,
            # since doing so tempts callers to launch a second reviewer for the same tree.
            if ([string]::IsNullOrWhiteSpace([string]$exitText)) {
                Write-Host "The review is still running ($([DateTime]::Now.ToString('HH:mm:ss'))); its exit receipt is not written yet. Run Read again; do not start another review."
                exit 3
            }
            $reviewExit = 0
            if (-not [int]::TryParse([string]$exitText, [ref]$reviewExit)) {
                $problems.Add('the recorded review exit status is missing or invalid')
            }
            elseif ($reviewExit -ne 0) {
                $problems.Add("the review command exited $reviewExit before a terminal verdict")
            }
            if ($problems.Count -eq 0) {
                Write-Host "The review is still running ($([DateTime]::Now.ToString('HH:mm:ss'))). Run Read again; do not start another review."
                exit 3
            }
        }
        if ($info.outcome -ne 'succeeded') { $problems.Add("session outcome '$($info.outcome)'") }
        if (-not $assistant -or $assistant.finish -ne 'stop' -or -not $text) { $problems.Add('the last assistant message did not finish normally') }
        $text = ([string]$text) -replace "`r`n", "`n"
        Set-Content -LiteralPath (Artifact '-final.txt') -Value $text

        $approved = ([regex]::Matches([string]$text, '(?m)^\s*VERDICT:\s*APPROVED\s*$')).Count
        $changes = ([regex]::Matches([string]$text, '(?m)^\s*VERDICT:\s*CHANGES_REQUESTED\s*$')).Count
        $verdict = if ($approved -gt 0 -and $changes -eq 0) { 'APPROVED' } elseif ($changes -gt 0 -and $approved -eq 0) { 'CHANGES_REQUESTED' } else { $null }
        if (-not $verdict) { $problems.Add('no verdict, or contradictory verdicts') }

        if ($verdict -eq 'APPROVED') {
            $begin = ([string]$text).IndexOf("COMMIT_MESSAGE_BEGIN`n"); $end = ([string]$text).IndexOf("`nCOMMIT_MESSAGE_END")
            if ($begin -lt 0 -or $end -le $begin) { $problems.Add('an approval without a COMMIT_MESSAGE_BEGIN/END block') }
            else {
                $message = ([string]$text).Substring($begin + 'COMMIT_MESSAGE_BEGIN'.Length + 1, $end - $begin - 'COMMIT_MESSAGE_BEGIN'.Length - 1)
                [IO.File]::WriteAllText((Artifact '-message.txt'), $message.TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
            }
            $now = try { Get-StagedTree } catch { "unavailable ($($_.Exception.Message))" }
            if ($now -ne $tree) { $problems.Add("the staged tree is $now, not the reviewed tree $tree") }
        }

        Write-Host "$($info.title) | $($info.agent) $($info.model.providerID)/$($info.model.id)#$($info.model.variant) | $($info.location.directory) | $($info.outcome)"
        Write-Host '----- reviewer final message -----'
        Write-Host $text
        Write-Host '----------------------------------'
        if ($problems.Count -gt 0) {
            Remove-Item -LiteralPath (Artifact '-message.txt'), (Artifact '-approved-tree.txt') -Force -ErrorAction SilentlyContinue
            Write-Host ('UNUSABLE: ' + ($problems -join '; ')); exit 4
        }
        if ($verdict -eq 'APPROVED') { Set-Content -LiteralPath (Artifact '-approved-tree.txt') -Value $tree -NoNewline }
        if ($verdict -eq 'CHANGES_REQUESTED') {
            Remove-Item -LiteralPath (Artifact '-message.txt'), (Artifact '-approved-tree.txt') -Force -ErrorAction SilentlyContinue
            Write-Host 'CHANGES_REQUESTED (terminal for this tree). Fix the findings, stage the new tree and review it from scratch; do not retry.'; exit 2 }
        Write-Host "APPROVED for tree $tree. Message: $(Artifact '-message.txt'). Next: pwsh build/opencode-review.ps1 -Action Commit -Name $Name"
        exit 0
    }

    'Commit' {
        $messageFile = Artifact '-message.txt'
        if (-not (Test-Path -LiteralPath $messageFile)) { throw "No approved review message for $Name (Read must report APPROVED first)." }
        $tree = (Get-Content -LiteralPath (Artifact '-tree.txt') -Raw).Trim()
        $approved = (Get-Content -LiteralPath (Artifact '-approved-tree.txt') -Raw -ErrorAction SilentlyContinue)
        if (-not $approved -or $approved.Trim() -ne $tree) { throw "Read has not approved tree $tree for $Name; run Launch and Read for this staged tree first." }
        $current = Get-StagedTree
        if ($current -ne $tree) { Write-Host "The staged tree is $current, not the reviewed $tree. Approval is void; nothing was committed."; exit 4 }
        $instruction = "The files are already staged (tree $tree; run git write-tree and stop if it differs). Run only: git status --short, git commit -F `"$messageFile`", git log --oneline -1, git status --short. Do not stage or change anything else."
        Write-Host "Committing tree $tree in $target (stdout: $(Artifact '-commit.json'), log: $(Artifact '-commit.log'))"
        Push-Location $target
        try {
            Invoke-OpenCodeRun @('run', '--format', 'json', '--print-logs', '--log-level', 'info', '--agent', 'build', '-m', $Model, '--title', "$Name-commit $($tree.Substring(0, 8))", $instruction) > (Artifact '-commit.json') 2> (Artifact '-commit.log')
            $exit = $LASTEXITCODE
        } finally { Pop-Location }
        Write-Host "opencode exit code: $exit"
        $headTree = Invoke-Git @('rev-parse', 'HEAD^{tree}')
        Write-Host (Invoke-Git @('log', '--oneline', '-1'))
        if ($headTree -ne $tree) { Write-Host "HEAD's tree is $headTree, not the reviewed ${tree}: the commit does not match the review. Report it; do not amend."; exit 4 }
        Write-Host 'Committed: HEAD has the reviewed tree. Check the message with: git show --stat HEAD'
        exit 0
    }
}
