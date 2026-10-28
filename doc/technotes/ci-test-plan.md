# CI test plan

Goal: broaden GitHub Actions coverage of locally runnable tests on Windows x64, Windows ARM64 and macOS ARM64.
Introduce substantial groups in observe mode, inspect their actual runner results, and promote stable entries to
required per platform. Linux product tests remain out of scope for this rollout.

## Current status (2026-10-28, 14:12 UTC)

The checked-in plan uses batch 4 and `focus: true`. The numbers below count enabled **entry/platform pairs**,
not individual test cases. They describe the complete plan, not what a focus-mode run actually executes:

| Platform | Required | Observe | Default focus run |
| --- | ---: | ---: | --- |
| Windows x64 | 32 | 0 | No test entries; its build can still supply Windows ARM64. |
| Windows ARM64 | 25 | 2 | Only the two observed entries run. |
| macOS ARM64 | 26 | 5 | Only the five observed entries run. |

**Focus mode skips every required entry. A green focus run does not prove that required coverage passed.** Use
`workflow_dispatch` with `full=true` to run both statuses before relying on the complete gate. Its `platforms` input
can select a comma-separated subset; an ARM64-only Windows run still builds the shared Windows x64 archive. A full run
also needs all relevant platforms selected to validate the whole matrix. The plan is the source of truth for individual
entries and per-platform statuses; counts above do not imply distinct tests, since a class can contain many cases.

