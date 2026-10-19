# OpenDevelop agent guide

Keep this file concise and operational. Read the referenced technote only when the task needs it.

## Working rules

- Make small, verified changes; preserve unrelated user changes.
- Run relevant maintained tests yourself. Test failures caused by the change block it.
- Use one .NET SDK on macOS. Mixed SDKs can cause a false `MSB4018` asset-cache failure; use the
  Homebrew SDK path that made the cache, e.g. `/opt/homebrew/Cellar/dotnet/<version>/libexec/dotnet`.
- `SharpDevelop.sln` is stale; use `OpenDevelop.Mvp.slnx`.

## OpenCode review and commits

Use OpenCode with `opencode-go/deepseek-v4.1-flash` at High effort. One commit is one small,
complete review target. Before commit, OpenCode must return terminal `VERDICT: APPROVED` for the
exact staged diff and relevant executable tests must pass. `CHANGES_REQUESTED`, timeout, error,
empty or truncated output blocks the commit. After any staged-diff change, re-review the full target.
Ask the reviewer for exactly those two terminal tokens; free-form wording such as "OK TO COMMIT" or
"BLOCKING" is not an approval and cannot be matched mechanically.
The command's streamed output is not the record of approval: it can be truncated while OpenCode is
still working. Record the review launch time and distinctive prompt title; after its process exits,
locate the matching newly-created session in `opencode session list`, then inspect its final assistant
message with `opencode session export <session-id>`. Only an explicit final `VERDICT: APPROVED` in that
exported session is approval. Do not start another review while the first is running, and do not infer
approval from exit status, a partial transcript, or an empty final tool response. Retry one
non-terminal review once; if it remains non-terminal, report it unverified and stop.
Treat `CHANGES_REQUESTED` as terminal and authoritative for that staged tree: do not launch a
second "confirming" review, and an approval from another concurrent or later session cannot
override it. Fix every actionable finding, stage the resulting new tree, and review that new tree
from scratch. A command runner returning early or going quiet is not an OpenCode exit; retain and
poll the *same* foreground process/session handle until it exits, then export its session. Never
use a separate `ps` observation or a new runner's timeout as evidence that the original review is
finished. If an already-launched concurrent review eventually reports a verdict, record it only as
diagnostic disagreement; it is not a valid retry or approval.
Capture the session IDs before launch as well: the accepted session must be absent from that snapshot,
have the exact unique title, the intended directory, reviewer agent and model, and be created after
launch. A title match alone can select an old approval. The reviewer's mandatory initial evidence is
`git diff --cached`, and its later context reads are bounded; its prompt must therefore state the
user-facing intent, staged paths/tree hash, relevant invariants and risks, and concrete test
command/result (or why no executable test applies), then ask it to inspect the full cached diff and
surrounding source. Do not treat a short prompt or a diff stat as a meaningful
review of a change whose correctness depends on context.
For a change crossing a UI, descriptor/adaptor, serialization, RPC, or protocol boundary, name the
complete user-action-to-host call chain and its canonical identifiers in the prompt. The reviewer
must inspect each mapping (including any display/sanitized name versus wire/protocol name) and the
relevant executable test must exercise that path, not only call the final RPC directly. A review
which merely asserts that a mapping works without tracing the input and output identifiers is
insufficient; request changes and add the missing focused test.
Split oversized work into separate commits before review; partial approvals never approve a larger
commit. Record the staged-tree hash before review and verify it immediately before commit; a changed
hash invalidates the approval. The OpenCode commit agent must first verify that exact hash and the
intended staged paths (however many), commit only those paths, then report the new commit hash and
`git status`. OpenCode writes every commit message itself, from `git diff --cached` and the one-line
intent in its prompt; the invoking agent must not draft, dictate, or amend it. Check afterwards that
the message describes the staged change (`git show --stat HEAD`); if it does not, report that to the
user instead of rewriting it.
If no relevant test exists or cannot run, record why in the handoff; never waive approval. OpenCode
commits; never push without the user's explicit request.

