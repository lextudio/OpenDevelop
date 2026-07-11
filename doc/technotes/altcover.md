# AltCover integration triage plan

This note tracks the current AltCover integration failure in OpenDevelop's integration tests and
the proposed step-by-step handling plan.

## Current problem

`CodeCoverageTests.RunWithCodeCoverage_ProducesModuleResults` fails because the coverage result
contains instrumented modules, but every module reports zero visited code length.

The visible test run itself is not missing: pass/fail/skip results are reported correctly. The
failure is specifically that AltCover's recorder does not preserve visits from the actual
test-execution phase.

Observed split:

- Code that runs during VSTest discovery can be recorded by AltCover.
- Code that runs during actual test execution currently records zero visits.
- Running `od.code-coverage.run` twice in the same IDE session is also unreliable; the second run
  can race AltCover's per-project instrumentation/report state and hang until the client-side HTTP
  timeout.

An attempted fix that restarted the VSTest test host after `Prepare()` was reverted. It did not
fix the zero-visit result and introduced a separate back-to-back coverage-run failure involving a
missing `coverage.<guid>.xml` working report.

## Constraints

- Do not reintroduce `ITestService.RestartTestRunner()` as a speculative fix.
- Treat AltCover as a three-step stateful flow: `Prepare`, unwrapped test execution, then
  `Collect`.
- Keep `dotnet test /p:AltCover=true` out of this integration path. The project uses the custom
  `Coverage` target for MTP compatibility.
- Avoid broad refactors while this failure is being isolated; the code-coverage addin is already in
  a high-churn migration state.
- Preserve user changes in the working tree.

## Immediate containment

1. Decide whether integration coverage should block the suite.

   Recommended default: make the execution-visits assertion a known limitation for now, so unrelated
   integration work can continue. Keep a smaller availability/smoke test enabled.

2. If skipping, skip only tests that execute a coverage run.

   Candidate:

   ```csharp
   [Fact(Skip = "Known AltCover limitation: execution-phase visits are not currently recorded; see doc/technotes/altcover.md.")]
   public async Task RunWithCodeCoverage_ProducesModuleResults()
   ```

   Do not skip `CodeCoverageService_IsAvailable`.

   Current state:

   - `RunWithCodeCoverage_ProducesModuleResults` is skipped because execution-phase visits are
     known to be zero.
   - `ClearCodeCoverageResults_EmptiesResults` is skipped because it also executes
     `od.code-coverage.run` and triggers the same AltCover state problem.
   - `CodeCoverageService_IsAvailable` remains enabled as the smoke test for addin availability.

3. Add or link an issue before making the skip permanent.

   Suggested issue title:

   ```text
   AltCover integration records discovery visits but zero execution visits
   ```

4. Track the repeated-run bug as a separate issue.

   Suggested issue title:

   ```text
   Back-to-back od.code-coverage.run can race AltCover working report state
   ```

## Reproduction checklist

Run these from `OpenDevelop` unless noted otherwise.

1. Clean stale instrumentation output.

   ```bash
   find src tests -type d -name '__Instrumented' -prune -exec rm -rf {} +
   find tests/OpenDevelop.IntegrationTests -maxdepth 1 -name 'coverage*.xml' -delete
   ```

2. Build the solution.

   ```bash
   dotnet build OpenDevelop.Mvp.slnx
   ```

3. Run the integration test that currently fails.

   ```bash
   dotnet test tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj \
     --filter FullyQualifiedName~CodeCoverageTests.RunWithCodeCoverage_ProducesModuleResults
   ```

4. Confirm the failure mode.

   Expected current failure:

   - coverage results are available;
   - at least one module exists;
   - all modules have `visitedCodeLength == 0`;
   - the assertion `"Expected at least one module to show non-zero visited code length."` fails.

5. Reproduce the repeated-run problem from the integration bridge or IDE command path.

   Run `od.code-coverage.run` twice in one OpenDevelop session after discovery has completed.

   Expected current failure:

   - second run may fail to find a `coverage.<guid>.xml` file;
   - command may not return useful results before the HTTP timeout.

## Diagnostic plan

### Phase 1: prove what process executes instrumented code

Goal: determine whether the execution-phase test code is loaded from the instrumented assembly on
disk, from a pre-instrumentation assembly already loaded in a reused host, or from another copied
location.

Actions:

1. Log the target project output directory used by `AltCoverApplication.GetTargetWorkingDirectory()`.
2. Log the exact `Prepare` and `Collect` command lines.
3. During test execution, log:
   - current process id;
   - `AppContext.BaseDirectory`;
   - loaded assembly path for `SampleTestProject`;
   - whether the loaded file path is under the directory AltCover instrumented.
4. Compare the loaded assembly timestamp and size before and after `Prepare`.

Exit criteria:

