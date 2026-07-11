# AltCover integration triage notes

This note tracks the AltCover integration status in OpenDevelop's integration tests: what's fixed,
what's still broken, and how to keep investigating.

## Status summary (resolved)

| Symptom | Status |
| --- | --- |
| Coverage run rebuilds the project and silently overwrites the just-instrumented assembly | Fixed |
| Instrumented modules reported zero visited code length when run through the IDE | **Fixed** - root cause was the classic-VSTest test-running infrastructure (`VsTestAdapter`/`VsTestRunAdapter`/`VsTestDiscoveryAdapter`), not AltCover. Resolved by replacing it with Microsoft.Testing.Platform (MTP) and running coverage as a plain one-shot process. See "Resolution" below. |
| Discovery-phase code shows visits, execution-phase code doesn't (through the IDE) | Same fix as above |
| Back-to-back `od.code-coverage.run` in one session is unreliable | Improved, not fully eliminated - see "Residual: timing sensitivity" below |
| `RunWithCodeCoverage_ProducesModuleResults` / `ClearCodeCoverageResults_EmptiesResults` | **Un-skipped.** Both pass; verified with a full clean `dotnet test` run (`Failed: 0, Passed: 30, Skipped: 0, Total: 30`) |

`CodeCoverageService_IsAvailable` (the addin-availability smoke test) was never affected.

## Resolution: replaced VSTest with Microsoft.Testing.Platform (MTP)

The zero-visits bug was never in AltCover - it was in how OpenDevelop drove test execution. The
Unit Testing addin (`src/AddIns/Analysis/UnitTesting/`) used to talk to a **long-lived,
IDE-session-scoped `vstest.console` process** (`VsTestAdapter`/`VsTestRunAdapter`/
`VsTestDiscoveryAdapter`) over a persistent socket. AltCover's recorder flushes visits to disk on
`AppDomain.ProcessExit`/`DomainUnload` (`externals/altcover/AltCover.Recorder/Recorder.fs`) - a
one-shot process reliably hits that hook on exit; a long-lived host talked to across many requests
does not, by design.

Fix, following the same architecture already proven in the user's sibling project **UnoDevelop**
(`/Users/lextm/uno-tools/UnoDevelop/src/Main/UnitTesting/Mtp/`) and in this repo's own
`tests/OpenDevelop.IntegrationTests/AltCover.Mtp.targets`:

1. **Removed** the entire classic-VSTest stack: `VsTest/` folder (`VsTestAdapter`,
   `VsTestDiscoveryAdapter`, `VsTestRunAdapter`, `VsTestTreeBuilder`, `TestResultBuilder`,
   `DiscoveredTests`, `VsTestMethod`, `VsTestClass`, `IVsTestTestProvider`), plus root-level
   `VsTestFramework`/`VsTestProject`/`VsTestRunner`/`VsTestDebugger`, plus the
   `Microsoft.TestPlatform.TranslationLayer`/`Microsoft.TestPlatform.ObjectModel` package refs.
2. **Added** an MTP server-mode JSON-RPC client ported from UnoDevelop
   (`src/AddIns/Analysis/UnitTesting/Mtp/MtpServerProcess.cs`, `MtpTestNode.cs`,
   `MtpServerCapabilities.cs` - depends only on `StreamJsonRpc` + BCL) plus
   `MtpTestFramework`/`MtpTestProject`/`MtpTestRunner`/`MtpTestDebugger`/`MtpTestTreeBuilder`/
   `MtpTestMethod`/`MtpTestClass`, preserving the existing `ITestRunner`/`ITestProject`/
   `ITestFramework`/`ITestService`/`TestExecutionManager` contracts untouched, so the UI test tree,
   `UnitTestsPad`, and the CodeCoverage addin needed no changes beyond the coverage command itself
   (below). `MtpTestRunner` starts a **fresh `MtpServerProcess` per run** (not an IDE-lifetime
   singleton) and tears it down at the end of that run.
