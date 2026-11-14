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
| Surface renderer as a runtime (`OD_WINUI_RUNTIME=surface`), matched host per project, private run copies | `WinUIXamlDesigner.SurfaceHost`, `.Child`, fork provisioner | live in the IDE |
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
   The **drop** journey is not pointer-verifiable here: the Surface backend advertises no toolbox catalog, so the pad has
   no rows (`toolboxItemCount = 0`) and `od.winui-designer.toolbox.query-item-bounds Button` fails, while the Uno and
   Microsoft child hosts that would report one are not deployed on this machine. Its commit side (design point -> Grid
   cell / Canvas position, one Undo) is unaffected and stays covered by the `drop` hook and the unit tests; a real
   OLE drag needs one of those hosts.
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
  and reports no distinction), the pointer drag from the Toolbox (no deployed backend reports a catalog), and anything on
  macOS/Linux (the Surface is Windows-only). Grid-track snapping uses the *measured* track offsets: a Grid with padding or
  a border offsets its tracks by more than its own bounds origin, and that difference has not been measured yet.
- **The Surface host advertises no toolbox catalog**, so its Toolbox pad stays empty and a real drag cannot start there; the `drop`
  hook therefore falls back to the control type name when the pad is empty, and still requires the pad to offer the control when it
  has rows. A real drag journey needs a backend whose child reports a catalog; the pointer journey confirmed the pad has zero rows
  and `toolbox.query-item-bounds` fails, so the drop is verified through its hook and unit tests only.
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
