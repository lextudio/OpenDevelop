# WPF Designer

This technote is the dedicated home for the WPF designer (`WpfDesign`): current state,
architecture, the drag-and-drop findings, and testing notes. The cross-designer roadmap
(WinForms + WPF + WinUI together), framework detection, provider contracts, phases, and the
test matrix live in [`xaml-services.md`](xaml-services.md). The WinUI/Uno designer's dedicated
technote is [`winui-designer.md`](winui-designer.md).

Current status: the WPF designer is the official WPF backend, added to the main solution and
built on `LibreWPF.Sdk`.

## Current Baseline

| Component | Location | Current Status |
|---|---|---|
| WPF Designer | `src/AddIns/DisplayBindings/WpfDesign/` and `externals/vscode-wpf/external/WpfDesigner/` | Added to the main solution, uses `LibreWPF.Sdk`; it is the official WPF backend |
| XAML language server | `externals/vscode-wpf/` | The WPF language server for `.xaml` is wired up; framework detection cannot rely on file extension alone |

The WPF designer engine lives partly in OpenDevelop (`src/AddIns/DisplayBindings/WpfDesign/`)
and partly in the `externals/vscode-wpf` submodule (`external/WpfDesigner/`), which also carries
the XAML language services (`external/wxsg/`). See
[`winui-designer.md`](winui-designer.md) for the externals layout and the local-feed packaging
workflow that applies to `vscode-wpf` changes.

The designer follows the same "split chrome" philosophy as the WinUI designer: the design
surface renders inside the document tab, while Toolbox, Properties, and Outline are served
through OpenDevelop's shell contracts (`IToolsHost.ToolsContent`,
`IHasPropertyContainer.PropertyContainer`, `IOutlineContentHost.OutlineContent`) — the shared
`WpfToolbox` pad in particular is reused by the WinForms designer too
(see [`winforms-designer.md`](winforms-designer.md)).

## Portable Drag-and-Drop Findings (2026-08-12)

Real WPF's drag source blocks inside a Win32 OLE modal loop. `PortablePresentationSource` has no
such loop, so LibreWPF reimplements the source half in
`src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/PortableDragDropOperation.cs`:
capture the mouse, push a nested `DispatcherFrame`, and hit-test on every mouse move to drive the
same `DragEnter`/`DragOver`/`DragLeave`/`Drop` routed events. Two OpenDevelop-visible consequences
came out of investigating toolbox → WPF-designer drag-drop.

### Resolved: re-entrancy guard turned a drag-source cleanup handler into a mid-drag tool reset

`OnPreviewMouseMove` on the drag source is **re-entered for every mouse move of the drag it just
started** — the nested `DispatcherFrame` keeps pumping input through WPF's normal event system,
which real OLE's native modal loop never does. `PortableDragDropOperation.Run` guards against the
resulting recursive `DoDragDrop` with a `[ThreadStatic] s_isRunning` flag (added in librewpf
`1e8db81ec`, "Implement macOS drag and drop"), failing the nested call closed.

Failing *closed* means the nested call **returns normally** rather than blocking. Any `finally`
around the caller's `DoDragDrop` therefore runs *while the outer drag is still in flight*. In
OpenDevelop that finally was `WpfToolbox.ResetToolSelection()`, which sets
`toolService.CurrentTool = PointerTool` → `CreateComponentTool.Deactivate()` →
`designPanel.DragOver -= designPanel_DragOver`. The in-flight drag then had no `DragOver` handler
left and the drop silently created nothing.

Neither change is wrong alone; together they broke drag-drop. Fixed on the OpenDevelop side with an
`isDragging` re-entrancy guard in `WpfToolbox.OnPreviewMouseMove`
(`src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfToolbox.cs`).

**Rule for any portable drag source:** guard your own `PreviewMouseMove` against re-entry, and never
put drag-teardown work in a `finally` around `DoDragDrop` without one.

### Open: `DragOver` delivery is sparse and path-dependent

Measured with temporary tracing in `CreateComponentTool.designPanel_DragOver`, driving synthetic
input through DevFlow (`/api/v1/ui/actions/{press,drag-move,release}`) against the
`externals/vscode-wpf/sample/net6.0/SamplePane.xaml` fixture:

| gesture | `drag-move` calls | `DragOver` events delivered |
| --- | --- | --- |
| toolbox → bottom of `PaneStack` | 6 | **2** |
| toolbox → top of `PaneStack` | 24 | **0** |

The 24-step case delivered nothing at all, so the drop was silently lost — no control created. A
representative trace of the working case (`p` is design-panel-relative):

```
DragOver p=171,396 ModelHit=Border      -> AddItems: Border FAILED, UserControl FAILED (retry)
DragOver p=335,387 ModelHit=StackPanel  -> AddItems: StackPanel OK  (item created HERE)
(no further DragOver for the rest of the gesture)
```

Consequences for the designer, all downstream of this one defect:

- **The control is created wherever the drag first lands on a valid container, not where the pointer
  is released.** `CreateComponentTool.designPanel_DragOver` creates the item in its
  `moveLogic == null` branch; `MoveLogic` is supposed to make it follow the pointer afterwards, but
  `Start(createPoint)` and `Move(p)` are two *separate* `DragOver` events and only run once the new
  element reports `IsLoaded`. With ~2 events per gesture, `MoveLogic` never runs at all.
- For a `StackPanel` the position *is* the child index, so this shows up as "dropped at the top,
  inserted at the bottom". The index logic itself is fine — an item created at
  `pos=323,5` was correctly placed at index 1 (right after `PaneTitle`); it is the *creation point*
  that is wrong, not `StackPanelPlacementSupport`.
- One gesture was observed creating **two** controls (item created, `moveLogic` reset, item created
  again), so something also resets `moveLogic` mid-drag — likely a spurious `DragLeave`.
- Dropping onto an existing child can lose the drop entirely: `HitTest` returns that child (e.g. a
  `TextBlock`), and `AddItemsWithCustomSize` walks up to the parent — but if no `DragOver` arrives
  while the pointer is over a container that accepts the item, nothing is ever created.

A partial mitigation is in `CreateComponentTool.designPanel_Drop` (replay the real release point
through `MoveLogic` before committing, so the release point is authoritative regardless of how many
`DragOver` events arrived). It does not regress the passing tests, but its benefit could **not** be
demonstrated end to end, because the only scenario that would show it (dropping at the top of the
stack) is blocked by the zero-delivery case above. Treat it as unverified until the delivery problem
is fixed.

Root cause is on the LibreWPF side — `OnPointerUpdate` only runs when `PreviewMouseMove` actually
reaches the drag source — so fixing it in WpfDesigner would only paper over it.

## Testing and Instrumenting

The designer's DevFlow actions live in
`src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfDesignDevFlowActions.cs`
(`od.wpf-designer.*`); the integration tests follow the DevFlow action pattern documented in
[`integration-testing.md`](integration-testing.md).

For instrumenting the drag path (`DragOver` delivery is invisible from the outside): add
temporary `Console.WriteLine` tracing (gated on an env var) in
`CreateComponentTool.designPanel_DragOver` and `CreateComponentTool.AddItemsWithCustomSize`,
launch with `OD_TEST_MODE=1`, and drive the gesture over DevFlow. Notes that cost time:

- Use `OD_TEST_MODE=1` so the window does not steal focus, and delete
  `~/Library/Application Support/ICSharpCode/SharpDevelop5/{LastViewStates.xml,preferences,layouts}`
  first — restored view state otherwise makes which view is active nondeterministic.
- `WpfDesign.Designer.csproj` pins an older `LibreWPF.Sdk` than the local feed carries, so it cannot
  be built standalone; build `src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/WpfDesign.AddIn.csproj`
  instead, which builds it as a project reference and deploys the DLL.
- Screen coordinates from `od.wpf-designer.query-element-screen-bounds` and window-relative
  coordinates in `/api/v1/ui/tree` differ by the window origin (~`(10, 61)` here) — do not compare
  them directly.
- Repeatedly calling `od.open-file` in a wait loop can leave the workbench in a state where the drag
  never reaches the design panel at all. Open once, then poll `od.wpf-designer.status`.