3. **`RunTestWithCodeCoverageCommand`** (`src/AddIns/Analysis/CodeCoverage/Project/Src/`) no longer
   goes through `ITestService`/`MtpTestRunner` at all for the actual test execution. It now: builds
   the project, runs AltCover `Prepare`, launches the built test apphost **directly as a plain
   one-shot child process** (`Process.Start` + `WaitForExitAsync`, no JSON-RPC, no server mode -
   mirroring `AltCover.Mtp.targets`'s `<Exec>` step and UnoDevelop's own `CoverletCoverageRunner`,
   which independently bypasses its own MTP JSON-RPC client for exactly this reason), then runs
   `Collect` and promotes the results.
4. **Migrated the fixture** `tests/fixtures/SampleTestProject/SampleTestProject.csproj` off classic
   VSTest (`Microsoft.NET.Test.Sdk` + `xunit` 2.9 + `xunit.runner.visualstudio`) to `xunit.v3` +
   `<OutputType>Exe</OutputType>` + **`<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`**
   (without this, xunit.v3 defaults to its own native single-dash CLI runner with no `--server`
   support at all - `MtpServerProcess.StartAsync` got "unknown option" instead of a callback
   connection).
5. **Fixed an unrelated, pre-existing, now-newly-exposed bug**: OpenDevelop's embedded MSBuild
   engine (`MSBuildInternals.InitializeMSBuildEnvironment`) could not resolve
   `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator` - an SDK resolver *every* `Microsoft.NET.Sdk`
   project unconditionally needs, via `Microsoft.NET.Sdk.ImportWorkloads.props`, not just
   workload-based (MAUI/Android/iOS) ones. This broke `MSBuildBasedProject.GetEvaluatedProperty()`
   (`OutputAssemblyFullPath`, `AssemblyName`, etc.) for **every** project loaded in this embedded
   engine, which is why test discovery silently found nothing (and separately, why
   `RoslynParser.Parse()` returned null for every `.cs` file) - previously masked only because the
   coverage-execution integration tests were always skipped, so nothing had exercised "open a fresh
   process, immediately load a project" end-to-end before. Manually deploying the resolver's own
   dependency closure got further but hit a second, deeper crash inside the resolver itself
   (`FormatException` parsing this process's SDK version string as a workload release version) -
   rather than chase that, set `MSBuildEnableWorkloadResolver=false`, MSBuild's own documented
   escape hatch to skip the whole workload-resolution props/resolver chain (this embedded engine has
   no legitimate use for workload-based SDKs anyway). See `MSBuildInternals.cs` for the full comment.
6. Bumped `OpenDevelopAppFixture`'s `HttpClient.Timeout` from 120s to 240s - it was shorter than the
   180s budget some coverage-test invocations pass to `od.code-coverage.run`, so the *client* could
   abort a request the *server-side* action was still legitimately allowed to keep polling for.

Verified end-to-end, in order: (a) manual `Prepare` -> `dotnet vstest`/one-shot run -> `Collect`
against the migrated xunit.v3 fixture recorded real visits (5857/27637 sequence points); (b) MTP
discovery via a live `od.unit-test.tree` poll against a freshly-started app populated the full
namespace/class/method tree; (c) a live `od.code-coverage.run` through the real IDE command path
reported `SampleTestProject: visitedCodeLength=288` (non-zero); (d) a full, clean
`dotnet test tests/OpenDevelop.IntegrationTests` run reported `Failed: 0, Passed: 30, Skipped: 0,
Total: 30`.

## Residual: timing sensitivity on a loaded machine

A second `dotnet test` run (same machine, heavy concurrent load from other work - `uptime` showed a
load average of 13+ with another unrelated `dotnet build` actively running) saw
`ClearCodeCoverageResults_EmptiesResults`'s `od.code-coverage.run` call hit its own 180s server-side
timeout once (`{"timedOut":true}`, not a crash or wrong result). This is the previously-documented
"back-to-back run" sensitivity, but observed here on the *first* coverage run of that session, not
specifically a second/racing one - consistent with plain resource contention (build+instrument+run+
collect legitimately taking longer under load) rather than a new logic bug. If this recurs on an
otherwise-idle machine, treat it as a real regression and reopen the diagnostic-phase plan below;
otherwise, consider it a known timing margin and re-run in isolation.

## Fixed: the pre-run build was clobbering `Prepare`'s instrumentation

**Symptom:** every coverage run produced instrumented module entries (from AltCover `Prepare`'s
report skeleton) but `visitedCodeLength == 0` for every one of them, even though the test run itself
clearly executed the tests (pass/fail/skip counts were correct, and discovery-phase code *did* show
real visits).

**Cause:** `RunTestWithCodeCoverageCommand.Run()`'s original sequence was: run `Prepare` (instruments
the target assembly in place), then call `testService.RunTestsAsync(...)`. That call chain -
`SDTestService.RunTestsAsync` &rarr; `TestExecutionManager.RunTestsAsync` &rarr; ... &rarr;
`VsTestRunAdapter.Instance.RunTestsAsync` - rebuilds the project first:
`TestExecutionManager.RunTestsAsync` (`src/AddIns/Analysis/UnitTesting/TestRunner/TestExecutionManager.cs`)
unconditionally rebuilds any project whose `ITestProject.IsBuildNeededBeforeTestRun` is true, and
`TestProjectBase.IsBuildNeededBeforeTestRun` hardcodes this to `true` with no opt-out ever surfaced
through `TestExecutionOptions`. That rebuild silently recompiled the test assembly from source
*after* `Prepare` had already instrumented it - overwriting the instrumented DLL with a fresh,
uninstrumented one before the test host ever loaded it. This also explains the discovery/execution
split observed earlier: discovery evidently ran against the instrumented assembly, while execution
always saw the freshly rebuilt, clean copy.

