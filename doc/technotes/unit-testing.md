# Unit Testing

## Two implementations, on purpose

This repo carries two separate `ITestService`-shaped things under
`src/AddIns/Analysis/UnitTesting/`, and that's deliberate, not drift:

- **`ICSharpCode.UnitTesting`** (`Service/ITestService.cs`/`SDTestService.cs`, `Model/*.cs`,
  `MtpTestFramework.cs`/`MtpTestProject.cs`/`MtpTestRunner.cs`) - the classic, tree-shaped
  abstraction: `ITestFramework.IsTestProject`/`CreateTestProject` registered per-framework via
  AddInTree (`/SharpDevelop/UnitTesting/TestFrameworks`), `ITestSolution` → `ITestProject` →
  `ITest` (namespace/class/method) tree, driving the WPF `UnitTestsPad`/`TestTreeView`. Built for
  multiple simultaneously-supported frameworks (NUnit, MSTest, MSpec, MTP) and a tree UI.
- **`ICSharpCode.UnitTesting.Simple`** (`Simple/ITestService.cs`/`TestService.cs`/
  `TestProjectDetector.cs`/`DotNetTestRunner.cs`) - a flat, MTP-only contract: `GetTests()` returns
  a plain `IReadOnlyList<TestInfo>`, `RunTestsAsync(IReadOnlyList<string> fullyQualifiedNames)`
  takes keys, not `ITest` objects. Built for a single always-MTP backend and a native list-style
  test panel with no tree UI at all.

This was originally two independent implementations - UnoDevelop had its own local
`ITestService`/`TestService`/`TestProjectDetector`/`DotNetTestRunner` fork, OpenDevelop had (and
still has) the classic tree-shaped one. On investigation (2026-07-27) the *literal* duplication
turned out to be narrower than "the whole test service is forked twice": `MtpTestRunner.RunAsync`
and `DotNetTestRunner.RunTestsAsync` ran the same discover→filter→run MTP sequence, and
`MtpTestProject.ResolveAssemblyDll`/`GetTargetFrameworks` solved the same "where's the built test
assembly" problem `DotNetTestRunner.ResolveOutputAssembly`/`TestService.ResolveTargetFrameworks`
did (less reliably - by scanning `bin/` for the newest `.dll` instead of reading MSBuild's
evaluated `OutputPath`/`AssemblyName`). The two `ITestService` *interfaces* themselves are not
duplicates of each other; they're different, both-intentional shapes for different consumers (a
tree pad vs a flat pad).

Rather than force UnoDevelop's flat pad to speak the tree contract (candidate rejected - would
replace an already-correct, simpler design with a mismatched heavier one, just to make it "reuse"
something), the flat implementation was moved here as `Simple/` (namespace
`ICSharpCode.UnitTesting.Simple`, so it can't collide with the classic `ICSharpCode.UnitTesting`
types) and UnoDevelop links it back via `$(SharpDevelopSourceRoot)`, same pattern as `Mtp/*.cs`.
Both `ICSharpCode.UnitTesting.SDTestService`/`ITestFramework`/`MtpTestFramework` and
`ICSharpCode.UnitTesting.Simple.TestService` remain live and necessary:

- OpenDevelop's own `UnitTestsPad`/`TestTreeView`, `Profiler`/`CodeCoverage` AddIns
  (`RunTestWithProfilerCommand`, `RunTestWithCodeCoverageCommand`, `RunAllTestsWithCodeCoverageCommand`),
  and `OpenDevelopDevFlowActions.cs` all consume the classic tree service - deleting it breaks
  OpenDevelop.
- UnoDevelop's native `TestResultsPad`/DevFlow actions consume `Simple/ITestService` - that's the
  one now-shared, no-longer-forked implementation.

The genuinely duplicate MTP-driving logic (`MtpTestRunner` vs `DotNetTestRunner`,
`MtpTestProject`'s assembly/TFM resolution vs `TestService`'s) is still forked today; unifying
*that* narrower slice (not the two `ITestService` interfaces) is a real follow-up, not attempted
in this pass.

## Implemented: Roslyn-assisted discovery, MTP-confirmed results

**Status: done for `Simple.TestService` (2026-07-27). Not done for the classic
`ICSharpCode.UnitTesting`/`MtpTestProject` tree** - same idea applies there (see "Still open"
below), just not implemented yet.

