# Integration Testing

## What this suite is

`tests/OpenDevelop.IntegrationTests` is a single xunit.v3 project (Microsoft.Testing.Platform
native, not VSTest) that boots the *real* `SharpDevelop.exe` as a child process and drives it
end-to-end over an in-process REST API called the "DevFlow agent" (`LeXtudio.DevFlow.Agent.Core`,
port 9299 by default). There is no mocked `IWorkbench`, no fake pads, no fake project system - the
whole app runs for real, and tests assert on what it actually did (opened a solution, rendered a
file, produced a build result, etc.).

This exists because a lot of past regressions in this codebase (crashes in
`AutoDetectDisplayBinding`, null `FormattingStrategy`, WorkloadAutoImportPropsLocator SDK
resolution failures, etc.) only showed up when the whole app ran together, not in any unit test.
The tradeoff is that this suite is slow (each test class shares one app instance, but starting
that instance takes real seconds) and must be run explicitly, never as part of a fast inner loop.

## Shared fixture and collection

Every test class:

```csharp
[Collection("OpenDevelop app")]
public sealed class SomeTests
{
    readonly OpenDevelopAppFixture _app;
    public SomeTests(OpenDevelopAppFixture app) => _app = app;
}
```

`OpenDevelopAppFixture` (`OpenDevelopAppFixture.cs`) is registered once via
`[CollectionDefinition("OpenDevelop app")]` + `ICollectionFixture<OpenDevelopAppFixture>`, so xunit
starts **one** `SharpDevelop.exe` process for the entire test run and every test class in the
collection shares it. `AssemblyInfo.cs` sets `CollectionBehavior(DisableTestParallelization)` so
xunit never tries to run two test classes against that one shared app at the same time - don't
remove that attribute, and don't add a test class that skips the `[Collection("OpenDevelop app")]`
attribute, or it'll either hang waiting for its own app instance to bind the same port, or run
concurrently against the shared one and corrupt other tests' state (wrong solution open, etc.).

The fixture exposes:

- `InvokeAsync(string action, params object[] args)` - `POST /api/v1/invoke/actions/{action}`,
  unwraps the `returnValue` envelope and parses it as `JsonElement`.
- `GetUITreeAsync()` - `GET /api/v1/ui/tree`, the full WPF visual tree as JSON (see below).
- `GetStatusAsync()` - `GET /api/v1/agent/status`.
- `OpenDevelopProjectPath`, `SolutionExplorerFixturePath`, `DebugTestProjectPath`,
  `SlnxFixturePath`, `WpfSampleSolutionPath`, `GitFixtureTemplatePath`, etc. - paths to
  `tests/fixtures/*` resolved by walking up from `AppContext.BaseDirectory`. Add one of these
  `LocateXxx()` static methods + property when a new test needs its own fixture project.

Prerequisites before running anything in this project:

```bash
dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
dotnet build tests/fixtures/SampleTestProject/SampleTestProject.csproj
```

Some test classes need their own fixture also built first (e.g. `IlSpyAddInTests` needs
`tests/fixtures/DebugTestApp/DebugTestApp.csproj` built) - check the prerequisites comment at the
top of the test file.

## Adding a new DevFlow-driven test case

There is no native-dialog automation for the WPF-embedded DevFlow agent (it can't click an
`OpenFileDialog`), so every flow that would normally start from a menu command with a file picker
needs a DevFlow action that bypasses the dialog and calls the same underlying service directly.

1. **Add the action(s).** Static methods on a `[DevFlowUIThread]`-annotated static class,
   attributed `[DevFlowAction("od.xxx", Description = "...")]`, are auto-discovered by reflection
   and dispatched to the UI thread - no manual router/registration step.
   - App-wide actions (open solution, open file, build, ...) live in
     `src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs`.
   - AddIn-specific actions live in a `<AddIn>DevFlowActions.cs` file inside that addin's project,
     e.g. `src/AddIns/DisplayBindings/ILSpyAddIn/IlSpyDevFlowActions.cs`
     (`od.ilspy.open-assembly`, `od.ilspy.status`),
     `src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfDesignDevFlowActions.cs`. Follow
     this pattern (`od.<addin>.<verb>`) for a new addin rather than dumping everything into the
     shared file.
   - Return `JsonSerializer.Serialize(new { ... })` - an anonymous object, not a raw value - so
     `InvokeAsync` callers can `.GetProperty(...)` off it.
   - Prefer exposing **real service state** (a status snapshot, a tree walk, a cache query) over
     re-deriving something a test could just as easily get from the UI tree. But when the thing
     under test *is* the UI (an icon actually rendering, a pane actually being visible), don't
     shortcut around it by only asserting on backend state - that proves the service works, not
     that the UI reflects it. See `GitAddInTests.OpenSolution_WithGitRepo_OverlayIconsReflectFileStatus`
     for an example: it doesn't call `GitStatusCache`/`IProjectBrowserOverlayService` directly, it
     reads `od.ui.tree` and asserts on the real `AutomationId` that the overlay `<Image>` in
     `ProjectBrowserView.xaml` was bound to - the same data-bound value that produced the on-screen
     icon.
   - If you need a UI-observable property that isn't naturally exposed (icon identity, e.g.), it's
     often better to bind a stable string (a status name, an automation id) onto the real visual
     element in XAML than to add a "read backend state" DevFlow action - the latter can pass while
     the actual UI is broken (wrong binding, wrong converter, etc.).

