# WinUI designer: where it stands and the plan

Companion to `winui-designer-landscape.md` (the evaluation and the measurements behind every claim here) and `hot-reload.md`
(the XAML Diagnostics agent). This note is the plan: what is built, what is next, in what order, and what we decided not to do.
Dates are 2026-11.

## 1. Direction

- **Text-authoritative, design-time.** The XAML text is the document. Every design action is a source edit, undoable, and the
  preview is a re-render of the text. This is also what Microsoft proposes for v1 (microsoft/WinAppVSCE#56), which we read as: a
  design-time engine ("B4b") that renders, hit-tests and reports bounds and layout metadata, plus a controller that turns gestures
  into minimal text edits. Their runtime designer (manipulating the running app) is explicitly deferred to later.
- **Our own B4b.** The upstream engine is not available, so the Surface renderer (the `lextudio/WinAppVSCE` fork, package
  `LeXtudio.WinUISurface`) is extended to be one. Every capability is in frame mode: no native window, no per-click excursion.
- **Live means no restart.** An edit re-parses in the running renderer (`UpdateXaml`, about 300 ms). A feature that needs a host
  restart to show an edit is the wrong feature (this is why the compiled-page route was removed).
- **Real `{x:Bind}` is a hot-reload problem**, not a renderer one: it needs a running compiled app patched in place.
- **No upstream PRs to Microsoft.** Decided by the owner. The fork stays ours; nothing is proposed to `microsoft/WinAppVSCE`.

## 2. Built and verified

| Capability | Where | Evidence |
|---|---|---|
| Surface renderer as the default Microsoft WinUI runtime (Options can choose Microsoft host), matched host per project, private run copies | `WinUIXamlDesigner.SurfaceHost`, `.Child`, fork provisioner | live in the IDE; unavailable payload is reported, never replaced by another host (7.1) |
| Described frames: authored element tree with bounds (package 0.3.0) | `describe: true`, `FrameTree.cs`; `SurfaceDesignDocument` | 313 ms update with the tree; IDE outline resolves names |
| Hit-test and selection from the frame (no native mode) | `SurfaceDesignDocument.HitTestAsync` | fake-surface and real-surface DDP tests |
| Layout metadata (package 0.4.0): margin, parent kind, Grid row/col/span and tracks, Canvas position, z-index, orientation | `FrameLayout` -> `GridTracks`, `LayoutInsets`, `ZIndex` | real-surface probe of Grid, Canvas, StackPanel |
| Property catalog: the renderer's full property list per element in the Properties pad | `DescribeElement`, `design/describe-properties`, `WinUIXamlElementPropertyAdapter` | IDE: about 70 properties for a Button, enum and boolean dropdowns, an edit through the pad |
| Property pad depth: renderer values in the value cell, catalog-typed editors, attached layout properties | `WinUIXamlElementPropertyAdapter` (value fallback, type-driven converters, attached offer), `od.winui-designer.properties-pad.properties` | 6 UI-fact tests; IDE: 14/14 - FontSize 14 from the renderer, Brush/CornerRadius/FontFamily editors, Grid and Canvas attached properties authored and undone |
| Move and resize as source edits | `WinUIXamlMarginEditor.TryDelta` (Margin, alignment-aware), `ApplyElementBounds`; hooks `nudge`, `resize` | IDE: +20,+10 move, 98x32 -> 200x48 resize, one Undo restores. Pointer: a real press/stepped drag/release moved 60x30 and authored `Margin="60,30,0,0"`, a handle drag grew 98x32 to 168x72 with `Width`/`Height`, one Undo each |
| Toolbox drop at a point: the measured Grid cell (`Grid.Row`/`Grid.Column`), `Canvas.Left`/`Top`, or the StackPanel child index, as one source edit | `WinUIXamlDropPlacement`, `WinUIXamlDocumentEditor.Insert(..., before, attached)`, hook `drop` | 60 tests; IDE: a Button dropped at (600,400) in `DropGrid` authored `Grid.Row="1" Grid.Column="1"`, at (30,20) in `DropCanvas` authored `Canvas.Left="30" Canvas.Top="20"`, one Undo each |
| Edits keep the author's layout (declaration, line endings, untouched tags) | `WinUIXamlDocumentEditor.PreserveLayout` | 47 tests, `tests/OpenDevelop.WinUIXamlDocument.Tests` |
| Native-live window over the canvas (opt-in), follows the canvas, falls back to the last snapshot if the renderer dies | `UnoDesignRuntimeHost.NativeLive.cs`, the shared `DesignerCanvas` `NativeLive` toggle, `od.winui-designer.native-live` | IDE: the toolbar toggle enters/leaves, the window lands on the visible canvas at the same DPI, a click reaches the renderer's process, it follows a pan, and killing the renderer falls back to snapshots (12/12 checks) |
| Compiled preview with real `{x:Bind}` (opt-in, `OD_WINUI_COMPILED=1`) | `SurfaceConnection.CompiledPageType`, `SurfaceDesignDocument` (`x:Class` -> `compiledType`), the fork's compiled page, `tests/fixtures/WinUIDesignerFixture/Microsoft/BoundPage.xaml` | IDE: the bound TextBlock reports "Bound from code-behind" (empty in markup mode); a source edit does not change it until the project is rebuilt; 21.5 s warm open vs 13 s markup |
| Outline selection by element name, and the unnamed root | `DocumentOutlineControl.ResolveDisplayedId`, `WinUIXamlDesignerViewContent.SelectSourceElement`/`OnOutlineSelectionChanged` | 3 UI-fact tests; IDE: clicking `[Page]`, `[Grid]`, `RootStack`, `TitleText` and `PrimaryButton` keeps the row selected and draws exactly that element's box |
| Selection handles, outline and snapping guides from the described bounds | shared `DesignSurface` adorner layer; `GridTrackSnapLines` + `SnapGuideCalculator.Line` in `DesignSurfaceController` | 5 UI-fact snap tests; IDE: a real drag 4 units short of a measured column divider lands exactly on it (`Margin="120,0,0,0"`), a resize edge likewise (`Width="120"`), and a position away from every line stays put |
| Guard: a project whose own WinAppSDK bootstrap initializer is on is not loaded into the surface | `SurfaceDesignRuntimeHostBootstrap` | measured: otherwise every XAML object fails with `0x8001010E` |

## 3. Removed

- The **compiled-page route** (`compiledType`, `UseCompiledPages`, `OD_WINUI_COMPILED`, `rebuild-compiled`): it showed the last build,
  so XAML edits needed a rebuild and a host restart. The fork keeps its optional `compiledType` message, and the
  `0x8001010E` bootstrap finding stayed in the surface host. **Re-added on 2026-11-14 as an opt-in reference**, not as the
  live preview: once the C++ toolchain existed, `SurfaceConnection.CompiledPageType`/`SurfaceDesignDocument` reconnect
  `x:Class` -> `compiledType` under `OD_WINUI_COMPILED=1` (the earlier `OD_SURFACE_COMPILED=1` is still accepted), for what the
  *build* renders. It is measured in step 5, it shows the last build, and `rebuild-compiled` is not restored. The live
  mechanism is step 8 (`winui-designer-bindings.md`).

## 4. Plan, in order

Each step ends with a committed change, tests where the code has an executable seam, and a check in the running IDE (DevFlow, port
9299). A step is not done until it has been seen in the IDE.

1. **Push the stack.** Rebase on `origin/master`, rerun the two test projects, push on the owner's word. (7 local commits today.)
2. **Toolbox drop onto the canvas.** Done. `WinUIXamlDropPlacement` turns a design point into the
   layout, and `WinUIXamlDocumentEditor.Insert(control, container, before, attached)` writes the
   element, its attached properties and (for a StackPanel) its source position as one history entry.
   The container is the innermost measured Grid/StackPanel/Canvas under the point (a named one when
   the hook names it); only non-default Grid cells are written, and the DevFlow hook is `drop`
   (`controlName, x, y, containerName`), with `query-element-bounds` reporting the design-unit space
   its point is in. Done when: dropping a Button at a point in a Grid cell sets `Grid.Row`/`Grid.Column`,
   on a Canvas sets `Canvas.Left`/`Top`, and one Undo removes it. Verified in the IDE on the Surface
   backend, in `tests/fixtures/WinUIDesignerFixture/Microsoft/DropPage.xaml`; the unit tests pin the
   same rule and the undo step.
3. **Real pointer verification.** Done on Windows with a visible IDE (Surface backend). The journeys confirm the
   action-driven checks and contradict one of them (a second, empty Undo step after a resize), now fixed:
   - **Selection**: a real press/release on the element's screen centre switched the selection from `TitleText` to
     `PrimaryButton`, and the selection outline and handle still hug the rendered element afterwards.
   - **Move**: a press, eight stepped held-button moves and a release moved `PrimaryButton` by (60,30) in design units
     (screen scale 1.0), rendered it 60x30 screen units from where it was, and authored `Margin="60,30,0,0"`.
   - **Resize**: a press on the reported handle and a (70,40) stepped drag grew it from 98x32 to 168x72 and authored
     `Width="168"`/`Height="72"`, i.e. the same commit the `resize` hook produces.
   - **Undo**: one Undo per gesture restored the geometry and removed the authored attribute from the saved file.
   - **Coordinates**: the delivered press arrived at window (375,181) for screen (393,222) with client origin (18,41);
     `query-element-screen-bounds` equals `surface-geometry.element`; `od.pointer-target` names the design surface's
     `Image`. The screen-to-design conversion the drag uses is therefore confirmed, not just assumed.
   - **Contradiction, fixed**: a resize gesture used to leave a second, empty Undo step, because its commit wrote the
     unchanged `Margin` (`SetAttribute` records history even for a no-op). The Margin math now lives in
     `WinUIXamlMarginEditor.TryDelta`, which asks for no edit when the position delta rounds to zero;
     `MarginEditorTests` pins that rule and the pointer journey confirms one entry per gesture.
   The **drop** journey was not pointer-verifiable at first: the Surface child advertised no toolbox catalog, so the pad had
   no rows (`toolboxItemCount = 0`) and `od.winui-designer.toolbox.query-item-bounds Button` failed. Since `89f1c17fa4` the
   child advertises a static catalog (`WinUIControlCatalog.StaticCatalog()`, 51 items in 11 groups) and the real OS drag is an
   automated journey (`WinUISurfaceDesignerTests.SurfaceHost_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer`, passing, section 7.2).
4. **Selection chrome from frame bounds.** Done. The shared canvas already drew the eight resize handles, the selection
   outline, the alignment guides and the layout-inset labels from the bounds the designer hands it, so no renderer change
   was needed; what was missing was the *Grid* half of snapping. `GridTrackSnapLines` projects a Grid parent's outer
   edges and its measured row/column dividers (only when the runtime described the layout) into
   `SnapGuideCalculator` lines, and both the move and the handle-resize paths now snap to those lines as well as to
   sibling edges. Every line is limited to the Grid's own orthogonal extent, so it only catches a gesture that overlaps
   the Grid. Verified in the IDE with real pointer drags on `DropPage.xaml`: a move put 4 units short of the 120 column
   divider commits x=120 and `Margin="120,0,0,0"`; a handle drag whose edge lands on 116 commits `Width="120"`; a
   position away from every line stays at x=50; one Undo per gesture restores the document. The fixture's `DropCanvas` is
   now explicitly 120x60 and top-left aligned - a Stretch-aligned element with an explicit size is centred in its cell,
   so its measured x is not its authored Margin and a snap could not be observed from the commit. `snap-lines` reports
   the lines and the parent's measured track counts for this check.
5. **Hot-reload spike for real `{x:Bind}`.** Done: real bindings render in an opt-in **compiled preview**, and the cost is
   measured. `OD_WINUI_COMPILED=1` makes the Surface child send the page's `x:Class` as the fork's `compiledType`; the
   renderer then instantiates the project's compiled page (generated `InitializeComponent`, generated `{x:Bind}`, code-behind)
   instead of parsing the markup. On `BoundPage.xaml` the bound `TextBlock` reports `Text = "Bound from code-behind"` from
   the renderer's own catalog, where markup mode reports it empty; the element tree, property catalog and selection all keep
   working, and the first render has no document error. Warm open of that page costs 21.5 s against 13 s for markup (one
   document, same machine), and a source edit does **not** reach the preview: a literal changed in the file still renders
   the compiled XBF's old text until the project is rebuilt - the last-build coupling that removed this route before. The
   fixture needs `WindowsAppSdkBootstrapInitialize=false` and `WindowsAppSdkUndockedRegFreeWinRTInitialize=false`, or the
   surface refuses to load the project assembly. Editing the bound property itself remains `restart required` in the
   patcher. So it exists as an opt-in "what the build renders" mode, not as the default text-authoritative preview;
   step 8 replaces it as the live experience and keeps it as the fidelity reference.
6. **Native-live polish.** Done. The renderer's own window is reachable from the shared toolbar: `DesignerCanvas` gained
   a `NativeLive` capability and toggle (shown only for a runtime that implements `IWinUIXamlNativeLive`), a user press
   raises `NativeLiveRequested`, and `SetNativeLive` reflects a backend-initiated change without echoing it back. The
   runtime parents the window to the WPF **main window** (`WindowInteropHelper`), not `Process.MainWindowHandle` - the
   latter can name another top-level window, and the child was then placed at a bogus origin and destroyed with that
   window. Placement stays in physical pixels from the geometry probe (no dpi/96 division), both windows' DPI are
   reported, and the runtime's two-second scale poller re-places the window on a DPI change (LibreWPF raises no
   `DpiChanged`). A handle whose window no longer exists now reads as "not native", so the last snapshot is the fallback.
   Entering keeps keyboard focus on the design canvas; leaving hands it back. Verified in the IDE with real clicks on
   the toolbar and the live page (12/12 checks): the toggle enters and leaves native mode, the
   `WinUIDesktopWin32WindowClass` window lands exactly on the frame intersected with the viewport (432x80 at 314,214),
   both DPIs are 96, a click there lands in the renderer's own process (its content bridge), the window follows a pan, a
   DevFlow-started mode shows on the toggle, and killing the renderer's process falls back to snapshots and re-renders.
   OS-level keyboard focus hand-off cannot be asserted on this desktop (it has no stable foreground window:
   `GetForegroundWindow` alternates between 0 and the IDE), so that last step stays a visual check.
