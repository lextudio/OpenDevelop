# CI test plan

Goal: broaden GitHub Actions coverage of locally runnable tests on Windows x64, Windows ARM64 and macOS ARM64.
Introduce substantial groups in observe mode, inspect their actual runner results, and promote stable entries to
required per platform. Linux product tests remain out of scope for this rollout.

## Rollout plan and progress (2026-10-26)

* **Completed locally:** workbench, debugger and coverage integration lanes are configured as observations on all three
  platforms. Their macOS ARM64 local results are recorded below; hosted runner results for the new configuration are
  still needed. Their commits are local until pushed.
* **Committed locally:** Base, VSEditorCompat, LibreWPF XamlDom and Profiler unit suites are configured on both ARM64
  platforms. The actual macOS CI runner script passed 875 cases and skipped nine; Windows ARM64 has not yet run. Verify
  both hosted runners after an authorized push.
* **Committed locally:** the new-project dialog journeys pass locally on macOS ARM64 (7 passed,
  one Windows-only SDK download skipped; 57 seconds). Enable the complete class on macOS and its five non-SDK-install
  methods on both Windows architectures as observations. The Windows SDK-selection theory needs a separate lane and
  time budget because one data row installs a real SDK from the network; its two non-network rows are excluded with it
  until the theory can be split without weakening coverage. Hosted runner results remain outstanding.
* **Committed locally:** run the whole Profiler integration class on all three platforms instead of
  only its isolation case. On macOS ARM64 it passed 21 journeys and skipped its Windows-only WPF journey in 107 seconds.
  Its web, test, profiler and Windows WPF fixtures must be built into the shared archives. Hosted results are pending.
* **Committed locally:** the designer lanes observe 30 LibreWPF editing journeys and the LibreWinForms
  image-list journey on all three platforms, plus the LibreWPF property-editor journey on macOS. The two latter cases
  passed locally as part of a three-case class with one Windows-only skip in 45 seconds. The 30-case LibreWPF suite
  passed locally on macOS ARM64 in 217 seconds. On Windows they ran on run 40444468212 (29/30 on x64, about 28
  minutes on ARM64), slow enough to need their own lane. The property-editor
  method selects Microsoft WPF on Windows, so it is not enabled there until its host is available. Individual failures
  are observed; an all-failed lane still fails. These tests temporarily edit files under `src/Samples` and restore their
  original contents in cleanup. Run them serially in an isolated checkout or archive, not alongside other tests using
  those samples; interruption can leave a sample modified.
* **Committed locally:** add the Add Reference journey on all three platforms. A macOS run initially exposed a
  DevFlow mutation lock held during the modal dialog; opening the selected project's dialog by a posted action fixes the
  deadlock, and the complete reference-addition test passed locally in 37 seconds. Hosted results are pending.
* **Committed locally:** add ten MewUI designer journeys on all three platforms (now in `integration-mewui-winforms-designer`). The initial
  macOS run found two outdated tests using a DockPanel attached property on a StackPanel child; after correcting the
  fixture and checking both mismatched-owner rejection and unknown-property diagnostics, all ten passed in 55 seconds. The build includes
  MewUIGen and the fixture to populate the package cache needed when each test restores its temporary copy. Hosted
  results remain outstanding.