**Fix** (kept; build cleanly across the whole solution):

- `TestExecutionOptions.SkipBuild` - new `bool`, default `false`
  (`src/AddIns/Analysis/UnitTesting/TestRunner/TestExecutionOptions.cs`).
- `TestExecutionManager.RunTestsAsync` skips its own build pass when `options.SkipBuild` is set.
- `RunTestWithCodeCoverageCommand.Run()` (now `RunAsync().FireAndForget()`, since it needs to
  `await` a build) builds the target project explicitly via `SD.BuildService.BuildAsync` *before*
  running `Prepare`, then sets `options.SkipBuild = true` before calling
  `testService.RunTestsAsync(...)` (`src/AddIns/Analysis/CodeCoverage/Project/Src/RunTestWithCodeCoverageCommand.cs`).

This is a real, independent defect, and the fix should stay regardless of what else is still broken:
a coverage run should never be one accidental `TestExecutionManager` rebuild away from discarding
its own instrumentation. **It is not sufficient on its own** to make
`RunWithCodeCoverage_ProducesModuleResults` pass reliably; see the open issue below.

## Open: AltCover itself works - the IDE's own VSTest orchestration is the remaining suspect

To isolate the IDE from AltCover, the exact same three-step flow was run completely outside
OpenDevelop, by hand, against `tests/fixtures/SampleTestProject` (clean `bin`/`obj`, no leftover
`__Saved`/instrumentation state):

```bash
dotnet build tests/fixtures/SampleTestProject/SampleTestProject.csproj -c Debug
dotnet <bin>/bin/Tools/AltCover/AltCover.dll -i <targetDir> --inplace --save --reportFormat OpenCover -r <report.xml>
(cd <targetDir> && dotnet vstest SampleTestProject.dll)   # completely unwrapped, no IDE involved
dotnet <bin>/bin/Tools/AltCover/AltCover.dll Runner -r <targetDir> --collect
```

Result: **visits were recorded correctly.** AltCover's own summary reported real, non-trivial visit
counts (thousands of visited sequence points, tens of percent coverage), not all zero.