7. **Property pad depth.** Done. The value cell now shows the value the renderer reports for a property the source
   has not authored (the description says it came from the renderer), so a control's real `FontSize`, `CornerRadius`
   or font is visible before any edit; editing one authors the attribute through the same pad path. The renderer's
   catalog picks the editor by its real type: `Brush`/`Color` keep the color picker (literal colors only;
   `{StaticResource}`/`{ThemeResource}` stay a resource-key dropdown), `CornerRadius` gets its own 1-or-4-value
   converter (WinUI does not accept Thickness's 2-value form), and `FontFamily` a non-exclusive dropdown of the
   installed families (85 here). A child of a Grid is offered `Grid.Row`/`Grid.Column`/`RowSpan`/`ColumnSpan`
   (defaults 0/0/1/1) and a child of a Canvas `Canvas.Left`/`Canvas.Top`, in the Layout category, so a cell or offset
   can be authored without hand-editing XAML. `properties-pad.properties` reports the pad's rows (value-cell text,
   converter, authored flag) for tests. Verified in the IDE (14/14 checks): `PrimaryButton` shows `FontSize` 14 from
   the renderer, its authored `Background` `#FF4488FF` with the color editor, `CornerRadius` through
   `CornerRadiusValueConverter`, `FontFamily` "Segoe UI Variable" with 85 suggestions; editing `FontSize` authors it;
   `DropCanvas` offers the four Grid properties at their defaults and `Grid.Row=1` is authored; a button dropped into
   `DropCanvas` shows `Canvas.Left`/`Top` 30/20 and no Grid properties, and editing `Canvas.Top` authors it; every edit
   and drop is undone.