* **In local validation:** add 13 GTK designer journeys to a separate observation lane on all three platforms. The macOS local runs
  passed all 12 native-rendering/editing cases in 68 seconds and the nested edit/save case in 19 seconds through the
  CI runner script. The lane
  installs Homebrew GTK 4 and libadwaita for the native runtime and GIR metadata; its fixture is built to prime NuGet.
  Windows x64 uses MSYS2 UCRT64; Windows ARM64 uses CLANGARM64, with GTK 4, libadwaita and GIR metadata. The lane
  checks the GTK DLL architecture before launch. Hosted results and setup time remain unverified on all platforms.
  [MSYS2's setup action](https://github.com/msys2/setup-msys2) supports both environments; its
  [ARM64 libadwaita package](https://packages.msys2.org/packages/mingw-w64-clang-aarch64-libadwaita) depends on GTK 4.
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
  `integration-mewui-winforms-designer` (45 minutes) holds MewUI and LibreWinForms.
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

* **plan** (`plan-matrix.ps1`) reads the plan and emits the two job matrices. A platform gets a build job when any
  entry is enabled on it; a (platform, lane) pair gets a test job when it has an enabled entry. Enabling more tests,
  or a new lane, never edits the workflow.
* **build** (`build-binaries.ps1`, `package-workspace.ps1`) is the only job that restores and compiles. It builds every
  distinct `project` of an enabled entry, prints per-project durations and output sizes in the job summary, then packs
  the whole workspace (sources and all build outputs, no `.git`, no `node_modules`) and uploads it as one archive
  (zstd when the runner's `tar` supports it, gzip otherwise; the upload does not compress a second time). Refuses to pack
  a `NuGet.config` that holds a clear-text credential.
* **test** (`run-test-plan.ps1`) unpacks the archive at the same absolute path and runs the lane's entries, each `xunit-exe`
  one also writing a TRX (`-trx <results>/<entry>.trx`, which is how xUnit v3 spells it; `--report-trx` is the Microsoft
  Testing Platform option and is rejected). The workflow publishes those as a check (`dorny/test-reporter`, guarded by
  `continue-on-error` so a fork PR's read-only token cannot fail a lane) and uploads them as an artifact: `xunit-exe`
  runs the compiled xUnit v3 assembly directly (`dotnet <dll> [args]`, so the integration classes are
  `-class <name> -parallel none`), `msbuild` runs the AddIn SDK assertion projects with `-t:Build`. Nothing is restored
  or built there by the runner. Some integration journeys restore temporary fixture projects using the populated package
  cache; those restores can consult configured feeds. The workflow does not write credentials into the workspace archive.
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
* A new batch starts as `observe`. Promote it to `required` per platform after it has been green there for several runs
  (read the per-entry tables in the job summaries: with `observe` a green run does not mean the tests passed), and put the
  measured duration into `measuredSeconds`.
* An entry names its `lane`, `kind`, `target` (the test assembly or project), `args`, and the `project` the build job
  compiles for it. A lane is defined once under `lanes` with its timeout.
* `additionalProjects` names fixture projects that the entry needs built before the archive is created. Optional
  `platforms` refer to the build platform: both Windows architectures consume the `windows-x64` archive. The WPF Hot
  Reload lane builds the LibreWPF application and class-library host on both build platforms, plus the Microsoft WPF
  class-library host on Windows. Its single entry starts as `observe` with `allowAllFailures`, so an observed failure
  remains visible in the summary without failing the lane; remove that exception when the entry becomes required.

The solution-folder, project-options, T4, Task List and project-reload journeys also run in observe mode on macOS ARM64
and Windows ARM64. The existing Windows x64 entries remain observed. These are initial observations, not evidence of
passing results on the runners.

`integration-workbench` runs DevFlow, workbench, editor-view, stop-after-failure and one-process-build journeys on all
three platforms as observations. It runs separately from the other integration entries to avoid extending their serial
lane. In line with observe-first rollout, it allows even an all-failed run; read the entry summaries rather than treating
the green job as a pass. Windows previously had the host-start defect described below; remove this exception once stable
entries are promoted. The five classes passed 46/46 in one local macOS ARM64 IDE session in about 111 seconds. The CI
runner launches one IDE per entry and measured 20, 40, 26, 19 and 31 seconds locally (about 136 seconds combined); hosted
runner results are still needed.

`integration-debugger` observes the debugger journey and feature classes on all three platforms, with DebugTestApp and
AttachFixture prebuilt. `integration-coverage` observes the code-coverage journeys with both sample test projects built;
their project references build CoverageLib. Both lanes initially permit all observed entries to fail without gating CI,
so read their entry summaries and skipped counts. On macOS ARM64 the debugger classes passed 23 cases and skipped five
architecture/runtime-specific cases; code coverage passed all three cases. Windows runner results remain unverified.

`unit-cross-platform` observes Base, VSEditorCompat, LibreWPF XamlDom and Profiler tests on macOS ARM64 and Windows
ARM64; the existing Windows x64 unit lane is unchanged. The Windows x64 archive build includes projects needed solely by
its ARM64 consumer. On macOS those suites passed 490/499 (9 skipped), 141/141, 101/101 and 143/143 respectively. The
Windows ARM64 runner remains unverified. Each entry is observed individually, but a lane where all four fail is an
error: a green lane must contain at least one passing entry. Inspect every entry before promotion.

## Current plan (Windows x64)

* Lane `unit`: `OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests`, `OpenDevelop.XamlDom.Tests.LibreWPF` and
  `.MicrosoftWPF`, and the two `OpenDevelop.AddinSdk*.proj` builds (run with `dotnet msbuild -t:Build` as
  `doc/technotes/addin-sdk.md` says; `dotnet build` of the same file fails because its restore changes what the asserted
  targets see). They need no IDE process.
* Lane `integration-workbench`: DevFlow, Workbench, StopAfterFirstFailureBuild, VSEditorView and OneProcessBuild.
* Lane `integration-core`: SolutionFolder, ProjectOptions, T4, TaskListPad, ProjectReload, Terminal and other observed
  integration entries. The Hot Reload journeys run in their own `integration-hot-reload` lane.

Not planned yet: `AddInTests` (about 15 minutes, its own lane), the Microsoft designer
backends (need Visual Studio), and the heavy or environment-bound ones named in
`tests/OpenDevelop.IntegrationTests/TESTING.md` (dotnetup SDK installs, runtime upgrade, performance gates, probes).
The Profiler tests and the Terminal journey are plan entries now; `profiler.yml` and `terminal-cross-platform.yml` were
deleted with that fold. The Profiler shares the `unit` lane (its data layer attaches to a real process and needs no IDE
host), and the Terminal journey joins `integration-core` and gets its `win-x64` add-in from `buildLast`, the way its own
workflow built it. Windows x64, Windows ARM64 and macOS ARM64 have observed integration entries; Linux remains disabled.
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

The job initialises the submodules the build reaches: AvalonEdit, AvalonDock, cps, WPF Toolkit, vscode-wpf (with its
nested WpfDesigner and wxsg), the small debugger, coverage and XAML ones, and `externals/monodevelop` shallow, because
the AspNetCore add-in compiles four files straight out of it (a `Compile Include`, which a `ProjectReference` closure does
not show). The monodevelop sources stay out of the workspace archive. The build uses the repository's own LibreWPF pins
(preview.65 from nuget.org once the developer-local feed is removed); the preview.69 trial lives in the Terminal workflow
only.

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
  confirmed it (the IDE starts on both Windows architectures). Remove `allowAllFailures` from a Windows lane once its
  entries have been stable for several runs, as for any other promotion.

How the second one was found, for the next host-start problem: search the built host folder's DLL metadata for the API
that changes the state (`SelectMediaBackend`, `ProGpuWpfNativeMediaServices`), decompile the SDK bootstrap actually
compiled into `OpenDevelop.dll` with `ilspycmd`, and compare with the LibreWPF package versions in use rather than with the
`../openavalon` working tree. The `diagnose-host.ps1` step runs only for a `workflow_dispatch` with `diagnose`, and it
inspects assembly resolution (`COREHOST_TRACE`, trusted platform assemblies), not media or windowing state.