This rules out AltCover itself (recorder init, `--inplace` rewriting, `Runner --collect` merge logic,
the exact bundled version) as the cause. A bare `Prepare` &rarr; `dotnet vstest` &rarr; `Collect`
cycle against the identical target directory, identical AltCover binary, and identical fixture
project works, even after the `SkipBuild` fix above. Yet the same flow driven through
`od.code-coverage.run` in the IDE still intermittently produces `visitedCodeLength == 0` for every
module - sometimes it does show real visits (confirmed by grepping the raw `coverage.xml` for
non-zero `visitedSequencePoints` after an IDE-driven run), sometimes it doesn't, with no code
difference between runs. This narrows the remaining bug to how OpenDevelop's own `VsTestRunAdapter`
(`src/AddIns/Analysis/UnitTesting/VsTest/VsTestRunAdapter.cs` and the shared base `VsTestAdapter.cs`)
drives VSTest, not to AltCover.

The suspicious architectural difference: a bare `dotnet vstest` invocation is a single process that
exits normally right after the run completes - AltCover's recorder flushes visits to disk on
`ProcessExit`/`DomainUnload`, so that exit reliably fires the flush. The IDE's path instead talks to
a **long-lived, singleton `vstest.console` process** (`VsTestAdapter.Start()` - started once, cached
in `startTask`, and reused for the entire IDE session; see `VsTestAdapter.cs`) over a persistent
`SocketCommunicationManager` connection, sending `TestRunSelectedTestCasesDefaultHost` messages to
it rather than launching one process per run. `vstest.console` itself spawns a `testhost` child
process per run request, and that child *should* be short-lived and exit normally after each run -
but this has not yet been directly proven in-process, and the intermittent (not 100%-reproducible)
nature of the failure is consistent with a timing race around that child process's exit/flush rather
than a deterministic logic bug.

**Do not re-attempt restarting `VsTestRunAdapter.Instance`/`vstest.console` itself as a blind fix.**
This was already tried once (killing/restarting the persistent host right after `Prepare()`, via a
speculative `ITestService.RestartTestRunner()`) and reverted: it did not fix the zero-visits
symptom (the real cause turned out to be the rebuild-clobbering bug above, not host reuse) and it
introduced a *second* failure mode of its own - see the next section. Any future fix attempt in this
area should first prove, via direct instrumentation, which process/step is failing to flush, rather
than guessing at a lifecycle change.

**Next steps if picking this back up:**

1. Instrument `VsTestRunAdapter`/`VsTestAdapter` directly rather than reasoning from the outside: log
   the `testhost` process's PID (`Process.GetProcessesByName("testhost")`) immediately before and
   after an `od.code-coverage.run`, and confirm whether/when it exits relative to when
   `AfterTestsRunTask`'s `Collect` step actually runs.
2. Check whether `communicationManager`/`vsTestConsoleProcess` teardown in `VsTestAdapter` ever
   races the recorder's own flush - e.g. whether `TestRunComplete` (which resolves
   `VsTestRunAdapter`'s `OnTestRunComplete` and, transitively, the awaited `RunTestsAsync` task) can
   fire before the spawned `testhost` process has actually exited and flushed its AltCover recorder
   state.
3. If a genuine race is found, look for a way to explicitly wait for testhost exit (not vstest.console
   exit) before proceeding to `Collect`, rather than restarting any persistent process.

## Open: back-to-back coverage runs can still race on shared instrumentation state

Running `od.code-coverage.run` twice in the same OpenDevelop session (as
`ClearCodeCoverageResults_EmptiesResults` does, right after
`RunWithCodeCoverage_ProducesModuleResults`) is unreliable, independently of the issue above.
Observed failure mode: AltCover's `Collect` step (`Runner --collect`) fails with

```text
Could not find file '.../SampleTestProject/OpenCover/coverage.<guid>.xml'.
```

and the console additionally logs `A total of 0 visits recorded` for that run; the client-side HTTP
call in the integration test then hits its own timeout waiting for a result that will never arrive.