MTP-only discovery (`MtpTestProject`/`Simple.TestService`'s old `DiscoverTestsForProject`) is
authoritative but slow: it must build the project, spawn a `dotnet exec --server` test host, and
wait for it to connect back and answer `testing/discoverTests` - the existing code already carried
a 60s timeout and an explicit warning message for this, because a project that isn't actually
MTP-enabled hits that timeout on every single discovery pass, with no faster fallback.

The classic (non-Mtp) `TestProjectBase.OnNestedTestsInitialized` takes the opposite tradeoff: a
Roslyn/NRefactory syntax-tree walk over the project's source, looking for attribute-decorated test
classes/methods. Fast (no process spin-up, no build required first), but only *approximate* - it
can't expand a parameterized `[Theory]`/`[TestCase]` into its real per-data-row count, can't
produce the MTP `Uid` needed to run just one test, and can silently include or miss tests
dynamically generated/filtered at runtime.

### What was built

`Simple/RoslynTestScanner.cs` - a syntax-tree-only scan (`CSharpSyntaxTree.ParseText` per `.cs`
file under the project directory, `bin`/`obj` excluded, no semantic model, no compilation, no
build) that looks for methods carrying any of a fixed set of test-method attribute short names
(`Fact`/`Theory`, `Test`/`TestCase`/`TestCaseSource`, `TestMethod`/`DataTestMethod` - xunit, NUnit,
MSTest; TUnit's `[Test]` matches the same NUnit-shaped name) and returns `RoslynTestCandidate`
(`TypeFullName`, `MethodName`, `DisplayName`) records - deliberately the same shape MTP's own
`DisplayName` takes (`Namespace.Class.Method`), so results read consistently whichever source
produced them.

`Simple/TestService.cs`'s `GetTests()` was restructured around this:

1. `DiscoverTestsForProjectApprox` runs the Roslyn scan synchronously (fast enough to not need
   backgrounding) and returns `TestInfo` entries with `Uid: null` - these are what the *first*
   `GetTests()` call after a refresh returns.
2. For each test project, a background `Task.Run` immediately kicks off
   `ConfirmProjectAsync`/`DiscoverTestsForProjectViaMtpAsync` - the original MTP-based discovery,
   now genuinely `async` (no more `.GetAwaiter().GetResult()` blocking the caller).
3. When MTP confirmation completes, `ConfirmProjectAsync` replaces that project's entries in
   `_cachedTests` (by `ProjectPath`) with the authoritative, `Uid`-bearing ones, then fires a new
   `TestsConfirmed` event on `ITestService`.
4. A `_generation` counter (bumped by `RefreshTests()`) is captured by each confirmation task at
   launch and checked before it's allowed to write back - a `RefreshTests()` call that happens
   while a confirmation is still in flight makes that confirmation's eventual result a no-op
   instead of resurrecting stale data into a newer discovery pass's cache.

`TestResultsPad.cs` subscribes to `TestsConfirmed` (alongside the existing `TestResultUpdated`/
`TestRunStarted`/`TestRunCompleted`) and re-fetches+rebuilds when it fires. The ordering this
produces is deliberately "whoever's ready first paints first, the merge happens after there's
already something on screen" - not "wait for both, then show the merged result": `RefreshTestsAsync`
already shows the Roslyn-approximate list the moment `GetTests()` returns (no waiting on MTP at
all), and each project's `TestsConfirmed` firing independently updates just that project's rows
once its own MTP host answers, rather than waiting for every test project in the solution to
finish confirming before updating any of them.

Unconfirmed (Roslyn-only) entries have `Uid: null`; `RunTestsAsync`'s existing
`.Where(uid => !string.IsNullOrEmpty(uid))` filter already turns "select an unconfirmed test and
run it" into an empty `testUids` list, which `DotNetTestRunner`/`MtpServerProcess` already treat as
"run everything in this project" - a safe over-approximation (never a silent skip), not new
behavior added for this feature.

### Measured benefit

`RoslynTestScannerTests.cs` (`UnoDevelop.Core.Tests`) scans the real xunit/NUnit/MSTest fixture
projects (`Tests/fixtures/Sample{Xunit,NUnit,}MtpTests`) and asserts both correctness (finds
exactly the `[Fact]`/`[Test]`/`[TestMethod]`-decorated methods, none of `Calculator.cs`'s
un-annotated methods) and speed (`ScanProject_IsFastEnoughToSeedTestServiceCache`: a 2-file fixture
scans in low single-digit milliseconds in practice, asserted under a generous 5s ceiling to avoid
CI flakiness - the real-world contrast being the 30-60s an MTP round trip can take, not a few
hundred milliseconds either way). End-to-end, `UnoDevelop.IntegrationTests`' `UnitTesting`/
`TestPanel` tests dropped from ~32s to ~21s wall-clock with this change, and the full 70-test suite
still passes (`ide-refresh-tests` no longer blocks on the MTP round trip before returning anything).

### Still open

- The classic `ICSharpCode.UnitTesting`/`MtpTestProject` tree doesn't have this yet -
  `IsTestClass`/`CreateTestClass`/`UpdateTestClass` are still no-op stubs there (see
  `MtpTestProject`'s `OnNestedTestsInitialized` override comment). Same idea applies: seed
  `PopulateTree()` from a Roslyn pass before the first `DiscoverTestsAsync` round trip lands.
- `RoslynTestScanner` re-parses every `.cs` file from scratch on every `GetTests()`-triggered
  refresh; fine at fixture scale, worth revisiting (e.g. an mtime-based per-file cache) if it's ever
  used against a large real-world solution.
- No key-based reconciliation beyond "replace this project's entries wholesale" -
  `ConfirmProjectAsync` doesn't try to match a specific Roslyn candidate to its confirmed
  counterpart (e.g. to preserve a `Running`/`Passing` result across the swap); it just removes and
  re-adds by `ProjectPath`. Not observed to matter in practice (`_lastResults` is keyed separately
  and survives), but noted in case a future symptom traces back here.
