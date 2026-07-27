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

## Open idea: Roslyn-assisted discovery, MTP-confirmed results

MTP-only discovery (`MtpTestProject`/`Simple.TestService.DiscoverTestsForProject`) is authoritative
but slow: it must build the project, spawn a `dotnet exec --server` test host, and wait for it to
connect back and answer `testing/discoverTests` - the existing code already carries a 60s timeout
and an explicit warning message for this (`Simple/TestService.cs`'s `DiscoverTestsForProject`),
because a project that isn't actually MTP-enabled will hit that timeout on every single discovery
pass, with no faster fallback.

The classic (non-Mtp) `TestProjectBase.OnNestedTestsInitialized` takes the opposite tradeoff: a
Roslyn/NRefactory syntax-tree walk over the project's source, looking for attribute-decorated test
classes/methods (`IsTestClass`/`CreateTestClass`/`UpdateTestClass`, implemented per classic
framework - NUnit, MSTest). Fast (no process spin-up, no build required first), but only
*approximate* - it can't expand a parameterized `[Theory]`/`[TestCase]` into its real per-data-row
count, can't produce the MTP `Uid` `RunTestsAsync`/`RunTestsAsync(IReadOnlyList<MtpTestNode>)`
needs to run just one test, and can silently include or miss tests dynamically
generated/filtered at runtime. `MtpTestProject` deliberately skips this path entirely today (see
its `OnNestedTestsInitialized` override comment) - `IsTestClass`/`CreateTestClass`/`UpdateTestClass`
are all no-op stubs returning `false`/`null` for the Mtp framework specifically.

A hybrid worth designing later: populate the tree instantly from a Roslyn attribute scan
(`[Fact]`/`[Test]`/`[TestMethod]`-family attributes, matching whatever `TestProjectDetector`'s
marker-package heuristic already identifies as the active framework), then reconcile in the
background once the real MTP `discoverTests` call returns - replacing/annotating the approximate
Roslyn-derived nodes with the authoritative MTP ones (real `Uid`, real parameterized-test
expansion, catching anything Roslyn's static view couldn't see), the same way
`MtpTestProject.DiscoverTestsAsync`/`PopulateTree()` already replaces
`discoveredNodesByTargetFramework` and rebuilds the tree once MTP discovery completes - just seeded
with a fast approximate tree first instead of an empty one. This would apply to both
`ICSharpCode.UnitTesting`'s tree (`MtpTestProject`) and `Simple.TestService`'s flat list, since
both currently pay the same "empty until the 30-60s MTP round trip finishes" cost with no faster
path.

Not started. Sizing it properly needs: which attributes to scan for per detected framework, how
the Roslyn-derived key maps onto the eventual MTP-derived key so reconciliation doesn't show
duplicate/flickering entries, and whether the pad should show a "confirming..." state for
Roslyn-only entries before MTP settles.