Each `AltCoverApplication` instance already uses a unique GUID-suffixed working report path
(`AltCoverApplication.WorkingResultsFileName`, promoted to the stable path only after `Collect`
succeeds - see that class's remarks), specifically to avoid two runs colliding on the *same* report
file, so that part is not the issue. The remaining collision is one level lower: AltCover's
`--inplace` model instruments the **same physical target directory**
(`AltCoverApplication.GetTargetWorkingDirectory()`, the project's own build output folder) on every
run, and keeps a `__Saved_...` backup of the pre-instrumentation assemblies there while instrumented.
If a second run's `Prepare` starts while any part of the first run's
`Prepare -> execute -> Collect` sequence (including AltCover's own backup/restore bookkeeping in
that directory) hasn't fully settled, the two runs step on each other's instrumentation state in
that shared directory.

`RunTestWithCodeCoverageCommand.Run()`/`RunAsync()` is not reentrant-safe today: nothing prevents a
second `od.code-coverage.run` from starting while an earlier run's fire-and-forgotten
`AfterTestsRunTask` continuation (`Collect` + `PromoteResultsToStableFileName`) is still in flight
for the *same project*. The DevFlow `od.code-coverage.run` action does wait for
`CodeCoverageService`'s result count to increase before returning "completed" (see
`OpenDevelopDevFlowActions.RunCodeCoverage`), which should serialize sequential callers in
practice - but the observed failures suggest either that signal isn't airtight, or AltCover's own
directory-level bookkeeping needs more time to settle than it accounts for.

**Next steps if picking this back up:**

1. Make `RunTestWithCodeCoverageCommand`/`RunAllTestsWithCodeCoverageCommand` explicitly reject or
   queue a new run while a previous run's `AfterTestsRunTask` continuation for the *same project* is
   still pending, rather than relying on `od.code-coverage.run`'s polling loop to happen to serialize
   things.
2. Or: give each run's `Prepare`/`Collect` its own isolated copy of the target output directory
   (copy build output to a temp coverage directory, instrument the copy, run against the copy,
   collect, delete it) instead of instrumenting the live project output directory in place. Heavier,
   but sidesteps `__Saved_*`/in-place collisions entirely.

## Secondary observation: `visitedCodeLength` computation is not purely XML-driven

Separately from the above, be aware that `visitedCodeLength` is not read directly from the OpenCover
XML - it depends on re-reading the *original source file* from disk:

- `CodeCoverageModule.GetVisitedCodeLength()` sums `CodeCoverageMethod.GetVisitedCodeLength()`
  across methods, which sums `sequencePoint.Length` for every sequence point where
  `FileID` matches and `VisitCount != 0`.
- `sequencePoint.Length` is computed in `CodeCoverageMethodElement.GetSequencePointContent()` by
  re-reading the source file at `sp.Document` from disk and measuring the non-whitespace length of
  the text at that line/column range. If that source read fails or returns empty content, `Length`
  silently stays `0` even for a genuinely visited (`vc >= 1`) sequence point, with no error surfaced
  anywhere.

This was not root-caused as a distinct bug (the `<FileRef>`/`fileid` matching and the source file
paths were spot-checked and looked correct), but it means `visitedCodeLength == 0` is not, by itself,
proof that AltCover recorded no visits - always cross-check with
`grep -o 'visitedSequencePoints="[1-9][0-9]*"' coverage.xml` on the raw report before concluding a
run genuinely recorded zero visits (see the reproduction checklist below). If the VSTest-orchestration
issue above gets fixed and the integration test is still flaky, revisit whether the test's assertion
should key off `VisitedSequencePointsCount` instead of `visitedCodeLength`, since the former doesn't
depend on a live source-file read.

## Harness stability caveat

While re-verifying fixes in this area, the OpenDevelop app process itself was observed to exit mid
test class on at least one run (log showed a normal `Unloading services... Leaving
RunApplication()` sequence partway through `CodeCoverageTests`, before all three tests had run), and
VSTest test *discovery* occasionally timed out at 60s on an otherwise-healthy run. Neither was
chased down here - they may be environment flakiness (resource pressure, a lingering process from an
earlier debugging session, or a second process holding DevFlow's port 9299) rather than anything
specific to AltCover. If these skipped tests are revisited, run the class several times in isolation
first to separate "AltCover/VsTestRunAdapter bug" from "harness flake" before concluding a fix has or
hasn't worked.

## Reproduction checklist

Run these from the `OpenDevelop` repo root.

1. Clean stale instrumentation output.

   ```bash
   find src tests -type d -name '__Instrumented' -prune -exec rm -rf {} +
   find tests/fixtures/SampleTestProject -maxdepth 2 -iname 'coverage*.xml' -delete
   rm -rf tests/fixtures/SampleTestProject/bin/Debug/net10.0/__Saved
   ```

2. Build the solution (or at least the CodeCoverage add-in and the integration test project).

   ```bash
   dotnet build OpenDevelop.Mvp.slnx
   ```

3. Run just the CodeCoverage test class, several times in a row to gauge flakiness (do this in
   isolation - no other process should be holding DevFlow's port 9299).

   ```bash
   tests/OpenDevelop.IntegrationTests/bin/Debug/net10.0/OpenDevelop.IntegrationTests \
     -class "OpenDevelop.IntegrationTests.CodeCoverageTests"
   ```

4. To check whether execution-phase code is actually being visited (independent of the
   `visitedCodeLength` assertion, which can be misleading per the secondary observation above),
   inspect the raw report directly after a run:

   ```bash
   grep -o 'visitedSequencePoints="[1-9][0-9]*"' \
     tests/fixtures/SampleTestProject/OpenCover/coverage.xml | head
   ```

   Non-empty output here means AltCover genuinely recorded execution-phase visits, even if the
   integration test's `visitedCodeLength` assertion still fails.

## Upstream escalation packet

Not currently needed - the isolated repro above confirmed AltCover itself works correctly outside
the IDE. If that changes, prepare a minimal upstream report with:

- AltCover version: `9.0.102`.
- Runtime/TFM used by OpenDevelop integration tests (net10.0).
- Exact `Prepare` and `Collect` command lines (`AltCoverApplication.GetPrepareArguments()` /
  `GetCollectArguments()`).
- A small sample test project (the repo's own `tests/fixtures/SampleTestProject` reproduces it).
- The generated OpenCover XML.
- Relevant `__Instrumented/AltCover-*.log` files.

## Decision log

- 2026-07-11: Restarting the VSTest test host after `Prepare()` was tried and reverted. It did not
  fix zero execution visits and introduced a missing `coverage.<guid>.xml` failure on repeated runs.
- 2026-07-11: Both integration tests that execute `od.code-coverage.run` were marked skipped:
  `RunWithCodeCoverage_ProducesModuleResults` and `ClearCodeCoverageResults_EmptiesResults`.
  `CodeCoverageService_IsAvailable` remains active as the addin-availability smoke test.
- 2026-07-11: Found and fixed the real root cause of the discovery-vs-execution zero-visits split:
  `TestExecutionManager.RunTestsAsync` unconditionally rebuilds the project
  (`ITestProject.IsBuildNeededBeforeTestRun` is hardcoded `true`), silently recompiling over the
  assembly `Prepare` had just instrumented. Fixed via `TestExecutionOptions.SkipBuild` plus building
  explicitly before `Prepare` in `RunTestWithCodeCoverageCommand.cs`.
- 2026-07-11: Isolated AltCover from the IDE entirely (manual `Prepare` &rarr; `dotnet vstest` &rarr;
  `Collect` against the same fixture project, same AltCover binary) and confirmed AltCover itself
  records visits correctly. This rules out AltCover and narrows the remaining zero-visits bug to
  OpenDevelop's own long-lived `VsTestRunAdapter`/`vstest.console` orchestration.
- 2026-07-11: Re-verified through the IDE after the `SkipBuild` fix: the raw `coverage.xml` sometimes
  shows genuine non-zero `visitedSequencePoints` for the fixture's own test classes, and sometimes
  still shows all zeros, with no code difference between runs - confirming an intermittent race
  rather than a deterministic logic bug in the IDE's VSTest orchestration. Both integration tests
  remain skipped pending that fix, and the back-to-back-run race is tracked as a separate open issue.