2. **Add a fixture if the flow needs project/solution content.** `tests/fixtures/<Name>/` - a
   minimal `.sln` + `.csproj` (SDK-style, so file globbing "just works" - no need to hand-list
   `<Compile>` items) is usually enough. Add a `LocateXxx()`/property pair to
   `OpenDevelopAppFixture.cs` following the existing ones. If the scenario needs external state a
   fixture can't hold statically (e.g. a real git working copy with dirty/staged files), build that
   state at test setup time into a **temp copy** of the fixture, not by committing a nested `.git`
   directory or mutated files into this repo (see `GitAddInTests.cs`'s constructor for the
   copy-to-temp-dir + `git init`/`git add`/`git commit` pattern, with `-c user.name=... -c
   user.email=...` so it doesn't depend on global git config, and cleanup in `Dispose()`).

3. **Add the test class.** `[Collection("OpenDevelop app")]`, constructor takes
   `OpenDevelopAppFixture`, call `_app.InvokeAsync(...)`/`_app.GetUITreeAsync()`, assert on the
   returned `JsonElement`. Put an explanatory comment block at the top of the file describing what
   real user-visible flow this covers and why it's driven this way (see any existing test file for
   the expected tone/detail) - this suite's whole value is in each test tracing back to a concrete
   regression or user-visible behavior, not generic coverage.

### Reading `od.ui.tree`

`GetUITreeAsync()` returns `{ "elements": [ ... ] }`, where each element is (camelCase JSON):
`id`, `parentId`, `type` (short CLR type name, e.g. `"TextBlock"`, `"Image"`, `"Grid"`), `fullType`,
`framework`, `automationId`, `text`, `isVisible`, `isEnabled`, `bounds` (`left`/`top`/`width`/
`height`), `nativeProperties`, `frameworkProperties` (Brush-typed properties only), and a nested
`children` array (so the JSON is already a tree, not just a parent-id-linked flat list - but
`parentId` is populated too, useful once you've flattened it). It does **not** expose
`ImageSource`/`Geometry`/tooltips - image content itself is invisible to this API, only the
element's own bound properties (like `AutomationId`) are. That's why UI assertions that care about
"which icon is showing" need a stable string bound onto the element (see `GitAddInTests.cs`), not
image/geometry comparison.

To find a specific file's node in the Project Browser tree: match a `TextBlock` element by
`text == fileName`, take its `parentId` (the `StackPanel` from
`ProjectBrowserView.xaml`'s `HierarchicalDataTemplate`), then find sibling elements under that same
parent id to reach the icon `Grid` and its child `Image`s.

## Running the suite

The project builds to a self-testing executable (`OutputType=Exe`,
`TestingPlatformDotnetTestSupport=true`), so both of these work:

```bash
# MTP-native (fastest path; args after "--" go to the xunit v3 runner, not dotnet)
dotnet run --project tests/OpenDevelop.IntegrationTests --no-build

# Also works (shells out to the same MTP executable)
dotnet test tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -c Debug
```

### Running a single test class or method

**Don't use `dotnet test --filter "FullyQualifiedName~Foo"`** - that's VSTest filter syntax, and
this MTP/xunit3 project doesn't honor it the same way; it silently runs the *entire* suite instead
of just matching tests. Use the xunit v3 runner's own filter flags, passed after `--`:

```bash
# One test class
dotnet run --project tests/OpenDevelop.IntegrationTests --no-build -- -class "OpenDevelop.IntegrationTests.GitAddInTests"

# One test method (fully qualified: Namespace.Class.Method)
dotnet run --project tests/OpenDevelop.IntegrationTests --no-build -- -method "OpenDevelop.IntegrationTests.GitAddInTests.AddInsList_ContainsGitAddIn"

# Or invoke the built exe directly, equivalently:
dotnet tests/OpenDevelop.IntegrationTests/bin/Debug/net10.0/OpenDevelop.IntegrationTests.dll -class "OpenDevelop.IntegrationTests.GitAddInTests"
```

Other useful runner flags (see `-- -help` for the full list): `-namespace "name"`, `-trait
"name=value"`, `-list tests` (enumerate available tests without running them), `-verbose`
(reporter with per-test progress). Wildcards (`*`) are supported at the start/end of `-class`/
`-method`/`-namespace` filter values.

Because of the shared single-app-instance collection, never run two invocations of this project
concurrently - they'll both try to bind the same DevFlow port (9299, override via
`DEVFLOW_AGENT_PORT`) and one will lose.

## Code coverage

```bash
dotnet build tests/OpenDevelop.IntegrationTests -t:Coverage -p:AltCover=true -p:AltCoverInPlace=true
```

Do **not** use `dotnet test /p:AltCover=true` - see the comment at the top of
`OpenDevelop.IntegrationTests.csproj` and `AltCover.Mtp.targets`: AltCover's own VSTest hookup
collides with this project's MTP test target. This produces `coverage.xml` (OpenCover) and
`coverage.cobertura.xml` in the project directory.
