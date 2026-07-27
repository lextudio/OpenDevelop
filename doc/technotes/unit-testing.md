# Unit Testing

## One backend: the classic `ICSharpCode.UnitTesting` tree

There is exactly one unit-testing backend in this repo, under
`src/AddIns/Analysis/UnitTesting/`: the classic, tree-shaped `ICSharpCode.UnitTesting`
(`Service/ITestService.cs`/`SDTestService.cs`, `Model/*.cs`, `MtpTestFramework.cs`/
`MtpTestProject.cs`/`MtpTestRunner.cs`). `ITestFramework.IsTestProject`/`CreateTestProject` are
registered per-framework through the AddInTree path `/SharpDevelop/UnitTesting/TestFrameworks`, and
the model is a tree: `ITestSolution` → `ITestProject` → `ITest` (target framework / class / method).
Both hosts consume it — OpenDevelop's WPF `UnitTestsPad`/`TestTreeView` and UnoDevelop's native
`TestResultsPad` — and UnoDevelop links the source in via `$(SharpDevelopSourceRoot)` rather than
keeping a port of its own.

### Why this one, and not the flat contract

There used to be a second, flat, MTP-only contract (`ICSharpCode.UnitTesting.Simple`:
`ITestService.GetTests()` returning `IReadOnlyList<TestInfo>`, `RunTestsAsync` keyed by
fully-qualified name). It began as a UnoDevelop-local fork and was briefly hosted here so both
hosts could share it. Keeping two `ITestService`-shaped abstractions alive was the wrong end state:
it left the discover→filter→run MTP sequence and the "where is the built test assembly" problem
implemented twice, in two places that drifted independently.

The classic tree was kept as the single backend because it is a strict superset, not a peer:

- It already drives features the flat contract never modelled — the `Profiler` and `CodeCoverage`
  AddIns (`RunTestWithProfilerCommand`, `RunTestWithCodeCoverageCommand`,
  `RunAllTestsWithCodeCoverageCommand`) consume `ITest`/`ITestProject` objects, not name strings.
- It supports several frameworks side by side via AddInTree registration, where the flat one
  hard-assumed MTP.
- `TestCollection` gives composite result roll-up (a failing method colours its class, project and
  the "All Tests" root) for free; a flat list has nowhere to roll up to.
- Both pads are trees anyway. UnoDevelop's `TestResultsPad` renders a `TreeView`, so the flat
  contract was being re-expanded into a tree at the UI layer regardless.

`Simple/RoslynTestScanner.cs` is the one piece of that namespace that survived, because it isn't an
alternative backend — it's the fast-discovery mechanism described below, now consumed by
`MtpTestProject`.

## Roslyn-assisted discovery, MTP-confirmed results

MTP discovery is authoritative but slow: it needs a built assembly, a `dotnet exec --server` test
host process, and a `testing/discoverTests` round trip — tens of seconds, and a project that isn't
actually MTP-enabled burns the full timeout on every pass. A syntax-tree scan is the opposite
trade-off: instant, but approximate (it can't expand a parameterized `[Theory]`/`[TestCase]` into
its real per-data-row count, can't produce the MTP `Uid` needed to run a single test, and can't see
tests generated or filtered at runtime).

So the tree does both, in order:

1. `MtpTestProject.PopulateApproxTreeFromRoslyn` runs `RoslynTestScanner.ScanProject`
   synchronously (`CSharpSyntaxTree.ParseText` per `.cs` file, `bin`/`obj` excluded, no semantic
   model, no build) and populates the tree immediately. Nodes are synthesised with a deliberately
   **empty** `Uid` — never a made-up value — so `MtpTestRunner` can detect an unconfirmed selection
   with `string.IsNullOrEmpty` and fall back to "run everything in this project" (a safe
   over-approximation, never a silent skip) rather than needing a separate flag threaded through
   `MtpTestNode`/`MtpTestMethod`.
2. `TriggerDiscovery` starts the real MTP pass. When it completes it replaces
   `discoveredNodesByTargetFramework` and rebuilds the tree with `Uid`-bearing nodes.

Discovery otherwise runs only once per project (lazily, on first `NestedTests` access) and again
after each build (`OnBuildFinished`).

### Discovery is awaitable, and cancellable

`MtpTestProject.RefreshAsync(CancellationToken)` returns a task that completes when the tree
reflects the MTP host's answer. This matters more than it looks:

- **Awaitable.** Discovery used to be fire-and-forget (`var _ = DiscoverTestsAsync()`), which left
  callers no way to know when it finished — so they polled the tree and guessed. That is not just a
  test-harness annoyance: `TestResultsPad.RefreshTestsAsync` fired the passes off and rebuilt the
  tree *immediately*, so the explicit "Refresh Tests" action never actually displayed refreshed
  results. It now awaits every project's pass before rebuilding.
- **Cancellable.** The token reaches `MtpServerProcess`, so the host process round trip can be
  abandoned. `OperationCanceledException` is caught separately from real failures: a user-requested
  cancellation leaves the tree exactly as it was (keeping the Roslyn approximation) and is not
  logged as a discovery failure.

The pad surfaces that cancellation to the user: `RefreshTestsAsync` creates its status-bar progress
via `IStatusBarService.CreateCancellableProgressMonitor(cts)`, and the status bar shows a Cancel
button for as long as that monitor lives. The seam exists because a bare `CancellationToken` only
lets the UI *observe* cancellation — to *request* it the operation has to hand over its
`CancellationTokenSource`, which is what `ProgressCollector`'s CTS constructor plus
`IsCancellable`/`Cancel()` provide. Both hosts implement the service method, so the behaviour is
shared rather than per-UI.

### Locating the built test assembly

`MtpTestProject.ResolveAssemblyDll` builds a list of candidate paths and returns the first that
exists on disk, because no single rule is right for every project model:

- MSBuild's evaluated `OutputPath` is TFM-qualified for multi-targeted projects but not always for
  single-TFM ones, so both shapes are tried.
- Project models that don't derive from `MSBuildBasedProject` can report a TFM-less
  `OutputAssemblyFullPath` (`bin/Debug/X.dll`) while the SDK actually writes
  `bin/Debug/<tfm>/X.dll`.

One trap is worth calling out, since it silently disabled MTP discovery on macOS/Linux entirely:
MSBuild writes **Windows separators** into `OutputPath` (`bin\Debug\`) on every platform, and
`Path.Combine` does not translate them. On Unix the backslashes stay literal, so the result names a
single absurd directory (`bin\Debug`) that cannot exist — every target framework was skipped, and
because the empty result then overwrote the tree, the project node ended up permanently empty.
Hence `NormalizeDirectorySeparators`, and hence `DiscoverTestsAsync` refusing to replace a populated
tree with an empty result: when no TFM yields an assembly the loop `continue`s past every `await`,
so that overwrite ran *synchronously* inside `OnNestedTestsInitialized` and wiped the Roslyn
candidates the line above had just added.

### Measured benefit

`RoslynTestScannerTests.cs` (`UnoDevelop.Core.Tests`) scans the real xunit/NUnit/MSTest fixture
projects (`Tests/Fixtures/Sample{Xunit,NUnit,}MtpTests`) and asserts both correctness (finds exactly
the `[Fact]`/`[Test]`/`[TestMethod]`-decorated methods, none of `Calculator.cs`'s un-annotated ones)
and speed (a 2-file fixture scans in low single-digit milliseconds, asserted under a generous 5s
ceiling to avoid CI flakiness — the contrast being the tens of seconds an MTP round trip takes).

`UnitTestingCodeCoveragePadIntegrationTests` exercises discovery and a coverage run against a real
MTP fixture end to end. Replacing its polling with `RefreshAsync` took it from two tests each
burning a 120s deadline (~4m15s for the suite) to sub-second discovery and a ~3s coverage run.

### Still open

- `RoslynTestScanner` re-parses every `.cs` file on each refresh; fine at fixture scale, worth an
  mtime-based per-file cache if it is ever pointed at a large real-world solution.
- No key-based reconciliation between an approximate node and its confirmed counterpart — the MTP
  pass replaces a project's nodes wholesale rather than matching them up (e.g. to preserve a
  per-test result across the swap). Not observed to matter, noted in case a symptom traces back
  here.
- `IsTestClass`/`CreateTestClass`/`UpdateTestClass` remain no-op stubs on `MtpTestProject`: the
  parser-driven incremental-update path of `TestProjectBase` is unused, since discovery is driven by
  the Roslyn scan plus MTP rather than by `ParseInformationUpdated`.
