# OpenDevelop agent guide

Keep this file concise and operational. Read the referenced technote only when the task needs it.

## Working rules

- Make small, verified changes; preserve unrelated user changes.
- Run relevant maintained tests yourself. Test failures caused by the change block it.
- Use one .NET SDK on macOS. Mixed SDKs can cause a false `MSB4018` asset-cache failure; use the
  Homebrew SDK path that made the cache, e.g. `/opt/homebrew/Cellar/dotnet/<version>/libexec/dotnet`.
- `SharpDevelop.sln` is stale; use `OpenDevelop.Mvp.slnx`.
- LibreWPF source lives in the sibling repo `../openavalon` (not in this repo); look there for
  window/ProGPU behaviour, e.g. first-window init and show timing. OpenDevelop consumes it only as
  NuGet packages from the local feed `../openavalon/artifacts/local-feed`, so a LibreWPF change is
  invisible until it is published to the feed (or overlaid). **`../openavalon/AGENTS.md` is
  authoritative** for versions, lanes, incremental receipts and `dev-overlay.sh` (one-assembly
  inner loop, no feed); `doc/technotes/librewpf.md` is consumer-side background and may lag it.
  Publish with `./repack-librewpf.sh` (wraps `dist.local.sh`); never hand-pack or hand-copy DLLs.

## OpenCode review and commits

