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
* **test** (`run-test-plan.ps1`) unpacks the archive at the same absolute path and runs the lane's entries:
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

## The build

The build job initialises the submodules the integration test project's `ProjectReference` closure reaches (AvalonEdit,
AvalonDock, cps, WPF Toolkit, vscode-wpf with its nested WpfDesigner and wxsg) plus the small debugger, coverage and
XAML ones; not `externals/monodevelop` (about 750 MB). It builds against the repository's own LibreWPF pins (preview.65
from nuget.org once the developer-local feed is removed); the preview.69 trial lives in the Terminal workflow only.