8. **Live `{x:Bind}` values (the better mechanism).** Specified in `winui-designer-bindings.md`; built, published and on
   by default on 2026-11-14. The compiled preview (step 5) is correct but last-build; the plan's live rule needs XAML-only
   binding edits to render without a project build. The mechanism keeps the markup preview and evaluates each `{x:Bind}`
   against one design-time **page instance** (from the project assembly, constructed under the existing
   `WINUI_XAML_DESIGN_MODE` signal), substituting the value into the preview XAML: reflection for member paths, cached
   Roslyn delegates for function/static bindings, with converter/format/fallback semantics, extending the fork's existing
   `SamplePlanner`/`SampleTypeModel` resolver from "synthesise from types" to "evaluate against the app's design-time
   state". It lives in the Surface host beside `SamplePlanner` - the only process where WinUI and the user assembly are
   loaded - behind two render fields; the IDE sends them and shows the mode as the default. **Shipped** in
   `LeXtudio.WinUISurface` 0.5.0 (fork tag `surface-v0.5.0`; the release workflow built x64 and ARM64, smoke-tested both
   and published): member paths, Roslyn-compiled function/static bindings, converters declared in the document, and
   `ConverterParameter`/`FallbackValue`/`TargetNullValue`. OpenDevelop defaults it on (`OD_WINUI_LIVE_BINDINGS=0` opts
   out) and the payload is rebuilt from the published package. Measured on `BoundPage`, whose probe rows are now
   `{x:Bind Title}`, a converter declared in the page's resources and `{x:Bind Shout(Title)}`: markup mode reports
   `"Bound from code-behind"`, `"Bound from code-behind (design)"` and `"BOUND FROM CODE-BEHIND!"` - identical to the
   compiled reference on the same document - evaluation costs about 1 ms of a render, warm open is about 10 s against
   21.5 s, and binding edits made through the Properties pad (a path, a nested function call, a converter parameter)
   update the preview in 2.4-2.6 s with no build. `{x:Bind}` has no `StringFormat` (WMC0011), so a format comes from a
   converter or a function. Non-text targets (`Visibility`, `IsEnabled`, `Width`, numeric attached properties) are
   evaluated too (surface 0.6.0), and the mode has the shared canvas's **Live Bindings** toggle plus
   `od.winui-designer.live-bindings on|off|query`; the toggle took it off and back on in the IDE (values, then
   placeholders, then values). Still to do: `x:DataType` roots, template content, `BindBack`/two-way.
   Done when:
   `BoundPage` shows "Bound from code-behind" in markup mode; changing the binding or adding a converter updates the
   preview on save without a build; a code-behind edit is the only remaining rebuild (and is reported as such); warm
   render stays near markup cost (13 s measured; the compiled preview is 21.5 s).