Latest checked state: the push run for `f405f23b06` ([40702681819](https://github.com/lextudio/OpenDevelop/actions/runs/40702681819))
was cancelled, so it did not validate that commit. The manual Windows ARM64 focus run for its parent `ddd19ef9ec`
([40701241603](https://github.com/lextudio/OpenDevelop/actions/runs/40701241603)) was in progress at the timestamp above:
Plan and the Windows x64 build had passed; the ARM64 integration jobs had not finished. It does not cover the later
WSL-watchdog change or establish a full-matrix result. Recheck the run before using its conclusion.

The latest completed, non-cancelled run in that sequence, [40680604340](https://github.com/lextudio/OpenDevelop/actions/runs/40680604340)
for `182bbc4fa6`, failed in the macOS and Windows ARM64 designer jobs. On macOS, four observed designer entries failed
and the all-failed guard made the lane red; on Windows ARM64, the one scheduled MewUI entry failed. Later commits add
input and desktop diagnostics and change watchdog handling, but they need their own hosted confirmation.

### Priorities

* Inspect current hosted run summaries, screenshots, desktop/input diagnostics and per-entry TRX; do not infer that an
  observed entry passed from a green job. On macOS investigate pointer delivery and designer interaction failures. On
  Windows ARM64 verify the WSL watchdog, foreground handling, debugger stop and MewUI editing against fresh results.
* Promote an observed entry only after the last two completed hosted runs for that platform pass it; then verify the
  promotion. Run the full matrix, including required entries, before treating the current master as fully gated.
* Continue adding missing runnable suites. `AddInTests`, Microsoft designer backends and environment-dependent SDK,
  runtime-upgrade and performance scenarios still need separate prerequisites and budgets.

## Rollout history (2026-10-26 to 2026-10-28)

The following entries describe results at the time of each run, not the current status of a lane or commit.
The workbench, debugger, coverage, ARM64 unit, new-project, profiler, reference, LibreWPF, LibreWinForms, MewUI and GTK
journeys were initially added as observations and subsequently measured on hosted runners. Designer journeys temporarily
modify `src/Samples` and restore the original files in cleanup; run them serially in an isolated checkout or archive,
because interruption can leave a sample modified. GTK needs native runtime and GIR metadata: Homebrew GTK 4 and
libadwaita on macOS, MSYS2 UCRT64 on Windows x64 and CLANGARM64 on Windows ARM64. The Windows setup checks both DLL
architectures and metadata before launch. The Windows SDK-selection theory is excluded from the new-project entry because
one row installs an SDK over the network; its other rows need a separately budgeted path.

* **First hosted run (master `69bd9a4a50`, run 40429072140) failed before any new lane ran.** Both Build jobs stopped in
  `verify-terminal-assets.ps1`: the approved LibGhostty.Net 1.0.2 sha512 was stale, because the lextudio GitHub Packages
  payload changed (it, the local cache and the local feed agree; nuget.org's same-version package differs, and
  `NuGet.config` source mapping keeps LibGhostty.Net off nuget.org). It surfaced only now because the plan edit changed the
  NuGet cache key and the old cache held the previous package. Re-record the approved hash whenever the fork republishes
  1.0.2. The same run showed the four `PortableTextCompatibilityTests` cases failing on Windows with "The calling thread
  must be STA" (xUnit v3 theories run on the MTA pool); the test body now runs on an STA thread on Windows. Both fixes are
  in `757a9803fe`.
* **Second hosted run (`757a9803fe`, run 40431285339) is the first with every new lane.** Two Build/setup failures were
  transient and passed on re-run: GitVersion's `Global\gitversion...` named mutex on macOS (first time in ~14 macOS
  builds) and `setup-dotnet` crashing with 0xC0000005 on Windows ARM64. Results by platform:
  * **Windows (x64 and ARM64): every integration entry failed in ~10 s**, because the IDE exited during start-up with
    `PlatformNotSupportedException: Portable media requires a registered portable window host`. The cause was
    `PortableTextCompatibility.Initialize` (from `f087230955`) selecting LibreWPF's portable media backend
    unconditionally; on Windows the SDK bootstrap keeps Windows MIL and registers no portable window host. Fixed in
    `980e1b6a7c`; Windows results are still to be read. The lanes looked green only because of `allowAllFailures`.
  * **macOS ARM64: most entries pass, and a failed entry is usually one or two cases out of many** (an entry fails if any
    case fails; LibreWPF editing lost 1 of 30). Remaining cases: real pointer input not delivered on the hosted runner
    (Terminal pad 3, MewUI and GTK drag-drop, LibreWPF Ctrl-click, one debugger case); the new-project dialog (3,
    `KeyNotFoundException: 'net10.0'`, the runner's SDK set differs); 17 debugger cases where debugging starts but no
    breakpoint is hit; and two designer property round trips. `vseditorcompat-tests-arm` passed 141/141 but exited 1:
    34 `[UIFact]` dispatchers each made `WeakEventTable` wait 300 ms at exit, past the xUnit v3 runner's 11 s limit;
    fixed in `a57cd78dd7` with the WPF compat switch.
  * Windows ARM64 `base-tests-arm`: two hot-reload end-to-end cases fail (WinUI ARM64 and WPF); they pass on x64.
  * Entries that passed on macOS in this run (candidates once repeated): all five workbench classes, solution folder,
    project options, T4, Task List, project reload, Razor, Add Reference, fixture isolation, Profiler journeys, WPF
    designer assembly lock, code coverage, GTK nested edit, LibreWinForms editor, and the Base, XamlDom and Profiler units.
* **Third hosted run (`980e1b6a7c`, run 40444468212): Windows integration runs for real.** Windows x64 passed all five
  workbench classes and 11/11 `integration-core` entries; Windows ARM64 passed the workbench and 9/10 core entries;
  code coverage passed everywhere; the debugger classes passed 14/14 twice on Windows x64 and lost one case each on
  Windows ARM64. `vseditorcompat-tests-arm` now exits in a second. Five jobs hit their timeout (shown as cancelled):
  both Windows designer jobs (still running correct but slow journeys), and macOS core, GTK and hot reload (macOS hot
  reload for the second run in a row; macOS core took about 7 minutes of tests in the previous run, so something hung;
  cancelled jobs keep no log). The breakpoint failures are macOS only. `integration-designer` was therefore split:
  `integration-librewpf-designer` (60 minutes) holds the two LibreWPF entries, and
  `integration-mewui-winforms-designer` (45 minutes) holds MewUI and LibreWinForms. Both were folded into
  `integration-designer` right after, when the archive lanes were merged by reliability (see "Lanes").
* **First run with the merged lanes and entry budgets (`d84d34c4e7`, run 40461019311).** 13 jobs and 341 runner-minutes,
  against 26 jobs and 590 runner-minutes (five of them cancelled at their lane timeout) on run 40444468212; the nine lane
  jobs started together instead of queueing. Every job finished and no entry reached its budget. `integration-stable`
  passed 19/19 entries on macOS ARM64 and 17/17 on Windows x64; on Windows ARM64 only `add-reference-journey` failed, for
  the second run in a row, because the Add Reference dialog never gets native focus there
  (`"nativeFocused":false,"foregrounded":false`, `AddReferenceTests.cs:81`) - a foreground limitation of that runner, the
  counterpart of macOS's undelivered pointer input. The macOS entries that used to hang ended in minutes this time
  (Hot Reload 139 s, GTK 56 s, MewUI 97 s, Terminal 94 s) with ordinary case failures. On Windows x64 the debugger
  classes, Hot Reload, MewUI and both GTK entries passed. Measured maxima against budgets: LibreWPF editing 1561 s (35
  min), debugger 472 s (10 min, the tightest), feature debugger 513 s (12), GTK 496 s (12), MewUI 427 s (10), workbench
  314 s (8), Profiler journeys 378 s (10); the slowest lane, Windows designer, took 59 of its 80 minutes.
  Promotion followed in the next change, under the rule below: the `integration-stable` entries became `required` on
  macOS ARM64 and Windows x64 (`add-reference-journey` stays `observe` on Windows ARM64) and the lane lost its
  `allowAllFailures`; the push of that change is the confirming run, since a docs-only push triggers no CI.
* **First gated run (`aafaf5b745`, run 40470967407): the promotion held.** With `integration-stable` required on macOS
  ARM64 (19 entries) and Windows x64 (17) and no `allowAllFailures`, both jobs passed, so those 36 entry/platform pairs now
  gate `master`. Every other lane job passed as well; no entry in the two Windows designer jobs reached its budget (no
  `timeout` row; LibreWPF editing took 1423 s and 1574 s of its 35 minutes). The run first
  failed in `unit` on one `profiler-tests` case, `CaptureTests.Capture_CarriesTheRuntimeCountersAlignedWithTheSamples`
  (`mode: Allocation`), whose last `gc-heap-size` point was 0 (`CaptureTests.cs:134`, `heap.Last > 0`); a re-run of that
  job passed. Three local macOS captures of the same fixture showed every `gc-heap-size` payload with `Count=1` and a
  positive mean, but none reproduced the zero, so the cause (an empty end-of-session interval included) is still open.
  It is the only `required` entry that has failed so far; if it fails again, make the test print the whole counter
  series before changing it.
* **Windows ARM64 promoted next.** On Windows ARM64 the other 18 `integration-stable` entries passed on runs
  40444468212, 40461019311 and 40470967407 (the first run had no IDE there), so they are `required` too;
  `add-reference-journey` failed on all four runs and stays `observe`. The confirming run (`8e11e823c1`, run
  40482781106) passed all 13 jobs, so 54 entry/platform pairs now gate the workflow. `profiler-tests` passed there,
  so the new `gc-heap-size` series message has not been seen yet.
* **Windows observe entries promoted (2026-10-27).** On Windows x64, `base-tests` and `xamldom-tests-microsoftwpf`
  passed runs 40495471973, 40501928801 and 40514802977; `debugger-journeys`, `debugger-feature-journeys` and
  `wpf-hot-reload-journeys` passed those three and 40524161593; `librewinforms-designer-editor`,
  `mewui-designer-journeys`, `gtk-designer-journeys` and `gtk-designer-nested-journey` passed 40495471973 and
  40524161593, the two runs that completed the designer lane. On Windows ARM64, `librewinforms-designer-editor` and
  `gtk-designer-nested-journey` passed those same two. Those 11 pairs are `required`.
  At that point focus mode left Windows x64 with only `librewpf-designer-editing` and no in-place unit job (that entry
  was promoted later the same day, below).
* **macOS debugger unblocked and promoted (2026-10-27).** Every debugger attach on the hosted macOS runner hung inside
  `ICorDebug.DebugActiveProcess`: macOS authorizes `task_for_pid` through `system.privilege.taskport`, and on the runner
  nobody answers that prompt (a developer Mac caches it, so it never reproduced locally). `ci.yml` now enables developer
  tools and allows that right in the macOS lane (`a24f882e56`). Runs 40582707365 and 40595645276 then passed
  `debugger-journeys`, `debugger-feature-journeys`, `wpf-hot-reload-journeys`, `base-tests-arm`,
  `librewinforms-designer-editor` and `gtk-designer-nested-journey` on macOS ARM64, so those 6 pairs are `required`.
* **LibreWPF editing promoted on Windows; macOS templates fixed (2026-10-27).** After `8357403143`/`11191d5759` (selection no
  longer waits on a dead or reopening WPF child) and `ee763d3e7f` (the binding tests ignore the GPU-less software fallback
  warning), `librewpf-designer-editing` passed all 30 cases on Windows x64 and ARM64 in runs 40595645276 and 40606824351,
  so both pairs are `required`. `cc569fd5a8` installs SDK templates only from the runtime-matching `templates/` folder (the
  macOS image keeps .NET 8, 9 and 10 side by side, and an older pack won); `new-project-dialog-macos` then passed in run
  40606824351 and, after passing 40615707034 too, is `required` on macOS ARM64,
  its only platform, so it moved to `integration-stable`.
* **Windows ARM64 unblocked and promoted (2026-10-28).** The windows-11-arm image logs in to the first-logon privacy
  page (screenshot in the uploaded logs), so every pointer journey saw no foreground window; the "Prepare the Windows
  desktop" step ported from AvalonDock (`32fd2eda79`, `357e9326bb`) clears it and keeps `wsl.exe` windows away. The lane
  runs the x64 build's output, whose PDBs name `D:\a\...`: `e2dc739e95` maps that path onto the ARM64 workspace, and
  `1966db2a72` builds the ARM64 WinUI Hot Reload fixture on x64 and runs the WPF one through dotnet. After that every
  ARM64 integration entry passed run 40656190581; runs 40662664664 and 40671568295, the last two completed, then passed
  `base-tests-arm`, `add-reference-journey`, `debugger-feature-journeys` and `gtk-designer-journeys` on Windows ARM64, so
  those four are `required` there. `debugger-journeys` and `mewui-designer-journeys` each failed once in 40662664664 (a
  stop racing the adapter's stdin, fixed by `c7497292df`; an items-editor save, now logged in full by `82ba0c62ce`) and
  stay `observe` for one more green run. The first three promoted entries stay in `integration-unstable` (see "Lanes").
* **Desktop diagnostics and targeted runs (2026-10-28).** macOS lanes report input readiness and TCC decisions, probe
  native click delivery and sample window geometry (`0ee0b96365`, `4db0e6098a`, `9f9947628b`, `2d89e992ab`).
  `ddd19ef9ec` adds the `platforms` dispatch input, so an unavailable platform no longer holds up other selected jobs.
  `f405f23b06` narrows the Windows watchdog to WSL update processes and attempts the ARM64 update once before starting
  it; this is intended to stop update windows taking focus without killing unrelated WSL probes, but its push run was
  cancelled and the change still needs hosted verification.
* **Next:** inspect per-entry results and durations across multiple hosted runs, fix failures by platform, and promote
  stable entries to required. Continue expanding the remaining runnable suites, prioritizing high-value integration
  journeys; record exclusions and prerequisites before scheduling environment-dependent tests.

## Shape: compile once, test in parallel lanes

`.github/workflows/ci.yml` is the one workflow. `.github/ci/test-plan.json` is the only place that decides what runs.

```
plan  ->  build (per platform)  ->  test (per platform x lane)
          compile once, upload       download, unpack, run the lane's test assemblies
      \->  in-place (the one lane with "buildsInLane": own checkout, own compile, no archive)
```

* **plan** (`plan-matrix.ps1`) reads the plan and emits the two job matrices. A packaging platform gets a build job when
  an enabled archive lane needs it; a (platform, lane) pair gets a test job when it has an enabled entry. Focus and
  platform filters apply before the matrices are created. Entries can normally be enabled in the plan; new system
  prerequisites can require workflow changes.
* **build** (`build-binaries.ps1`, `package-workspace.ps1`) is the only job that restores and compiles. It builds every
  distinct `project` of an enabled entry, prints per-project durations and output sizes in the job summary, then packs
  the whole workspace (sources and all build outputs, no `.git`, no `node_modules`) and uploads it as one archive
  (zstd when the runner's `tar` supports it, gzip otherwise; the upload does not compress a second time). Refuses to pack
  a `NuGet.config` that holds a clear-text credential.
* **test** (`run-test-plan.ps1`) unpacks the archive and runs the lane's entries, each `xunit-exe`
  one also writing a TRX (`-trx <results>/<entry>.trx`, which is how xUnit v3 spells it; `--report-trx` is the Microsoft
  Testing Platform option and is rejected). The workflow publishes those as a check (`dorny/test-reporter`, guarded by
  `continue-on-error` so a fork PR's read-only token cannot fail a lane) and uploads them as an artifact: `xunit-exe`
  runs the compiled xUnit v3 assembly directly (`dotnet <dll> [args]`, so the integration classes are
  `-class <name> -parallel none`), `msbuild` runs the AddIn SDK assertion projects with `-t:Build`. Nothing is restored
  or built there by the runner. Some integration journeys restore temporary fixture projects using the populated package
  cache; those restores can consult configured feeds. The workflow does not write credentials into the workspace archive.
  Windows ARM64 maps the original x64 build workspace path to its own checkout for paths embedded in build outputs and PDBs.
* **in-place** runs only for the lane whose definition says `"buildsInLane": true` (today `unit`). It has its own
  checkout and submodules, restores the same package cache entry, compiles only that lane's entries
  (`build-binaries.ps1 -Lane <lane> -SkipHostBuilds`, so neither the host nor the RID rebuild happens) and runs them. It
  needs `plan` only and does not download the workspace archive, so its result arrives minutes into the run instead of
  behind the compile-and-pack job. The build job leaves that lane's projects out (`-ArchiveLanesOnly`), so nothing is
  compiled twice.

The package folder is one `actions/cache` entry keyed by `Directory.Packages.props`, `global.json`, `NuGet.config`,
the test plan, fixture projects and the requested batch. Plan, fixture or batch changes must use a fresh key because GitHub caches are immutable:
the build may restore new fixture packages that are absent from an older cache entry.
The build job restores and saves it and publishes the key; a lane with `needsNuGetCache` restores that exact key with
`fail-on-cache-miss: true`, because the IDE host is not self-contained - it resolves its LibreWPF assemblies from the
global packages folder at run time, which the first integration lane run showed as a start-up `FileNotFoundException`
for `PresentationFramework`. A miss fails the lane instead of letting the IDE die later.

## What makes a green lane mean something

`run-test-plan.ps1` refuses three shapes that used to pass silently:

* an `xunit-exe` entry that exits 0 but prints no test total, or a total of 0 - a filter that matches nothing, or an
  assembly that never ran, is a failure (a `::warning` while the entry is only `observe`, a `::error` once `required`);
* a lane in which every entry failed, whatever their modes say, unless its definition explicitly sets `allowAllFailures`
  for observe-first rollout or a tracked host defect. Such a lane can be green with no passing tests, so its entry summary
  must be checked before promotion;
* an entry whose test assembly was never produced - the build step is named in the error.

Every entry also has a time budget: its own `timeoutMinutes`, else its lane's `entryTimeoutMinutes` (5 minutes in the
integration lanes). Past it, `run-test-plan.ps1` kills the entry's process tree (the IDE the fixture started included),
records `timeout` and runs the next entry; a `required` entry that times out fails the job. Just before the kill it saves a
full-screen `<entry>.timeout.png` (`screencapture -x` on macOS, the virtual screen through System.Drawing on Windows), since
a hang is often a modal error dialog only the screen shows; a failed capture is reported (for example macOS's "could not
create image from display" without Screen Recording permission) and never changes the outcome. The output is printed as it
arrives and kept as `<entry>.out.log`/`.err.log` with the TRX files. Before this, output appeared only when the process
exited and nothing stopped a hung entry, so a hang ran into the lane timeout, which cancels the job and loses every
result of it (cancelled jobs keep no log). Budgets come from measured durations: the default covers every entry that
took under about three minutes, and longer ones carry their own (`workbench-tests` 8, `profiler-journeys` 10, debugger
classes 10 and 12, WPF Hot Reload 8, LibreWPF editing 35, MewUI 10, GTK journeys 12). The lane `timeoutMinutes` is only
the safety net: the slowest preparation (about 21 minutes) plus the lane's measured tests plus one budget.

`observe` means an individual failure does not fail the job; a lane with `allowAllFailures` can also stay green when no
entry passes.

Lanes are separate jobs on separate fresh machines, so they cannot interfere through ports (the DevFlow port), config
directories, temp files or a shared IDE instance. Inside a lane the entries run one after another; an integration entry
starts its own IDE process.

What a platform may build and how its lanes must run is the packaging contract, not a CI preference: one Windows payload
serves x64 and ARM64, macOS ships osx-arm64 only. [`dual-architecture-packaging.md`](dual-architecture-packaging.md)
states it, and its "CI: the same contract, enforced instead of hoped for" section maps it to the checks that live in this
workflow (`buildFrom`, the archive rules per platform, the payload scan and the per-lane RID/architecture check).

## How an entry is enabled

* `batch` orders the rollout; `throughBatch` is the highest batch that runs. `workflow_dispatch` can override it once
  (`through_batch`) to try the next batch without a commit.
* Per platform a status is `observe` (runs; a failure is an annotation and a row in the job summary, the job stays
  green), `required` (a failure fails the job) or `excluded` (does not run, `reason` says why). A platform missing from an
  entry is excluded.
* A new batch starts as `observe`. Promote an entry to `required` on a platform once the last two hosted runs that
  completed it there both passed (read the per-entry tables in the job summaries: with `observe` a green run does not mean
  the tests passed; a cancelled job does not count as a run), and put the measured duration into `measuredSeconds`. The
  push that promotes it is the confirming run: if the entry fails there, set it back to `observe` instead of retrying.
* An entry names its `lane`, `kind`, `target` (the test assembly or project), `args`, and the `project` the build job
  compiles for it. A lane is defined once under `lanes` with its timeout.
* `additionalProjects` names fixture projects that the entry needs built before the archive is created. Optional
  `platforms` refer to the build platform: both Windows architectures consume the `windows-x64` archive. The WPF Hot
  Reload entry builds the LibreWPF application and class-library host on both build platforms, plus the Microsoft WPF
  class-library host on Windows.

## Lanes

Preparing a lane job costs more than most entries: downloading and unpacking the workspace archive and restoring the
NuGet cache took 11-15 minutes on Windows (up to 21) and 3-12 minutes on macOS on run 40444468212, against 1-2 minutes of
tests for code coverage and the ARM64 unit suites. So entries share as few jobs as possible, grouped by how reliable they
have been, and a lane is split only for a real environment need or a timeout.

* `unit` (Windows x64, in place) compiles and runs its own unit suites without the archive, so its result comes first.
* `integration-stable` holds the entries that passed on every hosted run in which the IDE started: the five workbench
  classes, the stable core journeys (solution folder, project options, T4, Task List, project reload, the Windows Terminal
  journey, Razor, the Windows and macOS new-project dialogs, fixture isolation, Profiler journeys, the WPF designer
  assembly lock), code coverage, and the VSEditorCompat, XamlDom and Profiler suites on the ARM64 platforms. Its
  entries are `required` on the platforms where each is enabled (each passed on the last two hosted runs that completed it there;
  the results are recorded above), and the lane has no `allowAllFailures`. `new-project-dialog-macos` joined later: it
  failed on the hosted runner until `cc569fd5a8` and moved here after two green runs. `add-reference-journey` is `required` on
  all three platforms since 2026-10-28 but still runs in `integration-unstable` (see below and "Focus mode").
* `integration-unstable` holds entries with known hosted failures or under investigation: the macOS Terminal pad (pointer
  input) and `debugger-journeys` on Windows ARM64. Some of its entries are `required` where they pass: the debugger
  classes and WPF Hot Reload on macOS ARM64 and Windows x64, and `base-tests-arm`, `add-reference-journey` and
  `debugger-feature-journeys` on every platform. Those last three would belong in `integration-stable` under the rule below,
  but stay until a full run has re-measured that lane (55-minute timeout) with them added.
  Entries run in plan order, and Hot Reload is last so a hang there cannot hide the others. Move an entry to
  `integration-stable` under the same two-run rule.
* `integration-designer` holds every designer journey (LibreWPF editing and property editors, LibreWinForms, MewUI and
  both GTK entries) and installs the GTK runtime (`needsGtk`, 3.5 minutes on Windows). Entries in a lane run one after
  another, so the LibreWPF journeys that edit `src/Samples` in place still never share the sample with a running test.
  On Windows the 30 LibreWPF editing journeys alone took 24-28 minutes (budget 35 minutes). The setup uses Homebrew on
  macOS and architecture-specific MSYS2 environments on Windows, verifying the Windows DLLs and GIR metadata.

Merging the former nine archive lanes into these three changed no build input: `build-binaries.ps1 -List` produces the
same 23 Windows x64 and 21 macOS ARM64 projects before and after.

### Focus mode

While many entries still fail, the plan's top-level `"focus": true` runs only the `observe` entries on each platform: a
`required` one has already passed on the last two hosted runs that completed it, so rerunning it on every push mostly buys
the 11-15 minutes of lane preparation again. `.github/ci/run-statuses.ps1` holds the rule, and `plan-matrix.ps1`,
`build-binaries.ps1` and `run-test-plan.ps1` all use it, so a skipped entry is neither scheduled, built nor run. With it,
`integration-stable` has no entry left and is not scheduled on any platform (6 integration jobs instead of 9), and the
build lists shrank from 23 to 15 Windows x64 and from 21 to 14 macOS ARM64 lines when focus mode was introduced
(each later promotion shrinks them further). `add-reference-journey` moved to
`integration-unstable` for that reason while it was `observe` on Windows ARM64 only (it would otherwise have kept
`integration-stable` alive for one known failure); it is `required` everywhere now and stays in `integration-unstable`
for the reason given under "Lanes". A promoted entry stops running too, so run full once before trusting master again
(`gh workflow run ci.yml --ref <branch> -f full=true`), and set `"focus": false` to go back to full runs when the
failures are under control. Focus mode does not gate anything: the `required` entries are not checked while it is on.

## Current plan (Windows x64)

* Lane `unit`: `OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests`, `OpenDevelop.XamlDom.Tests.LibreWPF` and
  `.MicrosoftWPF`, `OpenDevelop.Profiler.Tests`, and the two `OpenDevelop.AddinSdk*.proj` builds (run with `dotnet msbuild -t:Build` as
  `doc/technotes/addin-sdk.md` says; `dotnet build` of the same file fails because its restore changes what the asserted
  targets see). They need no IDE process.
* Lanes `integration-stable`, `integration-unstable` and `integration-designer`: the integration entries, grouped as
  described under "Lanes".

Not planned yet: `AddInTests` (about 15 minutes, its own lane), the Microsoft designer
backends (need Visual Studio), and the heavy or environment-bound ones named in
`tests/OpenDevelop.IntegrationTests/TESTING.md` (dotnetup SDK installs, runtime upgrade, performance gates, probes).
The Profiler tests and the Terminal journey are plan entries now; `profiler.yml` and `terminal-cross-platform.yml` were
deleted with that fold. The Profiler shares the `unit` lane (its data layer attaches to a real process and needs no IDE
host), and the Terminal journey runs in the integration lanes and gets its `win-x64` add-in from `buildLast`, the way its own
workflow built it. Windows ARM64 and macOS ARM64 still have observed integration entries (Windows x64 has none left);
Linux remains disabled.
The `platformNotes` in the plan record each decision.

## The archives

Only the archive lanes consume the workspace archive; the in-place lane never downloads it, and the build job no longer
compiles that lane's projects. The first full run measured the raw workspace at about 14 GB (`AddIns` 12.2 GB: every
add-in carries ~550 MB because the RID-less build copies the native libraries of every system, and `.pdb` files are 4.2 GB
of it), packed to 5.3 GB, and the test jobs spent 4 to 6 minutes downloading and unpacking it. `package-workspace.ps1 -Platform windows-x64` therefore leaves
out what Windows can never load: `.so`, `.dylib`, `.a`, `.lib`, the `runtimes/<linux|osx|android|ios|tvos|maccatalyst|browser>*`
folders and the `.pdb` files under `AddIns/` (the fixtures' `.pdb` files stay, the debugger tests read them). The same
exclusions apply to the NuGet cache archive (`nuget-<platform>`). The job summaries print the on-disk and archived sizes and
the pack time; record the new numbers here once a run has produced them.

## The build

The build job builds, in this order: the plan's `buildFirst` projects (the host, `src/Main/SharpDevelop`), then every
distinct `project` of an enabled entry with that entry's `buildArgs`.

* **The host goes first.** Its build writes `OpenDevelop.host-assemblies.txt`, which every add-in build uses to trim the
  assemblies the host already ships (`src/SDK/OpenDevelop.Addin.Sdk`). The integration test project does not reference the
  host, so on a clean checkout it would build the add-ins before that manifest exists and every add-in would carry its
  own ~550 MB of copy-local dependencies: the first CI run measured `AddIns` at 13 GB, against 3.4 GB on a developer
  machine. `tests/OpenDevelop.IntegrationTests/TESTING.md` says the same ("build the application first").
* **Fixtures are not built with the integration project** (`-p:BuildIntegrationFixtures=false`): that target builds every
  fixture inside one MSBuild process, where `UnoHotReloadFixture` (`Uno.Sdk/6.6.42`) clashes with the SDK version already
  resolved (MSB4240, so no `InitializeComponent` is generated). A lane that needs a fixture lists the fixture's own
  `.csproj` as an entry `project`, which builds in a process of its own.
* `build-binaries.ps1 -List` prints these commands without running them; use it to check a plan edit.

The job initialises the submodules the build reaches: cps, WPF Toolkit, vscode-wpf (with its
nested WpfDesigner and wxsg), the small debugger, coverage and XAML ones, and `externals/monodevelop` shallow, because
the AspNetCore add-in compiles four files straight out of it (a `Compile Include`, which a `ProjectReference` closure does
not show). The monodevelop sources stay out of the workspace archive. The build uses the repository's LibreWPF SDK pin (`0.1.0-preview.69` in `global.json`) and the configured package
source mappings. AvalonEdit and AvalonDock are consumed as packages rather than initialized as submodules here.

Measured on the first runs (windows-2025): the integration project build takes about ten minutes and the four unit test
projects about two more; the numbers for the archive (size, pack, upload, download, unpack) come from the next run's job
summaries and belong in this section once known.

## The IDE did not start on the Windows runner

Two defects stopped the IDE during start-up on Windows, one after the other.

* `FileNotFoundException: Could not load file or assembly 'PresentationFramework, Version=11.0.0.0'`. The file was in the
  host folder, but the Windows `deps.json` registered the LibreWPF assemblies only inside the `LibreWPF.Transport` package
  entry. `.github/ci/patch-host-deps.ps1` now adds app-local entries for them (24 assemblies in run 40431285339), and the
  host gets past assembly loading. Delete that script once an SDK produces the entries itself and it reports "nothing to do".
* `PlatformNotSupportedException: Portable media requires a registered portable window host; Windows MIL window creation
  is not permitted`, from `Window.Show`. The SDK bootstrap compiled into `OpenDevelop.dll` (`ManagedPortable` renderer
  mode) registers the portable window host only off Windows and leaves Windows on Windows MIL, but
  `PortableTextCompatibility.Initialize` selected the portable media backend unconditionally. The message comes only from
  "no host registered and backend is Portable"; a registered host that fails to activate throws a different
  `InvalidOperationException`. Fixed in `980e1b6a7c` by leaving a non-portable backend alone; run 40444468212
  confirmed it (the IDE starts on both Windows architectures). Remove `allowAllFailures` from a lane once its
  entries are promoted under the rule above.

How the second one was found, for the next host-start problem: search the built host folder's DLL metadata for the API
that changes the state (`SelectMediaBackend`, `ProGpuWpfNativeMediaServices`), decompile the SDK bootstrap actually
compiled into `OpenDevelop.dll` with `ilspycmd`, and compare with the LibreWPF package versions in use rather than with the
`../openavalon` working tree. The `diagnose-host.ps1` step runs only for a `workflow_dispatch` with `diagnose`, and it
inspects assembly resolution (`COREHOST_TRACE`, trusted platform assemblies), not media or windowing state.
