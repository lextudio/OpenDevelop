# CI test plan

Goal: every test that runs locally also runs on GitHub Actions, switched on in small batches so a single run
does not become slow all at once and a new test cannot turn the main line red by surprise.

## Shape: compile once, test in parallel lanes

`.github/workflows/ci.yml` is the one workflow. `.github/ci/test-plan.json` is the only place that decides what runs.

```
plan  ->  build (per platform)  ->  test (per platform x lane)
          compile once, upload       download, unpack, run the lane's test assemblies
```

* **plan** (`plan-matrix.ps1`) reads the plan and emits the two job matrices. A platform gets a build job when any
  entry is enabled on it; a (platform, lane) pair gets a test job when it has an enabled entry. Enabling more tests,
  or a new lane, never edits the workflow.
* **build** (`build-binaries.ps1`, `package-workspace.ps1`) is the only job that restores and compiles. It builds every
  distinct `project` of an enabled entry, prints per-project durations and output sizes in the job summary, then packs
  the whole workspace (sources and all build outputs, no `.git`, no `node_modules`) and uploads it as one archive
  (zstd when the runner's `tar` supports it, gzip otherwise; the upload does not compress a second time). Refuses to pack
  a `NuGet.config` that holds a clear-text credential.
* **test** (`run-test-plan.ps1`) unpacks the archive at the same absolute path (and, for a lane with `needsNuGetCache`, the
  NuGet package cache: the IDE host is not self-contained, it resolves its LibreWPF assemblies from the global packages
  folder at run time, which the first integration lane run showed as a start-up `FileNotFoundException` for
  `PresentationFramework`) and runs the lane's entries:
  `xunit-exe` runs the compiled xUnit v3 assembly directly (`dotnet <dll> [args]`, so the integration classes are
  `-class <name> -parallel none`), `msbuild` runs the AddIn SDK assertion projects with `-t:Build`. Nothing is restored
  or built there, so the NuGet feeds are not touched and no credential is present in test jobs.

Lanes are separate jobs on separate fresh machines, so they cannot interfere through ports (the DevFlow port), config
directories, temp files or a shared IDE instance. Inside a lane the entries run one after another; an integration entry
starts its own IDE process.

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
`terminal-cross-platform.yml` and `profiler.yml` still run on their own and are listed in the plan as excluded; they move
into lanes of this workflow next. macOS is skipped for now (see `platformNotes` in the plan).

## The archives

The first full run measured the raw workspace at about 14 GB (`AddIns` 12.2 GB: every add-in carries ~550 MB because the
RID-less build copies the native libraries of every system, and `.pdb` files are 4.2 GB of it), packed to 5.3 GB, and the
test jobs spent 4 to 6 minutes downloading and unpacking it. `package-workspace.ps1 -Platform windows-x64` therefore leaves
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
(`ProGpuWpfSdkPortableBootstrap.Initialize`, the module initializer the LibreWPF SDK compiles into each project). What was
ruled out by inspecting the uploaded workspace archive: the file is in the host folder (`PresentationFramework.dll`,
assembly version 11.0.0.0, same identity as on a developer machine); the host's `OpenDevelop.runtimeconfig.json` and
`OpenDevelop.deps.json` match a developer build, except that the CI `deps.json` has 167 libraries against 193 and registers
`PresentationFramework.dll` only as a plain runtime asset of the `LibreWPF.Transport` package; restoring the NuGet package
cache in the test job did not change the error. What was ruled out the hard way: an earlier theory that the missing piece
was the NuGet cache. The `Diagnose the IDE host start-up` step of `ci.yml` (temporary) runs the host by hand with
`COREHOST_TRACE` and prints what the .NET host probed; replace this section with the answer when it is known.
