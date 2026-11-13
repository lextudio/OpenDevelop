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
| Move and resize as source edits | `ApplyElementBounds`; hooks `nudge`, `resize` | IDE: +20,+10 move, 98x32 -> 200x48 resize, one Undo restores |
| Toolbox drop at a point: the measured Grid cell (`Grid.Row`/`Grid.Column`), `Canvas.Left`/`Top`, or the StackPanel child index, as one source edit | `WinUIXamlDropPlacement`, `WinUIXamlDocumentEditor.Insert(..., before, attached)`, hook `drop` | 60 tests; IDE: a Button dropped at (600,400) in `DropGrid` authored `Grid.Row="1" Grid.Column="1"`, at (30,20) in `DropCanvas` authored `Canvas.Left="30" Canvas.Top="20"`, one Undo each |
| Edits keep the author's layout (declaration, line endings, untouched tags) | `WinUIXamlDocumentEditor.PreserveLayout` | 47 tests, `tests/OpenDevelop.WinUIXamlDocument.Tests` |
| Native-live window over the canvas (opt-in), follows the canvas, falls back to the last snapshot if the renderer dies | `UnoDesignRuntimeHost.NativeLive.cs`, `od.winui-designer.native-live` | IDE: on, off, on, killed renderer, window follows maximize |
| Guard: a project whose own WinAppSDK bootstrap initializer is on is not loaded into the surface | `SurfaceDesignRuntimeHostBootstrap` | measured: otherwise every XAML object fails with `0x8001010E` |

## 3. Removed

- The **compiled-page route** (`compiledType`, `UseCompiledPages`, `OD_WINUI_COMPILED`, `rebuild-compiled`): it showed the last build,
  so XAML edits needed a rebuild and a host restart. The fork keeps its unused optional `compiledType` message.

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
3. **Real pointer verification.** The IDE runs hidden in this environment, so pointer drags were never exercised. Run the same
   move, resize and drop journeys with a visible IDE and real input (the integration suite's pointer rules in `AGENTS.md`), and
   record which of the action-driven checks the pointer path confirms or contradicts.
4. **Selection chrome from frame bounds.** Handles, the selection outline and snapping guides drawn by the shell from the described
   bounds; snap to Grid track edges and sibling edges using the layout metadata. No renderer change needed.
5. **Hot-reload spike for real `{x:Bind}`.** Question: can `WinUIHotReload.Agent` attach to the Surface process (self-injection,
   `InitializeXamlDiagnosticsEx` on its own pid) and patch a compiled page's visual tree in place? Needs a surface that
   instantiates the project's page (the removed `compiledType` message is the starting point) plus the agent as a startup hook. Done
   when: edit a bound element's attribute and see the running compiled page change without restarting the renderer, with the real
   binding values intact. If it cannot attach, record why and stop; `{x:Bind}` stays a placeholder.
6. **Native-live polish.** DPI scaling (only 100% was tested), keyboard focus hand-off between the IDE and the renderer window,
   and a toolbar toggle (the shared control is `DesignerCanvas.cs`, which every designer uses, so that is a deliberate change).
7. **Property pad depth.** Show the renderer's current value in the value cell (today it is the description), type-aware editors
   for `Brush`, `CornerRadius` and `FontFamily` from the catalog, attached properties of the parent (`Grid.Row`, `Canvas.Left`).

Out of scope for now: visual states and storyboards, template editing, a data-binding wizard (the same non-goals Microsoft lists).

## 5. Risks and open items

- **Fork drift.** The surface is our fork of an unmerged upstream PR (#323). Every renderer capability lives only in our fork; it is
  published as `LeXtudio.WinUISurface` and pinned in `Directory.Packages.props` (0.4.0). A republish under the same version is not
  seen by NuGet caches: bump the version.
- **Round-trip.** The editor now preserves untouched layout, but the edited tag itself is still reflowed and two identical tags may
  swap layout. The corpus baseline (`real-files-baseline.txt`) makes any change visible.
- **Unverified here.** Real pointer input, high DPI and focus behaviour, the `design/set-bounds` incremental path (the Surface refuses it,
  so resize is a full re-render), and anything on macOS/Linux (the Surface is Windows-only).
- **The Surface host advertises no toolbox catalog**, so its Toolbox pad stays empty and a real drag cannot start there; the `drop`
  hook therefore falls back to the control type name when the pad is empty, and still requires the pad to offer the control when it
  has rows. A real drag journey needs a backend whose child reports a catalog.
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
