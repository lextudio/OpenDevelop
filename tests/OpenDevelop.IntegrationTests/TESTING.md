# Integration test organization

The suite optimizes expensive boundaries, in this order:

1. one `OpenDevelopAppFixture` and one application process for the assembly;
2. one solution open for a complete read-only workflow;
3. one build, test run, coverage run, or debug launch for all assertions that can observe it;
4. a separate fixture project only when the project shape is itself the behavior under test.

## Scenario rules

- A fact is a user journey, not a single assertion. After opening or building, assert the command
  result, model state, rendered UI state, output, diagnostics, and persistence in that same fact.
- Use `EnsureSolutionOpenAsync` for read-only continuation on a fixture. Use
  `ReopenSolutionAsync` only when reopening is the behavior or the reset is required for isolation.
- Keep destructive scenarios on a temporary copy and combine all mutations that form one journey.
- Do not rely on fact ordering or state left by another fact. Sharing happens inside a fact; the
  application process may be shared across facts, but correctness must not depend on execution order.
- Split a fact only when the next assertion needs a contradictory initial state, a different project
  shape, or recovery from a deliberately corrupted state.
- `FixtureTestCaseOrderer` is the canonical execution contract. It first fixes the test-class
  workflow order, then orders scenarios inside mixed classes by fixture and lifecycle stage. Never
  rely on declaration order, file order, or xUnit's default order.
- New test classes and fixture-name patterns must be added to the orderer. Unknown classes and
  scenarios sort last by their fully-qualified method name, so their order is still deterministic.

## Fixture policy

The long-term fixture set is organized by project shape rather than feature name:

- `SolutionExplorerFixture`: general C# solution, editor, tree, search, build, diagnostics and
  Roslyn scenarios. Prefer extending this fixture over adding another ordinary C# application.
- `SampleTestProject` plus `CoverageLib`: test discovery/execution and coverage. Coverage needs a
  referenced library because the self-hosted test assembly is intentionally not instrumented.
- `DebugTestApp`: deterministic executable and source lines for debugger journeys.
- `FSharpFixture` and `VBFixture`: language-specific project systems; these cannot be represented
  by the C# fixture.
- WPF and WinForms samples: designer-specific project systems and generated-code behavior.
- `RuntimeUpgradeApp`, `GitFixture`, and `NuGetFixture`: mutable templates copied to temporary
  directories. They exist to protect tracked files and provide isolated external state.

`SlnxFixture` should only cover multi-project `.slnx` parsing that the general fixture cannot cover.
Before adding a fixture, document the project-shape difference that prevents extending one above.

## Running

Build the application first because the fixture launches it with `--no-build`, then use the xUnit v3
in-process runner:

```bash
dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug --no-restore
dotnet run --project tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -- -class OpenDevelop.IntegrationTests.DebuggerIntegrationTests -parallel none
```

Do not use `dotnet test` for this .NET 10 project.

Building `SharpDevelop.csproj` is **not** sufficient on its own. The addins are runtime-discovered
plugins, not ProjectReferences of the app, so building only the app leaves their `AddIns/...`
folders empty - and an undeployed addin fails its tests with symptoms that read like product bugs
("no language service for file", a DevFlow action returning 404, "runtime host is not installed",
an empty `SimpleViewContent` instead of an editor). Building **this test project** deploys
everything the suite needs: it builds the addins, the out-of-process designer hosts, the fixture
apps that tests assume are prebuilt, and packs the local NuGet feed fixture. Prefer
`dotnet build tests/OpenDevelop.IntegrationTests/...` over building the app alone.

Tests carrying `[Trait("DesignerBackend", "Microsoft")]` exercise the Microsoft WinForms/WPF/WinUI
backends, which only exist on Windows. They skip themselves elsewhere (see
`AddInTests.MicrosoftDesignerBackendsAvailable`), so an unfiltered run is correct on every
platform; on Windows, `RunMicrosoftDesignerIntegration` in the csproj runs them with the
`OD_*_RUNTIME=microsoft` variables set.