Out of scope for now: visual states and storyboards, template editing, a data-binding wizard (the same non-goals Microsoft lists).

## 5. Risks and open items

- **Fork drift.** The surface is our fork of an unmerged upstream PR (#323). Every renderer capability lives only in our fork; it is
  published as `LeXtudio.WinUISurface` and pinned in `Directory.Packages.props` (0.4.0). A republish under the same version is not
  seen by NuGet caches: bump the version.
- **Round-trip.** The editor now preserves untouched layout, but the edited tag itself is still reflowed and two identical tags may
  swap layout. The corpus baseline (`real-files-baseline.txt`) makes any change visible.
- **Unverified here.** DPI scaling **above 100%** (placement is in physical pixels and both window DPIs are reported,
  but no monitor at another scale was available to move the IDE to), OS-level keyboard focus for the live window (this
  desktop has no stable foreground window: `GetForegroundWindow` alternates between 0 and the IDE), whether the Surface
  accepts the incremental `design/set-bounds` edit or silently falls back to a full reload (the commit works either way
  and reports no distinction), and anything on
  macOS/Linux (the Surface is Windows-only). Grid-track snapping uses the *measured* track offsets: a Grid with padding or
  a border offsets its tracks by more than its own bounds origin, and that difference has not been measured yet.
- **The Surface host's toolbox is a static list.** Its child has no WinUI runtime to reflect a control list from, so it advertises
  `WinUIControlCatalog.StaticCatalog()` (the common `Microsoft.UI.Xaml.Controls`, shared with the hosts that reflect theirs); controls
  from the project's own assemblies are not listed there. (Earlier it advertised nothing and the pad stayed empty; the `drop` hook's
  fallback to the control type name when the pad is empty remains for such backends.)