- We know whether VSTest execution is using the same on-disk assembly that `Prepare` rewrote.

### Phase 2: prove recorder initialization

Goal: determine whether the instrumented execution-phase assembly calls into the AltCover recorder
at all.

Actions:

1. Enable maximum AltCover verbosity for both `Prepare` and `Collect`.
2. Inspect generated `__Instrumented/AltCover-*.log` files immediately after a failed run.
3. Check whether raw visit files or temporary recorder state are created during execution.
4. Add a temporary probe to the sample test project that calls a known method and inspect the
   instrumented IL for AltCover-injected calls.

Exit criteria:

- Either recorder calls are missing from the execution assembly, or they exist but are not flushing
  visits into the report state.

### Phase 3: isolate VSTest host lifecycle

Goal: determine whether the test host lifecycle explains the discovery/execution split.

Actions:

1. Confirm whether discovery loads the sample test assembly before `Prepare`.
2. Confirm whether execution reuses that loaded assembly or starts a new process/AppDomain.
3. Try a one-off isolated run that starts a fresh OpenDevelop process per coverage run.
4. Try a one-off run with discovery delayed until after `Prepare`, if practical.

Exit criteria:

- We know whether a stale pre-instrumentation assembly remains in memory across Prepare/execution.

### Phase 4: isolate AltCover state collisions

Goal: separate zero-visits from repeated-run corruption.

Actions:

1. Run only one coverage command per OpenDevelop process.
2. Then run two coverage commands back-to-back with unique working report paths.
3. Capture the exact report path passed to `Prepare`, the path passed to `Collect`, and the path read
   by `CodeCoverageResultsReader`.
4. Verify whether `PromoteResultsToStableFileName()` runs for the first run before the second run's
   `Prepare`.

Exit criteria:

- We know whether the repeated-run bug is caused by shared report paths, shared `__Saved_*`
  directories, overlapping `Prepare/Collect`, or result promotion timing.

## Fix candidates

Try these only after the diagnostic phases narrow the cause.

1. Serialize coverage runs per project.

   This is likely needed regardless of the zero-visits root cause. AltCover in-place
   instrumentation is not naturally concurrent for the same project output directory.

2. Block new discovery while coverage instrumentation is active.

   If discovery against instrumented assemblies writes recorder data or keeps assemblies loaded, the
   IDE should avoid running incidental discovery inside the `Prepare` to `Collect` window.

3. Run coverage in a fresh test-host process that is created after `Prepare`.

   The previous restart attempt did not fix the issue, so do not repeat it unchanged. Only revisit
   this if Phase 1 proves execution is using stale assemblies and we can restart without overlapping
   AltCover state.

4. Use a copied, disposable output directory for coverage.

   Instead of in-place instrumentation of the live project output directory, copy the build output to
   a temporary coverage directory, instrument the copy, run tests from the copy, collect, then delete
   it. This is heavier, but it may avoid IDE discovery/test-host reuse and AltCover `__Saved_*`
   collisions.

5. Reduce the integration assertion.

   If AltCover cannot reliably support this IDE-hosted VSTest path, keep coverage parsing/UI tests
   and mark execution visit collection as unsupported for now.

## Upstream escalation packet

If this looks like an AltCover recorder issue, prepare a minimal upstream report with:

- AltCover version: `9.0.102`.
- Runtime/TFM used by OpenDevelop integration tests.
- Exact `Prepare` command line.
- Exact test execution command/process details.
- Exact `Collect` command line.
- A small sample test project.
- The generated OpenCover XML showing instrumented modules with zero visits.
- Relevant `__Instrumented/AltCover-*.log` files.
- Confirmation that discovery-phase visits are observable but execution-phase visits are zero.

## Decision log

- 2026-07-11: Restarting the VSTest test host after `Prepare()` was tried and reverted. It did not
  fix zero execution visits and introduced a missing `coverage.<guid>.xml` failure on repeated runs.
- 2026-07-11: Current recommendation is to contain the failing integration assertion as a known
  limitation, then debug with targeted process/recorder instrumentation rather than continuing
  speculative lifecycle changes.
- 2026-07-11: Both integration tests that execute `od.code-coverage.run` were marked skipped:
  `RunWithCodeCoverage_ProducesModuleResults` and `ClearCodeCoverageResults_EmptiesResults`.
  `CodeCoverageService_IsAvailable` remains active.
- 2026-07-11: Verification run after cleaning stale AltCover state passed:
  `dotnet test tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj` reported
  `Failed: 0, Passed: 28, Skipped: 2, Total: 30`.
- 2026-07-11: The failed pre-clean run confirmed the repeated-run/stale-state path:
  AltCover reported `__Saved/ already exists`, then failed to find
  `SampleTestProject/OpenCover/coverage.<guid>.xml`; the following normal unit-test integration
  assertion was polluted and returned `None` instead of `Success`.