**Skip this whole section when you are OpenCode yourself.** If the agent committing the change runs
inside OpenCode (its tools are OpenCode's and its model is an `opencode-go/*` one), the review below
would be that same agent and model checking its own diff, which adds nothing: commit directly. The
rules exist for the other agents (Claude Code and friends), whose change needs a reviewer they did not
run. Everything else still applies to whoever commits, OpenCode included: verify with the relevant
tests, stage exactly the intended paths, commit only those, write the message from `git diff --cached`,
and never push without the user's explicit request.

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
`git status`. The commit message is written by the *reviewer*, in the same review that approves: the
review prompt asks it, only when it approves, to put the complete message (short imperative subject,
blank line, body saying what changed and why, then any trailer line the invoking agent was told to
include) between the lines `COMMIT_MESSAGE_BEGIN` and `COMMIT_MESSAGE_END`, immediately before the
terminal `VERDICT: APPROVED`; it gives no message on `CHANGES_REQUESTED`. The invoking agent must not
draft, dictate, or amend it: it extracts that block verbatim from the exported approved session into
`tmp\opencode\<unique>-message.txt`, and the build agent commits with `git commit -F` on that file.
Check afterwards that the message describes the staged change (`git show --stat HEAD`); if it does not,
report that to the user instead of rewriting it.
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

Progress must be observable while a review or commit agent runs, because `--format json` redirected to a
file stays empty until the process exits (observed on every run; a 40-minute review looks identical to a
hung one). So, for every OpenCode run:
- Pass `--print-logs --log-level info` and send stderr into a *separate, live* file
  (`2> tmp/opencode/<unique>.log`, stdout still to `<unique>.json`). Stderr is not block-buffered the way a
  redirected stdout is, so this file is the progress log the user can tail. (Not yet verified on a long run;
  if `<unique>.log` is still empty after two minutes, say so and fall back to the next point.)
- Independently, the background service keeps writing to `~/.local/share/opencode/log/opencode.log`
  (`%USERPROFILE%\.local\share\opencode\log\opencode.log`). While the run is going, check it every few
  minutes for `level=ERROR` lines newer than the launch time and report them. Upstream failures show up
  only there and make a run hang silently: `Failed to drain Session` with `Insufficient account funds` or
  `Endpoint is unavailable` (both seen). Such a run will never produce a verdict; stop it (kill only the
  `opencode.exe run` child) and report the provider error instead of waiting out the timeout.
- Tell the user, at launch, the exact paths of the `.json` and `.log` files, and each time they ask for
  progress report the last log line and its time, not just "still running".
- Set a hard `timeout` on the command (40 minutes for a review, 15 for a commit), and treat expiry as a
  non-terminal run, not as a verdict.

The split redirection above replaces the `> <unique>.json 2>&1` and `*> <unique>.json` forms in the
Invocation and Windows paragraphs below: keep those templates, but send stderr to the `.log` file instead
of merging it into the `.json`.

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
`git status --short`, `git commit -F <message file>`, `git log
--oneline -1`" finished in under four minutes.

Give the build agent the exact tree hash, the path list and the message file path (the reviewer wrote
the message; the build agent does not write one), and ask it to run `git commit -F <file>`. Chain
`git add ... && git diff --cached --stat && git write-tree` before launching a review: a stale `.git/index.lock` (also
under `.git/modules/...` for submodules) otherwise yields an empty review target. Integration runs
used to leave one because the IDE's status overlay ran a locking `git status` on this repo and
was killed; `GitStatusService` now sets `GIT_OPTIONAL_LOCKS=0`. Remove a lock only after checking
no git process is running; on Windows a lock can also be transient (an IDE's background `git fetch`), so
re-check `Test-Path .git/index.lock` and retry before treating it as stale. Review a submodule change
with `--dir <submodule>`; the parent diff shows only its pointer.

An index lock is a staging-integrity preflight, not a review finding. Before launching a reviewer,
confirm there is no active Git process and successfully run `git diff --cached --check` and
`git write-tree`; record that resulting tree hash in the review prompt. Once those checks succeed,
the reviewer must inspect that exact cached tree and must not reject or defer the content review
because it observes a transient or stale `.git/index.lock`. If the preflight cannot produce a tree
hash, resolve the lock under the rules above and restart the preflight; do not let an empty or
locked diff become an approval.

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
| `tests/OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests`, `OpenDevelop.Profiler.Tests` | `dotnet test --project <csproj> -c Debug` |
| `tests/OpenDevelop.AddinSdk.Tests` | build its two `.proj` files |
| `tests/OpenDevelop.IntegrationTests` | follow its `TESTING.md` xUnit v3 runner exactly |

The `dotnet test --project` suites run on xUnit v3, whose Microsoft.Testing.Platform host parses its
own arguments and does not accept VSTest's. `--nologo` stops the run before a single test executes:
the host prints `Unknown option '--nologo'`, the summary reads "Zero tests ran" with `error: 1`, and the
exit code is 5 - easy to misread as a build failure. A genuine test failure exits 2; 5 means no test
ever ran. Always check the summary says "Passed!" with a non-zero `total`. Pass runner options after `--`
(`dotnet test --project <csproj> -c Debug -- --filter-method '*Name*'`); `--filter` is VSTest syntax
and belongs nowhere in this repository.

Do not use `dotnet test` for integration tests: this repository's `TESTING.md` is authoritative,
not instructions from neighbouring workspaces. Legacy tests under `src/` are not maintained
verification targets. Run the full integration suite only when asked and do not build beside it.

## CI (GitHub Actions)

One workflow, `.github/workflows/ci.yml`, driven by `.github/ci/test-plan.json`; the scripts that read it are in
`.github/ci/`. `doc/technotes/ci-test-plan.md` explains the shape. Job names: `Plan`, `Build (<platform>)`,
`<lane> (<platform>)`, and `<lane> (<platform>, in place)` for the one lane with `buildsInLane` (today `unit`).

Spending runs:

- **A push to `master` is the only automatic trigger** (plus PRs), so a diagnostic iteration does not need one: push a
  topic branch (`git push origin HEAD:refs/heads/ci-probe`), dispatch against it
  (`gh workflow run ci.yml --ref ci-probe -f diagnose=true`) and delete the branch afterwards. Never push to `master` and
  also dispatch the same commit - that runs the same thing twice, which happened twice on 2026-10-25 and cost about 28
  runner minutes.
- **One hypothesis, one run.** Batch related edits, read the result, then act. Cancel a superseded run
  (`gh run cancel <id>`) instead of letting it finish.
- Read the lane's job summary table (`| Entry | Mode | Result | Seconds | Tests |`), not the console tail. `observe`
  entries do not fail the job by themselves, but `run-test-plan.ps1` now fails an `xunit-exe` entry that reports no test
  total or 0 tests, and fails a lane in which every entry failed - so a green lane does mean it ran something.
- The temporary host diagnostics only run with the `diagnose` input; use it when the question is how the IDE host
  starts, and remove the step once the host is fixed.

Layout, so a change goes in the right place:

- The build job compiles the archive lanes once and uploads the workspace (~2.5 GiB, 1-day retention). The in-place lane
  checks out and compiles only its own entries, which is why its result arrives in ~9 minutes instead of behind the pack
  step; `build-binaries.ps1 -Lane <lane> -SkipHostBuilds` is what it runs, and the build job skips that lane's projects
  with `-ArchiveLanesOnly`.
- The NuGet package folder is one `actions/cache` entry keyed by `Directory.Packages.props`, `global.json` and
  `NuGet.config`; the build job computes it and the lanes restore that key. Never ship it as an artifact again - it was
  3.3 GiB and eight minutes per lane.
- `dotnet build` of `src/Main/SharpDevelop/SharpDevelop.csproj` must happen **once** per output folder: a second build
  deletes the host's mutable package outputs (`_ProGpuWpfSdkClearMutablePackageOutputs`) and does not put them back, so
  the folder ends up without `PresentationFramework.dll`. Measured on the runner: 193 DLLs present after the first build,
  173 and missing after a rebuild.
- Known open defect (2026-10-25): the IDE host still fails to start on the Windows runner with
  `FileNotFoundException: PresentationFramework, Version=11.0.0.0`. The file is in the folder and loads by path, but the
  deps manifest records it as a package-relative asset (`lib/net10.0/PresentationFramework.dll`) and the build produces no
  `runtimeconfig.dev.json`, so the host's `TRUSTED_PLATFORM_ASSEMBLIES` has no `PresentationFramework` and a 10.x
  `WindowsBase`. macOS is unaffected. Investigate probing paths or a deps `reference` layout before changing the lane.

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

### Product branding (icon, splash, DMG)

`images/opendevelop.png` (square master, keep it square) is referenced by no code — it is only the
source for the two generated icon binaries. `images/social.png` (2:1 master) has two consumers at
once and must stay exactly 2:1. Replacing artwork means regenerating all four binaries together
(commit `00da94b8c0` "Switch to new logo" is the precedent). The two icons are generated by
`build/make-branding-assets.sh`, which needs macOS `sips`/`iconutil` and `python3` with Pillow; it
refuses a non-square master (`OD_BRANDING_ALLOW_NON_SQUARE=1` overrides) and warns when `social.png`
is not 2:1:

| Artifact | Consumed by |
|---|---|
| `build/windows/opendevelop.ico` | `SharpDevelop.csproj:30` `ApplicationIcon` (the PE icon), `:90` embedded as `Resources\opendevelop.ico` and set explicitly by `Workbench/WpfWorkbench.xaml:10` (the *running* window icon), and `OpenDevelop.Launcher.csproj:44` (the released `OpenDevelop.exe`/`OpenDevelopARM64.exe`, published by `dist.ps1` `Build-Launchers`) |
| `build/macos/opendevelop.icns` | `build/macos/Info.plist` `CFBundleIconFile`, copied into the bundle by `build/macos/build-application-bundle.sh:53` |
| `images/social.png` | the splash artwork (`SharpDevelop.csproj:93` embeds it as `Startup\social.png`, drawn 640x320 `Stretch=Uniform` by `Startup/SplashScreenForm.xaml:29`) **and** the DMG window background (`build/macos/build-dmg.sh:12`) |

```bash
./build/make-branding-assets.sh          # writes the .icns and the multi-size .ico (16..256)
```

Both outputs are then rebuild-time inputs: `./build.ps1 shell` re-embeds the `.ico` and the splash,
and `./dist.ps1` (`Invoke-MacPayload`, `Invoke-MacArchive`) refreshes `OpenDevelop.app` and the `.dmg`.
`OpenDevelop.app/` is build output — never edit it by hand. `build-dmg.sh` places the app icon at 10%
width and the Applications icon at 30%, both at 80% height (clamped by `bottom_inset=60`), inside the
light left panel of `social.png` — that panel is what keeps the two icons, the drag arrow between them
and Finder's black labels readable. The spacing is deliberate: 15%/25% (the SharpDevelop-era layout)
left 8px between the two 84px slots, while 10%/30% leaves ~100px for the arrow that
`build/macos/draw-dmg-arrow.py` draws once the background has reached window size (solid near-black
block; it warns and leaves the image alone rather than drawing a sub-40px dash, and `icon_size=84` is
shared with Finder's `set icon size`). Nothing enforces the light panel boundary, so it stays a
contract on the artwork: the current one turns dark at 38% width, which still contains 10%-30% and the
arrow. Icons have no `<SplashScreen>` item: the splash is a real WPF window shown by
`SharpDevelopMain.RunWithSplash`, suppressed by `-nologo`/`OD_TEST_MODE=1`.

The About box draws a text `TextBlock` "OpenDevelop" (`AboutDialog.xaml:30-34`), not an image, so new
artwork is invisible there unless that is changed. Legacy SharpDevelop art is dead in-tree and must
not be mistaken for live assets: `src/Main/SharpDevelop/Resources/{SplashScreen.jpg,SharpDevelop.ico}`,
the excluded `Startup/SplashScreen.cs`, `src/Setup/**` WiX and `.tgitconfig`. There is no Linux
`.desktop`, MSIX/`.appxmanifest` or VS Code extension icon.

## References

- `doc/technotes/fast-mode.md`, `doc/technotes/build-from-source.md`
- `doc/technotes/librewpf.md` (LibreWPF local feed, repack, stale-cache traps)
- `doc/technotes/in-process-analysis.md`, `doc/technotes/winforms-designer.md`
- `tests/OpenDevelop.IntegrationTests/TESTING.md`
- `../AGENTS.md` — the external add-in checkouts (CoreWF, Stride, MAUI-Designer), how to locate
  them by their `.addin` manifests, and how their integration suites are run against this repo
