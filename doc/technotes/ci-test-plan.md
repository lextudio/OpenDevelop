# CI test plan

Goal: every test that runs locally also runs on GitHub Actions, switched on in small batches so a single run
does not become slow all at once and a new test cannot turn the main line red by surprise.

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
  or built there, so the NuGet feeds are not touched and no credential is present in test jobs.
* **in-place** runs only for the lane whose definition says `"buildsInLane": true` (today `unit`). It has its own
  checkout and submodules, restores the same package cache entry, compiles only that lane's entries
  (`build-binaries.ps1 -Lane <lane> -SkipHostBuilds`, so neither the host nor the RID rebuild happens) and runs them. It
  needs `plan` only and does not download the workspace archive, so its result arrives minutes into the run instead of
  behind the compile-and-pack job. The build job leaves that lane's projects out (`-ArchiveLanesOnly`), so nothing is
  compiled twice.

The package folder is one `actions/cache` entry keyed by `Directory.Packages.props`, `global.json` and `NuGet.config`.
The build job restores and saves it and publishes the key; a lane with `needsNuGetCache` restores that exact key with
`fail-on-cache-miss: true`, because the IDE host is not self-contained - it resolves its LibreWPF assemblies from the
global packages folder at run time, which the first integration lane run showed as a start-up `FileNotFoundException`
for `PresentationFramework`. A miss fails the lane instead of letting the IDE die later.

## What makes a green lane mean something

`run-test-plan.ps1` refuses three shapes that used to pass silently:

* an `xunit-exe` entry that exits 0 but prints no test total, or a total of 0 - a filter that matches nothing, or an
  assembly that never ran, is a failure (a `::warning` while the entry is only `observe`, a `::error` once `required`);
* a lane in which every entry failed, whatever their modes say - a lane that exercises nothing is not a pass. This is
  what turns a lane whose ten `observe` entries all fail into a red job instead of a green one;
* an entry whose test assembly was never produced - the build step is named in the error.

`observe` still means "a single failure does not fail the job", but it can no longer hide a lane that runs nothing.

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

## Current plan (Windows x64)

* Lane `unit`: `OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests`, `OpenDevelop.XamlDom.Tests.LibreWPF` and
  `.MicrosoftWPF`, and the two `OpenDevelop.AddinSdk*.proj` builds (run with `dotnet msbuild -t:Build` as
  `doc/technotes/addin-sdk.md` says; `dotnet build` of the same file fails because its restore changes what the asserted
  targets see). They need no IDE process.
* Lane `integration-core` (batch 3): the integration classes that share one IDE instance and a solution fixture:
  DevFlow, Workbench, StopAfterFirstFailureBuild, SolutionFolder, ProjectOptions, T4, TaskListPad, VSEditorView,
  OneProcessBuild, ProjectReload.

Not planned yet: `AddInTests` (about 15 minutes, its own lane), the debugger and coverage classes, the Microsoft designer
backends (need Visual Studio), and the heavy or environment-bound ones named in
`tests/OpenDevelop.IntegrationTests/TESTING.md` (dotnetup SDK installs, runtime upgrade, performance gates, probes).
The Profiler tests and the Terminal journey are plan entries now; `profiler.yml` and `terminal-cross-platform.yml` were
deleted with that fold. The Profiler shares the `unit` lane (its data layer attaches to a real process and needs no IDE
host), and the Terminal journey joins `integration-core` and gets its `win-x64` add-in from `buildLast`, the way its own
workflow built it. Windows x64 is the only enabled platform: macOS fails those journeys on LibreWPF's `Display` text
capability, and Linux comes back as its own platform entry later (the Profiler tests passed on `ubuntu-latest`). The
`platformNotes` in the plan record each decision.

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

## Open issue: the integration lane does not start the IDE on the runner

All ten `integration-core` entries fail in `OpenDevelopAppFixture.InitializeAsync`: the IDE exits during start-up with
`FileNotFoundException: Could not load file or assembly 'PresentationFramework, Version=11.0.0.0'`
(`ProGpuWpfSdkPortableBootstrap.Initialize`, the module initializer the LibreWPF SDK compiles into each project). Launching
`dotnet OpenDevelop.dll` by hand on the runner reproduces it (the temporary `diagnose-host.ps1` step), so it is the host
folder, not the test fixture or the lane.

What was measured, and so ruled out:

* The file is there: `PresentationFramework.dll`, assembly identity 11.0.0.0, same as on a developer machine.
* The NuGet package cache is not the missing piece: restoring it in the test job left the error unchanged.
* The .NET host's own trace (`COREHOST_TRACE`) shows `PresentationFramework.dll` resolved from the host folder and present in
  `TRUSTED_PLATFORM_ASSEMBLIES`. So the failure is not "not registered" and not "wrong path": the runtime has the path and
  still fails to load the file.
* Build order alone does not matter: the host's `OpenDevelop.deps.json` is 166 libraries without a `PresentationFramework/11.0.0.0`
  entry from the very first host build on the runner, and stays that way whatever is built afterwards in the default way (the
  build summary records it after every build). An earlier theory (add-ins that reference the host project rebuild it with other
  properties and rewrite `deps.json`) was wrong.

What does differ from a developer (macOS) build: 193 libraries with 28 `reference` entries for the LibreWPF assemblies
(`PresentationCore`, `PresentationFramework`, `WindowsBase`, `System.Xaml`, ...); the Windows build registers them only inside the
`LibreWPF.Transport` package entry. The Terminal workflow starts the IDE on Windows, with a differently built host (the integration
project is built there with `BuildIntegrationRuntimeProjects=false`).

Next, two things at once. The plan's `buildLast` now builds the host a last time with `--runtime win-x64
-p:AppendRuntimeIdentifierToOutputPath=false`, the recipe the Terminal workflow's passing Windows lane gets by building the Terminal
add-in that way (the add-in references the host project, so the host is built as that RID into the same folder); the build summary
shows what that does to the host `deps.json`. And the `Diagnose the IDE host start-up` step (temporary) loads `WindowsBase`,
`PresentationCore` and `PresentationFramework` by path with `dotnet fsi` (its own `WindowsBase` facade makes the type-load errors
noisy; read the `LoadFrom` line first) and launches the host once more with `DOTNET_ReadyToRun=0`. Replace this section with the
answer when it is known.