- **`{x:Bind}` today vs the target.** The compiled preview shows real values but is last-build; the default markup preview
  now evaluates them (`winui-designer-bindings.md`, surface 0.5.0, on by default) so a binding edit is live, while the
  compiled page stays the opt-in fidelity reference. Unhandled forms - `x:DataType` roots, template content and
  two-way bindings - still fall back to the cleaner's placeholders. Either mode constructs a page instance from the project
  assembly, so the project keeps the WinAppSDK bootstrap initializers off.
- **Process.** Commits so far skipped the `build/opencode-review.ps1` review at the owner's instruction; CI results are ignored for now.
  ARM64 is not a priority.

## 6. How to run a check

```bash
OD_TEST_MODE=1 OD_WINUI_RUNTIME=surface OD_SURFACE_ROOT='C:\wxp\idesurf' \
  dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj -f net10.0-windows --no-build
```

Then, over DevFlow (`POST http://localhost:9299/api/v1/invoke/actions/<name>`, body `{"args":[...]}`): `od.open-solution`,
`od.open-file`, wait for `od.winui-designer.status` to report `rendered`, then `select`, `nudge`, `resize`, `native-live`,
`query-element-screen-bounds`, `export-png`, `undo`. The first open after a surface package change re-provisions the matched host
(several minutes); later opens use the cache. Build the deployed pieces with `-p:SkipWinUIHotReloadTap=true` and stop the IDE first, or
the copy into `AddIns\...\SurfaceHost` fails on locked files.

