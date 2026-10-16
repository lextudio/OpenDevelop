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
[Collection("30 Add-ins and specialized fixtures")]
public sealed class SomeTests
{
    readonly OpenDevelopAppFixture _app;
    public SomeTests(OpenDevelopAppFixture app) => _app = app;
}
```

`OpenDevelopAppFixture` (`OpenDevelopAppFixture.cs`) is registered via
`[assembly: AssemblyFixture(typeof(OpenDevelopAppFixture))]` in `AssemblyInfo.cs`, so xunit starts
**one** `OpenDevelop.exe` process for the entire test run and every test class shares it.
`AssemblyInfo.cs` also sets `CollectionBehavior(DisableTestParallelization)` and registers
`FixtureTestCollectionOrderer`/`FixtureTestCaseOrderer`, which pin the cross-class and in-class
execution order (each class assumes the app state its predecessors left). `xunit.runner.json`
additionally sets `parallelizeTestCollections: false` — belt and braces; don't remove either, or
two collections will run against that one shared app at the same time and corrupt each other's
state (wrong solution open, focus stolen mid-gesture, scratch files appearing in another test's
fixture, etc.). Don't add a test class that skips the `[Collection(...)]` attribute either.

The fixture exposes:

- `InvokeAsync(string action, params object[] args)` - `POST /api/v1/invoke/actions/{action}`,
  unwraps the `returnValue` envelope and parses it as `JsonElement`.
- `GetUITreeAsync()` - `GET /api/v1/ui/tree`, the full WPF visual tree as JSON (see below).
- `GetStatusAsync()` - `GET /api/v1/agent/status`.
- `OpenDevelopProjectPath`, `SolutionExplorerFixturePath`, `DebugTestProjectPath`,
  `SlnxFixturePath`, `WpfSampleSolutionPath`, `GitFixtureTemplatePath`, etc. - paths to
  `tests/fixtures/*` resolved by walking up from `AppContext.BaseDirectory`. Add one of these
  `LocateXxx()` static methods + property when a new test needs its own fixture project.

Prerequisites before running anything in this project (the build scripts this summarises are
documented in [build-from-source.md](build-from-source.md) - keep the two in sync):

```bash
# App + shared assemblies (equivalent to ./launch.ps1 -BuildOnly):
dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
dotnet build tests/fixtures/SampleTestProject/SampleTestProject.csproj
```

Tests that assert the **Microsoft** designer backends additionally need those hosts built, which
`dotnet build`/`launch.ps1` do not produce - use `./dist.ps1 -Phase designer-hosts` (Windows) or
`./build.ps1 <MicrosoftHost project>`.

Some test classes need their own fixture also built first (e.g. `IlSpyAddInTests` needs
`tests/fixtures/DebugTestApp/DebugTestApp.csproj` built) - check the prerequisites comment at the
top of the test file.

## Platform and runtime matrix

The designers run on several UI runtimes, and the same test body can be meaningful on more than one. The matrix to
cover is:

| Runtime (the "cell") | Windows | macOS | Selected by (the project, never the host OS; `OD_WINUI_RUNTIME` only orders implementations within the WinUI family; see [designer-common.md](designer-common.md)) |
|---|---|---|---|
| LibreWPF | yes | yes | `LibreWPF.Sdk` project |
| Microsoft WPF | yes | no | `Microsoft.NET.Sdk` + `UseWPF` project |
| LibreWinForms | yes | yes | `UseMicrosoftDesktopRuntime=false`, or `LibreWPF.Sdk`/`LibreWinForms.Sdk` |
| Microsoft WinForms | yes | no | `UseMicrosoftDesktopRuntime=true`, or a `-windows` TFM |
| Microsoft WinUI | yes | no | `UseWinUI` (Windows App SDK) project, and `OD_WINUI_RUNTIME=microsoft` |
| Uno Platform | yes | yes | `Uno.Sdk` / `Uno.WinUI` project |
| ProGPU.WinUI | yes | yes | `OD_WINUI_RUNTIME=progpu` |

A cell is a runtime, not an OS. An OS is a column: a test that runs on Windows has not thereby run against Microsoft
WPF, and one that runs on macOS has not thereby run against "the" LibreWPF. The XamlDom unit suite learned this the hard
way (its "Windows run" was LibreWPF all along; see [xaml-loader-comparison.md](xaml-loader-comparison.md) and its
per-runtime hosts), and this suite must not repeat it.

### Where the suite stands

This is what exists, so the plan below is a delta and not a rewrite:

- **Microsoft cells (WPF, WinForms, WinUI):** `[Trait("DesignerBackend", "Microsoft")]` is applied 20 times: to 18 test
  methods in `AddInTests` and to the whole `WinUIGalleryDesignerTests` and `WpfGalleryDesignerTests` classes. Twelve of
  the 18 methods skip off Windows (`SkipUnless = nameof(MicrosoftDesignerBackendsAvailable)`). **The other six are plain
  `[Fact]`s that deliberately run everywhere** ("`WpfSampleSolutionPath` is the LibreWPF sample off Windows"), so the trait
  does not mean "Microsoft backend" for them: off Windows they run against LibreWPF, and `RunMicrosoftDesignerIntegration`
  selects them anyway. (One of them, `WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`, used to assert
  `backend == "LibreWPF"` unconditionally, which by reading could not hold on Windows; fixed, see Progress.) The Microsoft cells are run on Windows by the csproj target
  `RunMicrosoftDesignerIntegration`, which builds the Microsoft design hosts first and sets
  `OD_FORMS_RUNTIME`/`OD_WPF_RUNTIME`/`OD_WINUI_RUNTIME=microsoft` (see [TESTING.md](../../tests/OpenDevelop.IntegrationTests/TESTING.md)).
- **Libre cells:** they mostly run as "the default" in an unfiltered run, and **which backend that is depends on the host
  OS**: `WpfSampleSolutionPath` is the Microsoft WPF sample on Windows and `LibreWpfSample` elsewhere (before the step 1
  work below, `WaitForWpfDesignerStatusAsync` also defaulted its expected backend from `OperatingSystem.IsWindows()`; it
  now defaults to `OpenDevelopAppFixture.WpfSampleCell`, which encodes the same rule in one place). So an unfiltered Windows run exercises Microsoft WPF, an unfiltered macOS run exercises LibreWPF,
  and **LibreWPF and LibreWinForms on Windows are covered only where a test names the Libre fixture explicitly**.
  `FormsMenuEditingTests` used `OperatingSystem.IsWindows()` as a stand-in for "Microsoft WinForms" (now `MicrosoftCell`, see
  Progress), and `FormsDesignSolutionPath` is the same OS-dependent split for WinForms (`WinFormsSample` on Windows,
  `LibreWinFormsSample` elsewhere; its cell is `OpenDevelopAppFixture.FormsDesignCell`).
- **ProGPU.WinUI:** `ProGpuWinUIDesignerTests` skip unless the whole process runs with `OD_WINUI_RUNTIME=progpu`. They have no OS
  gate, so this suite does not confirm the macOS column for this cell; it is the target, not a measured fact.
- **Uno:** the default WinUI order prefers the Uno child, so Uno is covered implicitly by the WinUI tests of
  `AddInTests`; `UnoHotReloadIntegrationTests` is skipped on purpose.

### Plan

1. **Every designer test declares its cell, and the fixture checks it.** Add traits `Runtime=<cell>` (`LibreWpf`,
   `MicrosoftWpf`, `LibreWinForms`, `MicrosoftWinForms`, `MicrosoftWinUI`, `Uno`, `ProGpuWinUI`) next to the existing
   `DesignerBackend` one (keep `DesignerBackend=Microsoft` working until the runner scripts move). After the designer
   opens, assert the backend the app reports (`od.wpf-designer.status` `backend`, the Forms/WinUI equivalents)
   equals the declared cell (`RuntimeCell.AssertWpfBackend`/`AssertWinFormsBackend`; the old
   `AddInTests.AssertDesignerBackend` took its expectation from `OD_FORMS_RUNTIME`, which no longer selects the backend, and
   was removed). A test that silently ran on another
   backend then fails loudly, which is the integration-level version of `TestHostIdentityTests`.
2. **Stop inferring the backend from the OS.** Replace `OperatingSystem.IsWindows()`-as-backend checks (the
   `expectedBackend ??=` default, `FormsMenuEditingTests`, the `WpfSampleSolutionPath` split, and the six
   cross-everywhere `DesignerBackend=Microsoft` methods) with an explicit cell passed by the test and a fixture chosen per
   cell; split each of those six into one test per cell it is meant for. OS checks stay only for "this cell cannot exist
   on this OS".
3. **One fixture project per cell, chosen by the project rule.** Most already exist as explicit fixtures on
   `OpenDevelopAppFixture` (`LibreWpfSampleSolutionPath`, `MicrosoftWpfSampleSolutionPath`, `UnoXamlSampleSolutionPath`,
   `ProGpuWinUISampleSolutionPath`, `WinUISampleSolutionPath`; `XamlLanguageService_EachRuntime_UsesItsOwnServerAndControls`
   already loops over them). New tests use these, never the OS-dependent `WpfSampleSolutionPath`; add the missing
   ones (the WinForms pair is `WinFormsSample`/`LibreWinFormsSample`) rather than parametrize one sample across runtimes,
   because the file's runtime comes from the project that contains it. A portable sample owns real copies of its XAML, not
   links.
4. **Run one cell per process, sequentially.** The shared single app instance and DevFlow port 9299 forbid concurrent
   runs, and `OD_WINUI_RUNTIME` is a whole-process setting, so a cell is a separate invocation. Provide one entry point
   (an MSBuild target next to `RunMicrosoftDesignerIntegration`, or a script) that loops over the cells this OS supports
   and passes `-trait "Runtime=<cell>"`, building each cell's prerequisites first (the Microsoft hosts via Visual Studio's
   MSBuild, as today).
5. **Report skips per cell.** Cells absent on an OS are excluded by trait filter (not by in-test `Assert.Skip`) so a
   macOS run does not show Microsoft cells as skipped-and-green; a cell that is meant to run but skips (missing host,
   missing env var) is a failure of the run, not a skip.

Order of work: (1) first, since it makes every later change safe; then (2) for WPF, (2) for WinForms, then WinUI/Uno/ProGPU
via the same traits.

### Progress

- **Step 1, WPF cells (started: foundation plus three tagged tests and one tagged class; compiled, not run):** `tests/OpenDevelop.IntegrationTests/RuntimeCell.cs` names the seven
  cells, defines the `Runtime` trait, and provides `RuntimeCell.AssertWpfBackend(status, cell)`. Tagged so far:
  `MicrosoftWpfProject_UsesMicrosoftSurfaceHost`, `DragToolboxItem_OntoXamlSourceEditor_InsertsMarkupAtDropPoint` and
  `WpfGalleryDesignerTests` as `Runtime=MicrosoftWpf`; `LibreWpf_PropertiesPadEditAndSave_ChangesOnlyTheEditedAttribute`
  as `Runtime=LibreWpf`. `WaitForWpfDesignerStatusAsync` now takes a cell (default: the cell of the OS-dependent
  `WpfSampleSolutionPath`, exposed as `OpenDevelopAppFixture.WpfSampleCell`) and asserts through the helper, as do the
  gallery tests. The hard-coded `backend == "LibreWPF"` in `WpfDesigner_ResizeDrag_...` became
  `AssertWpfBackend(status, _app.WpfSampleCell)`, which removes the Windows failure described above.
- **Still open for WPF:**
  - the six cross-everywhere `AddInTests` methods keep their OS-dependent fixture and have no `Runtime` trait, because
    one `[Fact]` cannot carry a different cell per OS; they have to be split per cell (the drag tests drive the pointer
    with cliclick, so check what each cell needs before splitting);
  - `WpfStripEditingTests` (56 uses of `WpfSampleSolutionPath`), `DesignerEditorJourneyTests` (2) and `WorkbenchTests` (1)
    use the OS-dependent fixture and are untagged;
  - `WpfHotReloadJourneyTests` asserts `framework == "WPF"` on the hot-reload session and, with
    `WpfDesignerAssemblyLockTests`, uses `WpfHotReloadFixturePath` with no `Runtime` trait; decide whether `framework` is a
    design-host backend (then it belongs to this matrix) or a hot-reload adapter name (then document it as such).
- **Step 1, WinForms cells (started; compiled, not run):** `RuntimeCell.AssertWinFormsBackend` and
  `OpenDevelopAppFixture.FormsDesignCell` mirror the WPF ones. `FormsMenuEditingTests` now asks the cell instead of the OS
  (`MicrosoftCell`): its three Microsoft-only tests (`PopupItem_Click_Edit_Cancel_Commit_Undo`,
  `PopupTypeHere_Click_CreateItem_AndUndoRemovesIt`, `StatusStrip_InsertionNode_Click_AddsItem_AndUndoRemovesIt`) carry
  `Runtime=MicrosoftWinForms` and keep skipping on LibreWinForms (a product limitation, not a platform one), and its fixture
  asserts the backend after the designer loads. The first `AddInTests` test that used `AssertDesignerBackend` asserts the
  fixture's cell instead.
- **Still open for WinForms:** `ComponentTray_ContainsStripLevelObjects_NotToolStripItems` and the 11 `AddInTests` tests that
  use `FormsDesignSolutionPath` (plus one in `DesignerEditorJourneyTests`) run on whichever cell the OS picks and are
  untagged, for the same reason as the WPF six; the VB WinForms sample (`VbWinFormsSampleSolutionPath`) has not been
  classified.
- **Step 1, WinUI family (started; compiled, not run):** `RuntimeCell.AssertWinUIBackend` checks the `backend` that
  `od.winui-designer.status` reports (`WinUI`, `Uno`, `ProGPU`). Tagged: the 21 `AddInTests` methods that open the Uno
  sample (`OpenUnoXamlFile_...` and every `WinUIDesigner_*`, through `OpenUnoDesignerAsync`) as `Runtime=Uno`; the eight
  `WinUIOnly_*` methods and `WinUIGalleryDesignerTests` as `Runtime=MicrosoftWinUI`; `ProGpuWinUIDesigner_UsesTheSharedDesignCanvas`
  and `ProGpuWinUIDesignerTests` as `Runtime=ProGpuWinUI`. The two `Assert...RenderedBySelectedBackend` helpers were replaced
  by the cell assertion. `WinUIXamlDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`, which picks Uno or Microsoft
  WinUI from `OD_WINUI_RUNTIME`, asserted backend `"WinUI"` even for the Uno document it opens by default (it could only fail
  there); it now asserts the cell its own switch selects, and stays untagged because one method serves two cells.
- **Steps 4 and 5, the runner (written; one cell run for real):** `OpenDevelop.IntegrationTests.csproj` has two targets next to
  `RunMicrosoftDesignerIntegration`, which stays as it is.
  - `dotnet msbuild tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -t:RunRuntimeCell -p:RuntimeCell=<cell>`
    runs one cell in its own process: it rejects an unknown cell or one that cannot exist on this OS, builds the Microsoft hosts
    for a Microsoft cell, sets the process-wide switch the cell needs (`OD_WINUI_RUNTIME=microsoft|uno|progpu`, and the
    `OD_WPF_RUNTIME`/`OD_FORMS_RUNTIME` the Microsoft target already set), lists the cell's tests first and **fails if there are
    none**, then runs `-trait "Runtime=<cell>" -failSkips`, so a test skipped inside a cell the OS supports fails the run
    (`-p:RuntimeCellAllowSkips=true` turns that off).
  - `-t:RunRuntimeMatrix` runs every cell this OS supports in turn (Windows: all seven; macOS: LibreWpf, LibreWinForms, Uno,
    ProGpuWinUI), carries on after a failing cell and ends failed if any failed.
  - `-p:NoBuild=true` skips the build and `-p:RuntimeCellListOnly=true` only lists, which starts no app.
  - Verified on Windows (arm64) with list-only: an unknown cell and a missing `RuntimeCell` fail with the list of valid cells;
    LibreWpf 1, MicrosoftWpf 7, MicrosoftWinForms 3, MicrosoftWinUI 14, Uno 21 and ProGpuWinUI 4 tests are selected;
    **LibreWinForms selects none and fails**, which is correct and shows the gap (no LibreWinForms test is tagged yet); the
    matrix target ran all seven, reported that one failure and exited 1. Not run: the macOS cells, every cell with more than
    one test, and the Microsoft-host build step of a Microsoft cell (every check above used `NoBuild`).
  - One real run, `-p:RuntimeCell=LibreWpf -p:NoBuild=true`: the app started, the single test ran and the failure came back
    through MSBuild with exit code 1. The test's backend assertion passed (`backend` was `LibreWPF`), but the designer never
    loaded (`designerLoaded=false`, `hostProcessId=0`, `hostAlive=false`): **the LibreWPF design host did not start on Windows
    arm64 in this run.** That is the Windows x LibreWPF cell nothing covered before; the cause is not investigated (the host
    is deployed under `AddIns/DisplayBindings/WpfDesign/Host`, and a manual `dotnet exec` of it stays up waiting for input).
- **Still open for the WinUI family:** the dual-cell resize test above; `OD_WINUI_RUNTIME` still selects the cell for a whole
  process (`microsoft`, `progpu`), which the runner entry point (plan step 4) has to set per cell.

## Test stability rules

Each rule below exists because skipping it produced real, repeated failures (2026-08 stability
pass: 31-49 flaky failures per full run → 16, with every rule tracing to a measured root cause).

1. **Mutating tests operate on a temp copy of the fixture, never on the tracked one.**
   Writing a scratch file into `tests/fixtures/...`, editing a fixture `.csproj`, renaming files,
   or building an intentionally-broken file leaks across runs AND across later tests that read
   the same directory (measured: a leftover broken scratch file turned the next clean-build
   assertion into CS0246 noise about missing types; a search's matchCount shifted under another
   test's scratch files). Use `WorkbenchTests.OpenIsolatedSampleAppCopyAsync()` (copy-on-write +
   reopen + delete-in-finally) or the copy-to-temp pattern from `AddInTests`' constructor. The
   tracked fixture is for READ-only scenarios.

2. **Cross-file Roslyn actions don't need a readiness wait - but only because the app syncs.**
   `od.find-references` / `od.rename-symbol` / `od.extract-interface` search the language
   service's WHOLE workspace, and editors upsert their documents asynchronously on attach, so a
   headless caller that opened N files and immediately searched used to race that pipeline and
   legitimately get zero results. `GetSyncedLanguageServiceAsync` (in
   `OpenDevelopDevFlowActions.cs`) now upserts every open document first, making those actions
   deterministic. If you add another workspace-wide language action, route it through the same
   helper. `od.language-workspace.status` reports whether a given file is tracked, for diagnosis.

3. **Synthetic pointer input needs `od.activate` before EVERY attempt, not once upfront.**
   `OD_TEST_MODE=1` launches with `ShowActivated=false`; cliclick input only routes to the app
   when it is actually frontmost, and focus silently drifts back between attempts (measured: a
   drag/double-click retry loop failing 6 times in a row, then passing once activate ran again
   immediately before the gesture).

4. **Aim pointer gestures with PointToScreen-accurate query actions, never with
   `od.ui.tree` bounds.** The generic visual-tree walk reports stale/offset bounds for
   virtualized controls - measured: clicks aimed at tree coordinates for the Properties pad's
   Events grid landed one-to-three rows away (binding 'Closed'/'Scroll' instead of 'Shown').
   Use the dedicated bounds actions (`od.property-pad.query-event-row-bounds`,
   `od.wpf-toolbox.query-item-bounds`, `od.wpf-designer.query-element-screen-bounds`,
   `od.file.query-offset-screen-position`), which compute via the element's own
   `PointToScreen`. If none exists for your target, add one following that pattern rather than
   trusting tree bounds.

5. **Success criteria for UI-editing tests should be persisted state, not transient pad state.**
   The Events-row double-click test originally polled the pad's own `HandlerName`, which refreshes
   lazily via the project system - so a SUCCESSFUL bind looked like a failure and the retry loop
   kept re-clicking (each further click re-virtualized the list and could un-realize the target
   row). Assert on saved file content (`od.file.save` then read disk) instead.

6. **Fixture readiness = agent up AND workbench ready.** The DevFlow agent binds inside the App
   constructor, long before the workbench finishes layout/pad creation. `OpenDevelopAppFixture`
   therefore waits for `od.active-view` to answer successfully and stably
   (`WaitForWorkbenchReadyAsync`) after `WaitForAgentAsync`; without it, the first few facts of a
   run can fail instantly against a half-loaded workbench. Note `WaitForPortFreeAsync` matches
   both process names ("OpenDevelop" and legacy "SharpDevelop") - keep that if the binary is
   ever renamed again.

7. **When a test asserts designer capabilities, keep it in sync with the surface.** The WPF
   designer gained undo/redo/multi-select/layout ops over time while its test still asserted
   `supported=false` for them - the stale assertions failed with KeyNotFoundException once the
   response shape changed. When you implement a previously-unsupported capability, update the
   "unsupported reports" test in the same change; leave exactly one genuinely-unsupported case
   (nudge) asserting the deterministic `supported=false` shape.

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
