# CI test plan

Goal: every test that runs locally also runs on GitHub Actions, switched on in small batches so a
single run does not become slow all at once and a new test cannot turn the main line red by surprise.

`.github/ci/test-plan.json` is the only place that decides what runs. `.github/workflows/tests.yml` reads it
through `.github/ci/run-test-plan.ps1` and runs the enabled entries on Windows x64 and macOS ARM64.
The Terminal journeys (`terminal-cross-platform.yml`) and the Profiler tests (`profiler.yml`) keep their own
workflows and are listed in the plan as `excluded` with that reason, so the plan still names every suite.

## How an entry is enabled

* `batch` orders the rollout; `throughBatch` is the highest batch that runs. `workflow_dispatch` can override it
  once (`through_batch`) to try the next batch without a commit.
* Per platform a status is `observe` (runs; a failure is an annotation and a row in the job summary, the job stays
  green), `required` (a failure fails the job) or `excluded` (does not run, `reason` says why).
* A new batch starts as `observe`. Promote it to `required` per platform after it has been green there for several
  runs, and put the measured duration (the job summary prints it next to the planned one) into `measuredSeconds`.
* Adding a kind of test (for example an `OpenDevelop.IntegrationTests` class) means teaching
  `run-test-plan.ps1` its command line first, then adding entries; the script fails on a kind it does not know.

## Batch 1

`OpenDevelop.Base.Tests`, `OpenDevelop.VSEditorCompat.Tests`, `OpenDevelop.XamlDom.Tests.LibreWPF` and the two
`OpenDevelop.AddinSdk*.proj` builds. They need no IDE process. Measured once on macOS ARM64 through the script,
all passing: 33 s, 8 s and 4 s, and the two AddinSdk builds under a second each (recorded as 1 s in
`measuredSeconds`, which is whole seconds, rounded up). The AddinSdk projects must be run with `dotnet msbuild -t:Build`
(as `doc/technotes/addin-sdk.md` says); `dotnet build` of the same file fails because its restore changes what the
asserted targets see.

The workflow initialises only the submodules these entries reach (AvalonEdit, WPF Toolkit and `vscode-wpf` with its
nested WpfDesigner and wxsg), found by following the `ProjectReference` closure. It does not initialise
`externals/monodevelop` (about 750 MB). Widen the step together with the plan.

The workflow builds against the repository's own pins (LibreWPF preview.65 from nuget.org, after removing the
developer-local feed); the preview.69 trial lives in the Terminal workflow only.

## Not planned yet

Integration classes in `tests/OpenDevelop.IntegrationTests` other than the Terminal ones, `XamlDom.Tests.MicrosoftWPF`
(Windows only), and the heavy or environment-bound ones named in `tests/OpenDevelop.IntegrationTests/TESTING.md`
(dotnetup SDK installs, runtime upgrade, performance gates, probes). Each needs its own entry and, for the first
group, a command line in the script.