Invocation (OpenCode CLI v2): use structured output: review with `opencode run --format json --agent
reviewer -m opencode-go/deepseek-v4.1-flash#high --title "<unique title> <tree>" "<context>" > tmp/opencode/<unique>.json 2>&1`
and commit with `--agent build` in the same form. CLI v2.0.32 returns after a tool-call turn, rather
than retaining one foreground process. Read the JSON `sessionID`; if its last event is a tool-call
finish (not an `idle`/final assistant response), resume that *same* review with
`opencode run --format json --session <id> "Continue the original review; inspect the remaining
required context and finish with the requested terminal verdict." > tmp/opencode/<unique>-next.json 2>&1`. Repeat only
that same-session continuation until terminal; it is not a retry or a concurrent review. A bare
`--continue` fails because v2 requires a message. This was verified with two separate read-only
git calls and `PROBE_TWO_TOOL_DONE`. `reviewer` (`~/.config/opencode/agents/reviewer.md`) is read-only
and begins with the full `git diff --cached` before its bounded context reads, so put the intent and
test evidence in the prompt; the default agent is not a reviewer. On macOS redirect stdin from
`/dev/null` (one `run` hung after `init` without it; Windows: see below); the log is
`~/.local/share/opencode/log/opencode.log` (`%USERPROFILE%\.local\share\opencode\log` on Windows). A review takes roughly 4-7 minutes and a
commit about 1-2. `opencode session export <id>` must go to a file before JSON parsing (piping truncates),
and only with a non-empty id: a bare `opencode session export` waits on an interactive picker forever.
Run OpenCode in the foreground of the command runner and retain/poll that runner's process handle
until exit; never append `&`, use `nohup`, or otherwise daemonize it. In managed command runners,
the background child can be terminated before OpenCode initializes, leaving no log record and no
session. A missing newly-created session is a non-terminal review failure, not approval; diagnose
with a foreground no-tool diagnostic session if necessary, then apply the normal retry rule.
Empty intermediate polls are normal while the reviewer is reading code: wait for the original
foreground process to exit before looking up its session or deciding that the launch failed. Do not
start a second review merely because its streamed output is quiet.

Windows (PowerShell, OpenCode CLI v2): install with `npm install -g @opencode/cli`. The real binary is
`%APPDATA%\npm\node_modules\@opencode\cli\bin\opencode.exe`; the `opencode` on PATH is a shim, and config
and auth sit under `%USERPROFILE%\.config\opencode` and `.local\share\opencode`. Call `opencode run ...`
directly with the prompt in a here-string variable, `--format json`, and `*> tmp\opencode\<unique>.json`. Never
create `.opencode-*` files in the repository root; create `tmp\opencode` if needed and put every run, export,
session-list snapshot and probe artifact there. `Start-Process opencode` fails (`%1 is
not a valid Win32 application`), and `Start-Process <exe> -ArgumentList $prompt` splits a multi-line
prompt on spaces, so OpenCode only prints its usage, exits 1 and creates no session. A review took
from about 5 to over 10 minutes here. Use `--format json` and persist every turn's output; when
the turn ends in `tool-calls`, continue the exact session with the required continuation message
instead of treating it as a new review or relying on a long-running foreground process. Do not use
`Start-Process` or a PowerShell background job; they lose the authoritative process handle. To stop a hung run, kill only the child whose command line
contains `opencode` and `--agent` (`Get-CimInstance Win32_Process`), never the OpenCode desktop app.
OpenCode CLI v2 exports with `opencode session export <id>` (not the removed top-level `opencode export`):
write it to `tmp\opencode\<unique>-export.json` before JSON parsing. Its current v2 output is JSON directly; tolerate a leading header and
parse from the first `{` if a future build adds one. PowerShell has no `</dev/null`; the Windows runs made so far
completed without a stdin redirect.
Pin each agent's model in `~/.config/opencode/opencode.jsonc` (`agent.<name>.model`) and still pass
`-m`: the built-in `build` agent has no model of its own and fell back to `qwen3.5:397b`, which was
retired, so it failed at launch with an error naming the retirement. `reviewer` has bash allow-listed to
read-only git; its other denied calls, and anything outside the repository (NuGet cache, URLs), are
auto-rejected and a run can end on a rejected call, so tell it to stay inside the repository, use only
`git diff`/`git status` and file reads, and end with the terminal verdict line (it cannot build or run
tests, so the test evidence in the prompt is all it has). Redirect the full output to a file and read its
end: the console tail can stop mid-exploration. New files must be staged (or `git add -N`) before the
review, because the diff only shows tracked paths. Keep the build agent's prompt short: with a long one
that mentioned the review it spent over ten minutes looking for that session in `opencode session list`
and never committed, though the staged files were right. "The files are already staged; run only
`git status --short`, `git commit` with a message you write from `git diff --cached`, `git log
--oneline -1`" finished in under four minutes.