For a real pointer gesture, follow the integration suite's rules: `od.activate` (and require `foregrounded`), `od.pointer-events`
with `true` to reset, `POST /api/v1/ui/actions/press`, stepped `drag-move`s, `release` in **screen** coordinates from
`od.winui-designer.surface-geometry` or `query-element-screen-bounds`; then poll `od.pointer-events` (the native press can be
queued past the endpoint's return) and treat a zero `count` as a delivery failure, not a designer bug. A press/release without
movement selects; a press on the reported `handle` resizes. Read `MainPage.xaml` after `Ctrl+S` to confirm the authored
`Margin`/`Width`/`Height`, then `undo` and save again so the fixture stays clean. A gesture commits through a full document
reload, so poll `query-element-bounds` for the settled value instead of assuming a fixed delay. `snap-lines <name>` shows the
Grid-track lines the selected element's parent contributes to a drag. `od.designer-canvas.status` reports every visible
toolbar item's **screen bounds** (`toolbarItemBounds`) so a real click can press one by name - the native-live toggle, for
example, needs no coordinates hardcoded; a click over the live window is deliberately absent from `od.pointer-events` because
it goes to the renderer's own process. `properties-pad.properties` lists the pad's rows with the value cell's text, the
editor converter and whether the source authors the property; `properties-pad.edit` and `properties-pad.standard-values`
drive and inspect them. `runtime-properties <name>` reports the renderer's own values, which is how a compiled preview's
`{x:Bind}` result is read. For the compiled preview, launch with `OD_WINUI_COMPILED=1` and a project whose
`WindowsAppSdkBootstrapInitialize`/`WindowsAppSdkUndockedRegFreeWinRTInitialize` are false (see
`tests/fixtures/WinUIDesignerFixture/Microsoft/BoundPage.xaml`); without the variable the same page renders markup. The
markup preview itself now evaluates `{x:Bind}` (`doc/technotes/winui-designer-bindings.md`, surface 0.5.0 and on by
default), so it reports `Text = "Bound from code-behind"` with no variable set, and a binding edit made through the
Properties pad re-renders without a build; `OD_WINUI_LIVE_BINDINGS=0` turns the evaluation off.

## 7. Surface host: what is left to verify (handoff to a Windows machine)

State on 2026-11-15, written on macOS where the Surface cannot run (it is Windows-only and needs the Windows App Runtime).
The default switch (`RegisterDevFlowActionsCommand.DefaultRuntimePreference = "surface"`, plus the Tools > Options page) is in
the tree but **had never been run in an IDE when this was written** (since then it has: the default selection in 7.1, the Options page in 7.3): before it, every verification of the Surface in the IDE was manual
(2026-11-12) and every automated one was the `WinUIXamlDesigner.SurfaceHost.Tests` suite (fake surface, child process,
provisioner, the standalone `UnoDesignClient`).

**Written on macOS (as of that writing), since run on Windows (7.1, 7.2):** the `SurfaceWinUI` runtime cell. It now holds six journeys: the four
described here plus the toolbox drag and the resize-handle drag (7.2). The text below is kept as the original handoff; "four" counts the original set.

- `tests/OpenDevelop.IntegrationTests/WinUISurfaceDesignerTests.cs`, trait `Runtime=SurfaceWinUI`, four journeys on the
  `WinUIDesignerFixture/Microsoft` page: render with Document Outline; source edit outside the designer refreshes; Properties pad
  edit then undo and redo (`canUndo`/`canRedo`, dirty, saved text); select with `surface-geometry` and `describe-element`.
  Surface reports backend `WinUI` like the Microsoft host, so every test also requires the status text to contain
  `WinUI Surface host`; a silent fallback to the Microsoft host fails instead of passing. It copies the sample source without
  `bin`/`obj` (the provisioner builds it) and waits up to 4 minutes for the first render.
- `RuntimeCell.SurfaceWinUI`, and the runner: `dotnet msbuild tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj
  -t:RunRuntimeCell -p:RuntimeCell=SurfaceWinUI` sets `OD_WINUI_RUNTIME=surface`; the cell is in the Windows list of
  `RuntimeCellsOnThisOS` (so `RunRuntimeMatrix` includes it) and not in macOS's.
- Verified on macOS only: the test project compiles (`-p:BuildProjectReferences=false`), and `-list tests -trait Runtime=SurfaceWinUI`
  selects the four tests. They skip off Windows (`SkipUnless`). No test body has executed, so selectors, field names
  (`canUndo`, `canRedo`, `framework`, `status`) and timings are copied from the Microsoft/Uno tests and unconfirmed for Surface.
  Expect to fix the first run.

**To do on Windows, in this order:**

1. Run the four tests one at a time (`-p:RuntimeCell=SurfaceWinUI`, or `dotnet run` with `-method`), fix what the first run
   exposes, then run the cell. Prerequisites: the add-in built with `-p:SkipWinUIHotReloadTap=true` (stop the IDE first), the
   Windows App Runtime installed, a short `OD_SURFACE_ROOT`.
2. **Fallback test: done (no fallback by decision, see 7.1).** Originally: prove that with `OD_WINUI_RUNTIME=surface` and no `SurfaceHost/` payload (or a project the
   bootstrap declines) the designer reports the bootstrap's reason and renders nothing, and that `OD_WINUI_RUNTIME=microsoft`
   and an Options value of `microsoft` still select the Microsoft host. Note the code today does **not** silently fall back
   to the Microsoft host when the Surface payload is present but provisioning fails (section 2: "no surface is started");
   the Options text ("falls back automatically") only holds for an undeployed payload or a declined project. Decide which
   behaviour is intended, then fix the text or the code.
3. **Default-selection unit test: done on macOS.** The resolution is now the pure `WinUIRuntimeSelection.Resolve` (environment, then stored `WinUIXamlDesigner.Runtime`, then `surface`; blank ignored; unrecognised reported and replaced by the default), source-linked into `OpenDevelop.Base.Tests` and covered by `WinUIRuntimeSelectionTests` (13 cases, passing). The add-in project itself builds (`-p:SkipWinUIHotReloadTap=true`), so `RegisterDevFlowActionsCommand` and the Options page compile; neither had run when this was written (since then both have: the command registers the hosts in every Windows run of 7.1-7.2, and the page is exercised through `od.winui-designer.options`, 7.3).
4. **Default-on regressions: cells done (7.2); the Options page is driven through `od.winui-designer.options` (7.3), not clicked in the modal dialog.** With the default now `surface`, run the existing `Runtime=MicrosoftWinUI` and `Runtime=Uno`
   cells once each. The Microsoft cell pins `microsoft` through the environment so it is unaffected, but `Uno` and every
   test that opens the WinUI sample without a pin (`AddInTests` methods that call `OpenWinUIDesignerAsync`) now route
   through whatever is the default. Also run the Options page by hand (selected value shows, persists, restart applies).
5. **CI: partly done (7.3).** Promote `surfacehost-tests` from `observe` after two green runner runs (this plan records no runner result for it); the Windows App Runtime install step
   (`windowsappruntimeinstall-x64.exe --quiet --force`) is still absent from the runner setup. Since the CI change that followed `9e4c71a18d`,
   the whole Surface cell, the Microsoft cell, the Uno cell, `WinUIHostSelectionTests` and the real WinUI-Gallery corpus are `observe` entries
   of the `integration-winui` lane (`allowAllFailures`), so they may fail on a runner for that reason; what remains open is a first runner run,
   the missing runner prerequisites, and the promotion.
6. **Known functional gaps to cover or document:** `PickAt` finds nothing in frame mode (only native mode); native mode
   answers `UpdateXaml` with `Hwnd`, not a frame; a machine without the Windows App Runtime (the `0x80670016` message is tested
   with a fake surface only). The gesture journeys (drag toolbox item, resize handle) that the Microsoft and Uno cells have are
   done for Surface too (7.2; they were manual only, section 6).
7. **Do not call the Surface "the default" in user-facing text** until 1, 2 and 4 pass: the Options label says "Experimental
   Surface host (recommended)".

### 7.1 Windows run (2026-11-15, x64, Windows 11, Build Tools 2026): results

Committed, each through `build/opencode-review.ps1` (an approved review of the exact tree): `61b919d8cc` (Surface test fixes), `c4b1437be2` (Microsoft host build), `b9336fa6cc` (Uno and runtime gates). Not pushed.

**Surface cell (`Runtime=SurfaceWinUI`): 4 of 4 pass, one method at a time** (first cold run 255 s, then 100-150 s each, `OD_SURFACE_ROOT=C:\wxp\itsurf`).
The first run failed twice, both bugs in the test: `SurfaceAvailable` was private (xUnit resolves `SkipUnless` by name and needs a
`public static` member), and `SurfaceHost_SelectElement_ReportsGeometryAndBounds` asserted a `success` member that
`od.winui-designer.surface-geometry` does not return (its contract is `available`/`frame`/`selection`/`handle`/`element`); it now checks
`available` and a non-empty `frame`, then `AssertSurfaceGeometryConsistent` after the select. The `WinUI Surface host` status check is kept.

**Fallback and selection, observed in a real IDE** (private `-configdir`, `OD_TEST_MODE=1`):

| Scenario | Result |
|---|---|
| default (env unset, nothing stored) | Surface renders, status `Rendered by WinUI Surface host` |
| `OD_WINUI_RUNTIME=surface`, no `SurfaceHost/` payload | `Create` returns null with no reason; no other factory accepted it, so the designer says only `WinUI 3 runtime host is not installed.` (generic, names neither host) |
| `surface`, payload present, provisioning fails (over-long `OD_SURFACE_ROOT`) | **no fallback**: status is the Surface's own `cannot start: ... 137 characters ... at most 120`, nothing renders |
| stored Options value `microsoft` or `uno`, env unset | Surface is not registered; a Microsoft WinUI project goes to the Microsoft host (here: "not installed" until the host was built) |

Text vs code, for the owner to decide: the Options text ("automatically falls back to the Microsoft host") is true only when the payload is absent
*and* the Microsoft host is deployed, and false when provisioning fails. The generic "not installed" text also hides which host is missing.
The Options dialog itself had no DevFlow hook when this was written, so display, persistence and restart were not clicked through (the pure logic is covered by
`WinUIRuntimeSelectionTests`); only the stored-value selection above was exercised. Superseded for the page itself by 7.3 (`od.winui-designer.options`).

**Microsoft host: needed four source fixes and a Visual Studio component before it could run at all.** `build.ps1` builds it with VS MSBuild, and this Build
Tools install lacked the `.NET SDK` component (`Microsoft.NetCore.Component.SDK`, added with the VS Installer; without it VS MSBuild reports
`Could not resolve SDK "Microsoft.NET.Sdk"`). Then, at `2e892ff225`:
1. `WinUIXamlDesigner.MicrosoftHost.csproj` did not link `TemplateXBindPlan.cs` and `TemplatePlanMarker.cs` (new in `1b19885d62`): CS0246/CS0103.
2. `UnoHost/DesignHost.cs` used `string.EndsWith(char, StringComparison)`, which exists only on net11, while the Microsoft host also builds for net9 and net10 (CS1503).
3. `UnoDesignRuntimeHost.ProjectDependencyContext` applied the **Uno 7 requirement to every project**, so a Microsoft WinUI project was rejected with
   "Uno designer requires Uno Platform 7.x" (and the Surface runs wrote it to the output pad on every open). It is now limited to Uno projects (`isUnoProject`).
   The first review of that fix found that removing the early return made the packaged, architecture and framework checks run for the Surface host too, writing
   "pins .NET 10.0.0 ... cannot run on" lines to its output pad; they now run only for hosts without a `childLaunchProvider` (the Microsoft and Uno children, as before), not for the Surface host, and
   `SurfaceHost_RendersWinUIPage_WithOutlineAndNoDocumentError` asserts the pad has none of those lines (checked not to be vacuous: with the new early return disabled it fails on "pins .NET").
4. `CanHostRunOnAppFramework` compared a net10 app's pinned .NET with the **IDE process's** runtime (net11 since `0f522a1576`) and refused every framework-dependent
   net10 app with "cannot run this configuration's runtime graph". The child is already chosen per app CLR major, so it now compares with the selected child's major
   (`ChildRuntimeMajor`, read from the `MicrosoftHost\netN.M[-windowsappsdk...]` folder; the legacy flat child counts as net10).
With these, `AddInTests.WinUIOnly_DesignerOpensWinUIProject` (`Runtime=MicrosoftWinUI`) passes. The other 14 Microsoft-cell tests were not run.

**Uno cell: was broken on Windows by `1b19885d62`, fixed in `3bec47feea`.** `UnoHost/Program.cs` selected only `UseMacOS()`, so on Windows the child exited with
`No platform host could be selected`. It now picks the host by OS (Win32, MacOS, else X11). After the fix `OpenUnoXamlFile_UsesWinUIXamlDesignerInsteadOfWpfDesigner`,
`RendersButtonWithVisibleLightThemeColors` and the properties-pad edit test pass. The Uno fixtures (`UnoHotReloadFixture`) did not compile when first tried (`InitializeComponent` missing); superseded, see 7.2.

**No fallback (commit `8bf05d9824`, owner's decision).** The Surface host never falls back to the Microsoft or Uno host. `WinUIRuntimeSelection.HostsToRegister` is the rule
(`surface` registers uno + surface, no Microsoft host; `uno` registers microsoft + uno; `microsoft` registers uno + microsoft). When no host takes a Microsoft WinUI document the
designer says why (`WinUI 3 runtime host is not installed.` plus each registered factory's reason, e.g. the missing `SurfaceHost` folder); when a deployed Surface host fails to
start its own error is shown, ending with a note that the Microsoft host is not used automatically. The Options text no longer promises a fallback. This supersedes the
scenario table above, whose second row now reads: the designer names the missing Surface files. Automated by `WinUIHostSelectionTests` (own IDE each, both pass):
`SurfaceHostFilesMissing_IsReported_AndNothingIsRenderedThroughAnotherHost` (`OD_SURFACE_HOST_ROOT` simulates a missing payload; fails against the old behaviour, which rendered
through the Microsoft host) and `StoredMicrosoftChoice_SelectsTheMicrosoftHost_NotTheSurface`.

### 7.2 Follow-up run (same day): full cells, Surface journeys, Tools pad

- **Full cells on the final commits:** Microsoft cell (`Runtime=MicrosoftWinUI`) 36 tests, 9 passed, 27 skipped (the WinUI Gallery tests need `OD_WINUI_GALLERY_ROOT`),
  0 failed; Uno cell (`Runtime=Uno`) 25 tests, 23 passed, 2 skipped (Android and iOS devices), 0 failed. Surface cell: the original four plus the two new journeys pass.
- **Two earlier failures were tests, not products.** The Microsoft designer-build wait (60 s) was too short for the IDE's own build of the sample (`8a4bb475ac`, now 180 s);
  the Uno click test pressed the centre of a button whose centre lay 1.5 px below a small IDE window's design viewport (`b7f8eb4e3f`, it now clicks inside the viewport).
- **A stray Uno window** appeared on Windows once the Win32 platform host started the child (`3bec47feea`); it is now moved to (-32000,-32000) after activation (`880f86f63c`), with a
  brief flash at its creation position that is not eliminated.
- **Surface toolbox and journeys:** `89f1c17fa4` (static catalog), `6479c6d455` (real-drag toolbox drop and resize-handle drag, both asserting the Surface host served them).
- **Tools pad:** `493d0406d2` keeps the "No matching controls" text in sync with the list (it stayed on after a late catalog), `2e2889f482` fills the WinUI pad from the static
  catalog as soon as the designer view exists, before the host starts (the host's own catalog adds to it later).
- **Order dependence, not fixed:** `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer` (Uno) fails when run alone (the IDE stops answering after the press,
  three runs of four minutes each) but passes inside the full Uno cell; the cause was not found.
- `UnoHotReloadFixture`: a plain `dotnet build` in `tests/fixtures/UnoHotReloadFixture` succeeded (0 warnings, 0 errors, 23 s) on this machine at the end of the day. The
  earlier `InitializeComponent` failure (seen once, in 7.1) did not reproduce and its cause was not determined: no commit touched the fixture in between (its last change is
  `1b19885d62`), so a stale `obj/` or a different build invocation is the likely explanation, not a code fix. Treat the fixture as unverified for hot reload.

### 7.3 CI entries and the Options page

- **CI (`9e4c71a18d`, extended after it):** Windows x64 `observe` entries in a lane of their own, `integration-winui` (job `winui`, `allowAllFailures`): `winui-surface-designer-journeys` (the whole Surface cell by `-trait Runtime=SurfaceWinUI`, six journeys including the resize drag), `winui-host-selection`, `winui-microsoft-cell` and `winui-uno-cell` (whole cells by trait, with `OD_WINUI_RUNTIME` pinned through the plan's new per-entry `env`) and `winui-gallery-corpus` (ci.yml shallow-clones microsoft/WinUI-Gallery and exports `OD_WINUI_GALLERY_ROOT`); the same lane also carries the WPF counterpart `wpf-gallery-corpus`. The device-journey guard accepts `-trait Runtime=<cell>` on Windows entries only, and `run-test-plan.ps1` fails an entry whose every test skipped. The runner has no Windows App Runtime install step, and the Microsoft hosts are only built by a best-effort `ci.yml` step (`BuildMicrosoftDesignerHostsForIntegrationTests`, which needs Visual Studio MSBuild), and its interactive desktop is only a best-effort preparation (`ci.yml` runs `.github/ci/prepare-windows-desktop.ps1`), so first runs may fail, and nothing has run on a runner yet.
- **Options page (`2e6a572517`):** the dialog is modal, so `od.winui-designer.options [choice] [save]` builds the page from the Options tree and loads it as the dialog does, selects a choice on its
  combo box, saves it as accepting would, and reports the stored value and the runtime the next start resolves. Two `WinUIHostSelectionTests` journeys use it: the page's path, choices, text (no
  fallback promise) and the select / save / reselect round trip, and a stored `uno` (shown as the default) surviving an accept without change. Not covered: pixels in the dialog, the file written on
  disk (the IDE is killed, not closed) and an actual restart.

**Not done:** a first runner run of the `integration-winui` entries, the runner prerequisites above (Windows App Runtime, and whether the best-effort Microsoft host build step succeeds with Visual Studio MSBuild), and promoting the entries (two green runner runs). The Microsoft and Uno cells, the Surface resize journey and the Gallery corpus are `observe` entries since the CI change that followed `9e4c71a18d`.
