# WinUI/Uno Designer

This technote is the dedicated home for the WinUI/Uno designer: architecture decisions, the
XAML Studio/ProGPU integration boundary, packaging workflow, the current state, and the
real-world preview problem catalog (2026-08-14). The cross-designer roadmap (WinForms + WPF +
WinUI together), framework detection, provider contracts, phases, and the test matrix live in
[`xaml-services.md`](xaml-services.md).

Current status: the designer is integrated end-to-end for the `src/Samples/UnoXamlSample`
fixture (detect → compile → materialize → render → present → edit round-trips, covered by
`WinUIDesigner_*` integration tests). Real-world Uno projects are blocked by the diagnostics
and preview gaps catalogued in [the problem section](#real-world-project-preview-problem-2026-08-14).

## Architecture

### Reuse Boundary with UnoDevelop and XAML Studio

UnoDevelop's `src/AddIns/DisplayBindings/XamlDesigner/` has implemented native `Microsoft.UI.Xaml` Source/Design secondary views, a Toolbox provider, an Outline provider, Properties Pad wiring, and integration tests. OpenDevelop reuses that IDE wiring approach but does not re-implement the renderer: the original `XamlRenderService` in `externals/xamlstudio/XamlStudio.Toolkit/Services/XamlRenderService/` and its models/extensions are the upstream code, consumed through linked source or a standalone toolkit project, with only the narrow adaptations required to compile. Its algorithms must not be rewritten as a WPF XAML parser, nor maintained as a behavior-forked copy.

UnoDevelop/XAML Studio's UI files cannot directly become OpenDevelop WPF visuals: the former's control types are `Microsoft.UI.Xaml.*`, while the latter's shell and document views are `System.Windows.*`. The two visual trees must be isolated by an explicit host, similar to how the WinForms designer embeds WPF through `WindowsFormsHost` rather than loading WinForms controls as WPF controls.

### External References and Dependencies

```text
externals/
├── xamlstudio/       `https://github.com/lextudio/xamlstudio` submodule; linked reuse of the original renderer source
├── vscode-wpf/
│   ├── external/WpfDesigner/   OpenDevelop's WPF designer engine
│   └── external/wxsg/          XAML language services and framework profiles
└── AXSG (included transitively by wxsg)    XAML analysis/generation foundation
```

The submodules are pinned to the WinUI migration commit of `origin/unodevelop`, and the XAML Studio renderer already uses `Microsoft.UI.Xaml`; it still cannot compile into the WPF shell, but should compile into a standalone WinUI/Uno renderer assembly. Upstream still contains UWP Storage/Media assumptions; the port should isolate those APIs behind small platform adapters while preserving upstream file identity and licenses; it must not be replaced by WPF's `System.Windows.Markup.XamlReader`.

OpenDevelop owns the `externals/xamlstudio` submodule directly; it must not fetch source indirectly through a sibling directory in the UnoDevelop parent repository. The current ingestion baseline is commit `d711d64fed7d07d5c2dda545d255d1007588ab78`; when upgrading the submodule, the renderer compilation, standard control rendering, and error-recovery tests must be re-run.

### WinUI/Uno Host Decision

The designer runtime uses the special `Microsoft.UI.Xaml` implementation of `ProGPU.WinUI`, not the Uno runtime or `Uno.Sdk`. Uno Platform is only a supported project profile whose shared WinUI XAML is previewed by the ProGPU runtime. ProGPU currently materializes pages through the XAML compiler/Roslyn preview assembly and does not provide `Microsoft.UI.Xaml.Markup.XamlReader`; therefore XAML Studio's preprocessing, binding inspection, diagnostics, and result model remain as original linked source, while the final instantiation point connects to the ProGPU pipeline through `IProGpuXamlExecutor`. The WPF hosting part is built on the ProGPU render surface/`IWindowHost` and plays a role similar to `WindowsFormsHost`.

The current hosting control uses `WgpuContext`, `ProGPU.Scene.Compositor.RenderOffscreen`, and a WPF `WriteableBitmap`. Each arrange rebuilds the render target at the WPF DPI; WPF mouse/wheel/text/focus events are converted into ProGPU `InputSystem` events; unload/dispose cancels the frame callback and releases the staging buffers, textures, compositor, and context. The first version uses GPU-to-CPU readback to verify correctness first; later it should switch to same-device texture sharing between LibreWPF and ProGPU to avoid per-frame synchronous readback.

The former dependency gap is closed. The published `0.1.0-preview.47` feed was built from the
`wieslawsoltes/ProGPU` branch `openwpf` at commit `bab4dbef993f2b2d722ff46689021604c2e9b947`
(recorded in the `ProGPU.WinUI` nuspec `<repository>` element); that branch no longer exists on
GitHub, but the commit is still fetchable, and at that commit `ProGPU.Xaml`, `ProGPU.Xaml.Roslyn`,
`ProGPU.Xaml.Workspaces`, and `ProGPU.WinUI.Designer` all exist and are already marked
`IsPackable=true`. They were simply never published. OpenDevelop therefore hosts them itself:
they are packed from that exact commit and dropped into the same local feed as
`0.1.0-preview.47`, so there is no version drift and `LibreWPF.ProGPU` preview.41 — which was
compiled against ProGPU preview.47 — keeps its binary-compatible dependency closure. The
`ProGPU.*` pattern already present in `NuGet.config`'s `packageSourceMapping` covers the new
packages without configuration changes. Upstream's default branch has since moved to
preview.48 and still marks all four packable; if ProGPU ever publishes them, the local copies
should be dropped rather than upgraded piecemeal.

Note that ProGPU exposes **no** runtime XAML parser: `ProGPU.WinUI` contains a
`Microsoft.UI.Xaml.Markup` namespace and a `MarkupExtension` base type, but no `XamlReader`,
no `LoadComponent`, and no `IXamlMetadataProvider`. Materialization is only ever
compiler-driven, which is why `IProGpuXamlExecutor` is the correct seam.

- OpenDevelop's WPF visual tree does not host `Microsoft.UI.Xaml.UIElement` directly;
- WinUI dispatcher, resource lookup, XamlRoot, input, focus, and DPI can be embedded into a WPF document tab;
- when loading custom controls of the designed project, dependencies and `x:Bind`/code-behind can be safely isolated.

Implementation order and acceptance items:

1. Establish a standalone renderer/host assembly that links the XAML Studio renderer source files and renders pages containing only standard controls through the ProGPU XAML compiler/preview assembly pipeline.
2. Display the render surface in the WPF document area with the new ProGPU-in-WPF host; verify resize, input, focus, DPI, and theme resources.
3. Load valid/invalid XAML in succession and verify that exceptions never pollute the IDE and that the last valid preview can be restored.
4. After unloading a document, check that threads, windows, events, and the collectible load context are released.
5. Run at least once on Windows and on each non-Windows target ProGPU currently supports.

The host stays replaceable:

- **Preferred: in-process ProGPU WPF host.** Add a WPF hosting control similar to `WindowsFormsHost`; the renderer stays a separate assembly; this path serves both WinUI and Uno project profiles.
- **Option B: out-of-process preview host.** If the object models or dispatchers cannot safely coexist, launch a small WinUI/Uno preview process that exchanges XAML, project context, viewport, and selection over JSON-RPC, hosting the preview in a native child window or a captured surface. This option isolates better and is also more suitable for loading user assemblies.

Option B is the fallback for loading untrusted project assemblies and for native Windows App SDK-specific behavior; on Windows a `DesktopWindowXamlSource` adapter can also be added behind the same host contract. The WPF `XamlReader` compatibility renderer that was implemented at one point is not part of any official path: it conflates the object models, resource semantics, and control capabilities, and must be deleted — tests must not treat its successful rendering as a successful WinUI/Uno designer.

### Designer Chrome Decision

`ProGPU.WinUI.Designer` ships a complete in-surface designer: `DesignerHost`, `DesignerCanvas`,
`SelectionAdorner`, `PanelDragEditor`, `PropertyGrid`, `Toolbox`, `VisualTreeOutline`,
`DesignerSerializer`, and `VirtualizedCodeEditor`. Those chrome widgets are `Microsoft.UI.Xaml`
controls, so adopting them wholesale would render the Toolbox and Properties **inside** the
ProGPU surface and leave OpenDevelop's own pads empty — inconsistent with the WinForms and WPF
designers and contrary to the shared IDE-experience goal.

The decision is therefore **split chrome**: consume `WinUiXamlLivePreviewSession` for
materialization and (later) `DesignerCanvas`/`SelectionAdorner`/`PanelDragEditor` for the design
surface, but **not** ProGPU's `PropertyGrid`, `Toolbox`, or `VisualTreeOutline`. Toolbox,
Properties, and Outline are served through OpenDevelop's existing shell contracts —
`IToolsHost.ToolsContent`, `IHasPropertyContainer.PropertyContainer`, and
`IOutlineContentHost.OutlineContent` — exactly as `WpfViewContent` does.

Design-surface picking uses ProGPU's public `InputSystem.HitTest`, and maps the hit visual back
to the document through the WinUI namescope: the emitter never assigns `FrameworkElement.Name`, it
publishes names via `XamlTemplateFactory.RegisterName`, so `root.FindName(x)` is the supported way
back. Only the x:Name **string** crosses the host boundary - never a `Microsoft.UI.Xaml` object -
and a pick walks up to the nearest ancestor that exists in the source, because a hit normally lands
on a control-template part with no counterpart in the document. A surface pick and an Outline pick
call the same `SelectElement(name)`, so there is one selection concept rather than two.

Because that name map holds strong references to preview elements, it must be cleared before the
collectible preview assembly is unloaded; `WinUiXamlLivePreviewSession.Reset` requires the caller
to have detached the root first, and otherwise the whole ALC stays pinned. `LiveHostCount` and a
weak reference to the last preview root are exposed as lifecycle probes so
`WinUIDesigner_ClosingDocument_ReleasesRuntimeHostAndPreviewAssembly` can assert this for real
rather than merely observing that nothing crashed.

`WinUIXamlElementPropertyAdapter` backs the Properties pad with the XAML **source** element
(`System.Xml.Linq` only) rather than the live ProGPU visual. This keeps `Microsoft.UI.Xaml` out of
the shell and makes every property change a source mutation that re-parses, re-renders, and can
be undone.

### Backend Layering

| Layer | Responsibility | Reusable Source |
|---|---|---|
| Document model | XML/XAML nodes, stable IDs, source spans, diagnostics, text edits | AXSG/wxsg and UnoDevelop's UI-free logic |
| Render protocol | Load/Update, viewport, theme, diagnostics, visual-tree snapshot, selection | New; compatible with both in-process and out-of-process implementations |
| Renderer | Original `XamlRenderService` preprocessing/binding inspection, plus ProGPU compiler-driven instantiation (there is no runtime `XamlReader`) | Linked XAML Studio toolkit source + `ProGPU.Xaml.Roslyn`/`ProGPU.Xaml.Workspaces` |
| Runtime host | ProGPU WinUI visual root, render surface, WPF interop; does not parse XAML | New ProGPU-in-WPF host, replaceable by preview RPC/Windows adapter |
| OpenDevelop adapter | WPF secondary view, host lifecycle, Toolbox/Outline/Properties wiring | OpenDevelop shell + provider contracts |
| Editing operations | Insert, delete, move, resize, set property → versioned text edits | Three backends share command semantics; each generates its own edits |

Do not treat the runtime visual tree as the only document model. Every operation must ultimately produce an undoable source edit; re-parse and refresh the preview afterwards. This supports invalid intermediate text, Undo/Redo, formatting preservation, and out-of-process renderers.

Custom controls, merged dictionaries, `x:Bind`, and code-behind should not enter the first milestone. The first version loads only the standard controls and resources on a safe allowlist and shows diagnostics/placeholders for unsupported nodes.

## Local Feed and Packaging Workflow

`ProGPU.*` (including the never-published `ProGPU.Xaml.Roslyn`, `ProGPU.Xaml.Workspaces`,
`ProGPU.WinUI.Designer`) and `LibreWPF.*` are consumed from the local feed
`/Users/lextm/wpf-tools/librewpf/artifacts/local-feed` (see `NuGet.config`'s
`packageSourceMapping`). Every ProGPU.WinUI/designer change therefore requires repacking into
that feed, then clearing the NuGet global cache, then restoring OpenDevelop — same traps as the
LibreWPF workflow documented in [`librewpf.md`](librewpf.md):

1. Build **and pack** with the same configuration (`dotnet pack -c Release` — packing a Debug
   build or packing after a bare `dotnet build` ships stale bits).
2. Delete the old `.nupkg` from the feed before packing (a partial/failed pack must not leave a
   stale copy behind).
3. Delete the matching `~/.nuget/packages/<id>` folder(s) — NuGet serves the first restored copy
   forever for an unchanged version string.
4. `dotnet restore --force --no-cache`, then relaunch. Stale `obj`/`bin` in OpenDevelop's own
   projects can also hide changes (see `librewpf.md`'s second trap).

The four `ProGPU.*` designer packages are packed from the `progpu-p47` worktree
(`/Users/lextm/wpf-tools/progpu-p47`, wieslawsoltes/ProGPU at `bab4dbef`). Future ProGPU work
beyond preview.47 (e.g. the control/API additions in the problem catalog below) should be carried
on the lextudio fork (which is what `progpu`/`progpu-p47` track) and packed at a new version line.

## Real-World Project Preview Problem (2026-08-14)

### Symptom

Opening a real Uno Platform project's `MainPage.xaml` in the Design tab produces a **wall of
diagnostics** (169 lines for DotUninstall's `Presentation/MainPage.xaml`) and the preview does
not materialize. The fixture sample (`src/Samples/UnoXamlSample/MainPage.xaml`) is fine because
it only uses `Grid`/`StackPanel`/`TextBlock`/`Button`.

The full diagnostics are returned by the `od.winui-designer.status` action (`status` field) and
shown in the status line under the design surface. Because they live in that TextBlock, they
cannot be selected/copied in the UI — a product gap (see roadmap below).

### Root causes already found and fixed (2026-08-14)

Two independent bugs made the design surface render an empty frame even for the fixture sample;
both were diagnosed live via DevFlow and are fixed in `WinUIXamlDesigner.ProGPUHost`:

1. **Text never rendered — `PopupService.DefaultFont` was never initialized.**
   ProGPU's `Window` constructor is the only place that sets the process-wide
   `PopupService.DefaultFont` (on macOS: `/System/Library/Fonts/Supplemental/Arial.ttf`).
   The offscreen host creates no `Window`, so `DefaultFont` stayed `null`, and
   `RichTextBlock.GetOrUpdateRenderCommandCache` returned an empty command cache for every
   `TextBlock`/button label — the compositor compiled zero glyphs (`glyphs=0, glyphBatches=0`
   in the compositor metrics). Fixed in `ProGpuRuntimeHost` with `EnsureDefaultFont()`,
   mirroring the `Window` constructor. Verified: the WinUI command probe went from
   `commands=3 [DrawRoundedRect=3]` to `commands=11 [DrawText=8, DrawRoundedRect=3]`, and the
   frame readback shows the text rows.

2. **Presented frame invisible on screen — `WgpuContext.Current` was clobbered.**
   `WgpuContext.Initialize` sets the thread-static `WgpuContext.Current` to *this* context
   (`Current = this` in `ProGPU.Backend/WgpuContext.cs`). Creating the host's own offscreen
   context therefore stole `Current` from LibreWPF on the UI thread. LibreWPF's
   `WpfBitmapSourceImageAdapter` prefers `Current` when creating the GPU texture for a
   `DrawImage`/`DrawTexture` command, so the frame bitmap was uploaded onto the designer's
   context while the WPF window is composited on LibreWPF's own context — a cross-device
   texture that silently renders nothing (the red diagnostic border and text, being vector
   primitives, still showed, which is what made this diagnosable). Fixed in
   `ProGpuWinUIHostControl.Start()` by saving and restoring `WgpuContext.Current` around
   `context.Initialize(null)`. Verified: `capturedAtOnRender` now reports LibreWPF's context.

Note the readback path itself was always healthy — the frame content was provably in the
`WriteableBitmap` buffer; only the on-screen presentation was lost to the cross-context texture.

### Diagnostic tooling added (DevFlow actions, temporary)

Kept for now to support further investigation; all under `od.winui-designer.*`:

- `frame-profile` — row-by-row non-white pixel profile + per-pixel samples of the presented
  frame (reads the `WriteableBitmap` buffer; not an OS screenshot).
- `compositor-metrics` — the `Compositor.Metrics` snapshot (draw calls, vector/text vertices,
  glyph/path-atlas counts, pipeline counts, retained-composition state, timings).
- `draw-calls` — reflection dump of the compositor's `_drawCalls` (read between frames, so the
  list is restored to empty; the metrics are the reliable source).
- `winui-commands` — walks the WinUI visual tree, calls `OnRender` on every node, and reports
  the emitted command counts per type.
- `image-path` — replays LibreWPF's `WpfBitmapSourceImageAdapter` path step by step
  (`TryGetGpuTexture`, portable pixels, context identity at OnRender).
- `overlay` — red border + status text + two 64×64 test images (WriteableBitmap vs
  `BitmapSource.Create`) drawn in `OnRender`, to isolate vector vs image rendering.
- `recreate-bitmap` / `background-brush` — alternate presentation experiments (in-place
  `WriteableBitmap` updates vs per-frame recreation vs `Background = ImageBrush`).

### Real-project diagnostics catalog (DotUninstall, 169 lines)

Categories, in order of frequency:

1. **Project's own types unresolved (~140 lines).** `conv:NullToVisibilityConverter`,
   `conv:BoolToVisibilityConverter`, ... (`using:DotNetUninstall.Presentation.Converters`) and
   `controls:TwoPartBadge`/`controls:SingleBadge`
   (`using:DotNetUninstall.Presentation.Controls`) fail to resolve, and every member of an
   unresolved owner follows ("Member 'Label' cannot be resolved because its owner type is
   unresolved", ~8 member errors per usage). **Root cause: the preview compilation
   (`ProGpuXamlExecutor.EnsureProject`) builds an `AdhocWorkspace` from the framework metadata
   references + the ProGPU runtime directory only — the project's own source files (converters,
   custom controls, code-behind) and its output assemblies are never included.**
2. **Code-behind event handlers unresolved.** `Code-behind event handler
   'OnMessageCenterFlyoutOpening' was not found or does not match the event delegate` and
   `'OnOpenReleasePage' ... 'Microsoft.UI.Xaml.RoutedEventHandler?'` — same root cause
   (code-behind is not compiled).
3. **`muxc:` (Microsoft.UI.Xaml.Controls) types unresolved.** `muxc:InfoBar`, `muxc:InfoBadge`,
   and their members. Two layers: (a) the preview references do not include the project's own
   WinUI assemblies (Uno.WinUI), and (b) ProGPU.WinUI does not implement `InfoBar`/`InfoBadge`
   at all, so even with references added they could only resolve if the analysis compilation
   used the project's Uno references (see Fix roadmap).
4. **WinUI baseline APIs missing from ProGPU.WinUI.** `Member 'ColumnSpacing' was not found on
   'Microsoft.UI.Xaml.Controls.Grid'`; `Member 'Loaded' was not found on
   'Microsoft.UI.Xaml.Controls.Button'`; `Type 'Microsoft.UI.Xaml.Controls.Pivot' does not
   declare a content member` (Pivot exists in ProGPU.WinUI but lacks the content-member
   declaration).
5. **GridLength/star sizing conversion.** `Text '*,Auto' cannot be converted to
   'Microsoft.UI.Xaml.Controls.ColumnDefinition' by profile 'WinUI'` (×2, plus
   `'Auto,*,Auto' ... RowDefinition`). `WinUiXamlProfile.TryCreateGridLength` handles
   `Auto`/`*`/absolute for `GridLength`, but `TryCreateLiteralExpression` has no
   `ColumnDefinition`/`RowDefinition` case, so whole-string grid shorthand fails.
6. **StaticResource forward reference.** `StaticResource 'InstallEntryTemplate' is declared
   later in the same lexical resource chain` — the checker rejects page-level forward
   references that real WinUI/Uno accepts.

### Fix roadmap (options under consideration)

Two orthogonal workstreams; both are needed:

**A. OpenDevelop side — give the preview compilation project context.**
Add the opened `IProject`'s source files and output assemblies (including the project's own
`DotNetUninstall.dll` and, for a Uno profile, the Uno.WinUI assemblies from `bin`) as
`Document`s/`MetadataReference`s in `ProGpuXamlExecutor.EnsureProject`. This eliminates
category 1 and 2 entirely (~140 lines) and, with the Uno assemblies referenced, category 3a.
Materialization must then decide how to deal with types that exist in Uno but not in
ProGPU.WinUI — options:

- (1) compile against ProGPU.WinUI + the project's own assembly only: project types resolve,
  ProGPU-missing WinUI APIs still error;
- (2) separate analysis compilation (Uno references, real diagnostics) from materialization
  compilation (ProGPU references, renderable subset) — the VS Uno-designer-style split;
- (3) adopt the Uno runtime for rendering — explicitly rejected by the host decision above.

Recommended: (1) immediately, with an interface seam for (2).

**B. ProGPU side — extend `ProGPU.WinUI`/`ProGPU.Xaml.Roslyn` toward WinUI baseline.** Each
item has a known landing site:

| Missing piece | Landing site | Rough size |
|---|---|---|
| `Grid.ColumnSpacing`/`RowSpacing` | `src/ProGPU.WinUI/Controls/Grid.cs` | ~50 lines |
| `FrameworkElement.Loaded` event | `src/ProGPU.WinUI/Core/FrameworkElement*.cs` | ~30 lines |
| `Pivot` content member | `src/ProGPU.WinUI/Controls/Pivot.cs` | ~10 lines |
| `ColumnDefinition`/`RowDefinition` text conversion | `src/ProGPU.Xaml.Roslyn/WinUiXamlProfile.cs` | ~50 lines |
| `InfoBadge` control | new, `src/ProGPU.WinUI/Controls/` | ~100-200 lines |
| `InfoBar` control | new, `src/ProGPU.WinUI/Controls/` | ~300-400 lines |
| StaticResource forward reference | `ProGPU.Xaml` checker | ~30-50 lines |

**C. Product improvement — diagnostics surfacing.** Route designer diagnostics into the shared
Error List / Message View (copyable, line-navigable) instead of the unselectable status
TextBlock under the design surface; keep `od.winui-designer.status` as the DevFlow surface.

**Suggested order:** A (project context) first — it removes most of the noise — then B by
frequency (`ColumnDefinition`/`ColumnSpacing`/`Loaded` are high-frequency in real XAML;
`InfoBar`/`InfoBadge` mid; `Pivot`/StaticResource low), then C.