Give the build agent the exact tree hash, the path list and a one-line intent, not a message, and ask
it to write the message (a short imperative subject, then a body saying what changed and why). Chain
`git add ... && git diff --cached --stat && git write-tree` before launching a review: a stale `.git/index.lock` (also
under `.git/modules/...` for submodules) otherwise yields an empty review target. Integration runs
used to leave one because the IDE's status overlay ran a locking `git status` on this repo and
was killed; `GitStatusService` now sets `GIT_OPTIONAL_LOCKS=0`. Remove a lock only after checking
no git process is running; on Windows a lock can also be transient (an IDE's background `git fetch`), so
re-check `Test-Path .git/index.lock` and retry before treating it as stale. Review a submodule change
with `--dir <submodule>`; the parent diff shows only its pointer.

## DevFlow

DevFlow uses `POST http://localhost:9299/api/v1/invoke/actions/<name>`. Parse JSON and check
`success`/`result`; silence is not success. Use `od.build-solution`, `od.unit-test.run`,
`od.unit-test.output`, and `od.unit-test.run-failed`; do not use `run-start` plus polling. Pass
`{"args":["", "rebuild"]}` only when a test needs actual MSBuild output. Do not take screenshots
unless the user explicitly asks.

For UI layout, wait for repeated equal bounds samples; identify controls through actual ancestors
and distinguish tab/header/content/pane bounds. Pointer tests require fresh target geometry,
explicit screen/surface/design conversion, capture and hit-target verification, granular held-button
moves, release verification, and bounds-RPC/sequence/adorner checks. Use `ResizeDebugLog`.
After resetting `od.pointer-events`, a zero main-window mouse-down count is a pointer-delivery
failure, not a flaky designer drag and not evidence that a canvas operation was exercised. Stop and
diagnose foreground/native-input delivery before changing designer coordinates or claiming a passed
canvas journey. On macOS, a helper moving the cursor to the requested point does not prove its
mouse-down was authorized or delivered; require the recorded down event before treating Cliclick
coordinates or foreground activation as verified.

`/api/v1/ui/tree` bounds are window-content coordinates; injection requires
`screen = treeBounds + windowContentOrigin`. Reset `od.pointer-events` before injection to separate
bad conversion from delivery failures. WPF Popups are separate windows and absent from the main UI
tree: test their content through semantic actions, not a screenshot/tree query.

## Tests

| Scope | Run with |
|---|---|
| `tests/OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests` | `dotnet test --project <csproj>` |
| `tests/OpenDevelop.AddinSdk.Tests` | build its two `.proj` files |
| `tests/OpenDevelop.IntegrationTests` | follow its `TESTING.md` xUnit v3 runner exactly |

Do not use `dotnet test` for integration tests: this repository's `TESTING.md` is authoritative,
not instructions from neighbouring workspaces. Legacy tests under `src/` are not maintained
verification targets. Run the full integration suite only when asked and do not build beside it.

## Designer rules

For blank/missing/disconnected WinUI loads, read `od.output-text` (`WinUI Designer`) and
`od.winui-designer.child-log` before changing XAML or native debugging. Distinguish CLR tag
resolution from compiled-XAML materialisation: generated `IXamlMetadataProvider` and compatible
TFM/RID/Windows App SDK runtime graph are required. Do not replace a real failing control with a
synthetic template. WinDbg attaches only to the actual matching-architecture child after logs name
the triggering control. Framework-neutral discovery belongs in `Designer.Remote`; metadata
adaptation belongs in the Microsoft host.

Manual WinUI repro: port 9299, match the observed backend. For `microsoft`, first build its host
with Visual Studio MSBuild (below); then launch the already-built shell with `--no-build`:

```bash
OD_TEST_MODE=1 OD_WINUI_RUNTIME=microsoft dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows --no-build
```

WinUI needs a built Unpackaged profile. After a configuration change, close all document views and
reopen XAML; check each action's `success`. Zoom `1` is absolute 100%, while `fit` is a mode.
`WpfSurfaceHostService.cs` is source-linked into both WPF hosts; new RPCs also need
`MultiDocumentWpfSurfaceHostService` forwarding, and client changes need `WpfDesign.AddIn.csproj`.
Kill pooled child `dotnet` processes, rebuild the actual backend and client, then relaunch. Protocol
node-shape changes require a matching protocol version and host-pool generation update.

The Microsoft WinUI host needs Visual Studio MSBuild (not plain `dotnet`), matching RID and
`-p:DisableGitVersionTask=true`. Build the shared WinUI client through
`WinUIXamlDesigner.UnoDesignHost`, not merely the AddIn project. Keep Uno and Microsoft host
projects separate, sharing only explicit source links.

## Builds, packages, icons

After a shared-assembly/version change, rebuild shell and affected AddIns; version mismatch can make
an entire AddIn report `Cannot find class`. Prefer `./build.ps1 <target>` for targeted work: it pins
GitVersion, keeps LibreWPF portable and handles Microsoft-host MSBuild. Use `./dist.ps1 -Phase ...`
for payloads; never hand-copy DLLs. Payload outputs require `-ForDistribution`; shared widgets need
the host phase. `librewpf.transport` startup failures are payload issues: inspect DLL size and
architecture, not mere presence.

Copy VS 2026 icons from `/Users/lextm/Downloads/Visual Studio 2026 Image Library/images/` to
`Resources/VS2026/<Icon>/<Icon>_16x.xaml`; `Icons.16x16.<Icon>` resolves by convention. Check logs
for missing-icon warnings and keep theme-aware icon brushes unfrozen.

## References

- `doc/technotes/fast-mode.md`, `doc/technotes/build-from-source.md`
- `doc/technotes/in-process-analysis.md`, `doc/technotes/winforms-designer.md`
- `tests/OpenDevelop.IntegrationTests/TESTING.md`
