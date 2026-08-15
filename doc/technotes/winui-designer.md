# WinUI/Uno Designer

This technote is the dedicated home for the WinUI/Uno designer: architecture decisions, the
XAML Studio/ProGPU integration boundary, packaging workflow, the current state, and the
real-world preview problem catalog (2026-08-14). The cross-designer roadmap (WinForms + WPF +
WinUI together), framework detection, provider contracts, phases, and the test matrix live in
[`xaml-services.md`](xaml-services.md).

Current status: the designer is integrated end-to-end for the `src/Samples/UnoXamlSample`
fixture (detect → compile → materialize → render → present → edit round-trips, covered by
`WinUIDesigner_*` integration tests). Real-world Uno projects are blocked at a structural ceiling
of the current in-process host, not merely missing features — **the decision (2026-08-14) is that
real-project support requires an out-of-process host**, see
[the decision record](#out-of-process-host-decision-2026-08-14) and
[the problem section](#real-world-project-preview-problem-2026-08-14) for what's fixed
(type-resolution diagnostics) versus what remains blocked (materialization).

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

> **Superseded 2026-08-14:** the in-process ProGPU path described below is retired as a rendering
> path (no in-process fallback); the out-of-process host is the *only* renderer. See
> "Out-of-process host decision" and "Out-of-process host scoping" below for the operative
> architecture. This section is kept as history and for the RPC-host shape it anticipated.

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

- **In-process ProGPU WPF host (current, first milestone).** A WPF hosting control similar to
  `WindowsFormsHost`; the renderer stays a separate assembly; this path serves both WinUI and Uno
  project profiles for the standard-control fixture. Confirmed by the 2026-08-14 investigation
  below to be a dead end for real projects, not merely a "fallback for untrusted assemblies" as
  originally scoped — see "Out-of-process host decision (2026-08-14)". **Retired 2026-08-14:** no
  in-process fallback; the fixture renders through the out-of-process host like any real project.
- **Out-of-process preview host.** A small WinUI/Uno preview process that exchanges XAML, project
  context, viewport, and selection over JSON-RPC, hosting the preview in a native child window or
  a captured surface (the same shape as `DesktopWindowXamlSource` on Windows). This is now the
  target architecture for real-project support, not merely an isolation upgrade.

The WPF `XamlReader` compatibility renderer that was implemented at one point is not part of any official path: it conflates the object models, resource semantics, and control capabilities, and must be deleted — tests must not treat its successful rendering as a successful WinUI/Uno designer.

### Out-of-process host decision (2026-08-14)

**Decision: real-project support requires an out-of-process host running the actual Uno.WinUI
runtime.** This supersedes the original framing of out-of-process as merely "Option B, a fallback
for untrusted assemblies" (the "Host stays replaceable" list above records the earlier framing).
Two independent findings from implementing Fix A (below) forced this:

1. **Type identity.** `ProGPU.WinUI` is a from-scratch reimplementation of `Microsoft.UI.Xaml` —
   its `FrameworkElement`, `Button`, etc. are unrelated CLR types that merely share a name and
   namespace with the real Uno.WinUI SDK's types of the same name. A single Roslyn compilation
   can reference at most one of them without "ambiguous type" errors, so the preview compiler can
   never see both ProGPU.WinUI (needed to materialize *anything*, since it is the only runtime
   this in-process host can actually render) and the project's real Uno.WinUI references (needed
   to resolve `muxc:InfoBar`, the real `Grid.ColumnSpacing`, etc.) at the same time. This is a
   structural ceiling on the in-process host, not a missing feature - no amount of `ProGPU.WinUI`
   API completion (roadmap item B below) removes it for a project that references Uno.WinUI types
   ProGPU.WinUI doesn't implement.
2. **Runtime load, not just compile-time resolution.** Fix A adds the opened project's own output
   assembly (e.g. `UnoXamlSample.dll`) as a `MetadataReference` for the preview *analysis*
   compilation - this genuinely resolves the project's own converters/custom controls/code-behind
   as *types* (category 1+2 in the diagnostics catalog below no longer appear). But the generated
   preview program is materialized into a separate collectible `AssemblyLoadContext`
   (`WinUiXamlLivePreviewSession`'s `PreviewAssemblyLoadContext`, in `ProGPU.WinUI.Designer`) that
   has no load path to the project's own build output directory. Verified live: after Fix A, a
   converter type resolves cleanly with zero diagnostics, but materialization then fails with
   `Could not load file or assembly 'UnoXamlSample, Version=1.0.0.0, ...'. The system cannot find
   the file specified.` — trading a wall of compile-time diagnostics for a single, clearer
   runtime-load error, but still not a rendered preview. Teaching that ALC to probe the project's
   output directory is a small, separate fix (`PreviewAssemblyLoadContext.Load` override) and
   *would* work for the project's own assembly - but finding 1 still blocks it the moment the
   project references any real Uno.WinUI-only type, which real Uno projects do pervasively (every
   `Page`/`FrameworkElement` base class the generated program itself needs to be ProGPU.WinUI's,
   while the project's compiled code needs the real Uno.WinUI's - the same collectible ALC cannot
   satisfy both for a project that mixes them, which is every real Uno project, not an edge case).

This is exactly the problem class Microsoft's own out-of-process WinForms designer solves for the
in-process-hosting equivalent risk (see
[the .NET blog post on it](https://devblogs.microsoft.com/dotnet/custom-controls-for-winforms-out-of-process-designer/)):
run the *real* runtime the project targets in its own process, and talk to it over RPC, rather
than trying to reconcile two incompatible in-process object models. For WinUI/Uno the need is
structural (type identity), not merely defense-in-depth against a crashing/untrusted assembly -
unlike the WinForms designer's own hosting choice (see
[`winforms-designer.md`](winforms-designer.md#out-of-process-hosting-lower-priority-2026-08-14)),
which already runs the real `System.Windows.Forms` in-process via `WindowsFormsHost` with no
competing reimplementation, so it does not have this specific forcing function.

**What ships now vs. later (updated 2026-08-14, same day):**

- **There is no in-process fallback.** The out-of-process host is the *only* renderer path for the
  WinUI designer, including the `src/Samples/UnoXamlSample` fixture. The in-process ProGPU host
  (`WinUIXamlDesigner.ProGPUHost`) never reached working real-project materialization (the
  diagnostics catalog below), so keeping it as "the fallback" would mean maintaining a second
  renderer of unknown viability while the product ships on the OOP path. The fixture is itself a
  real Uno project, so a single code path (child process running the project's own runtime) serves
  both it and real projects; retired ProGPU host code stays in history. `ProGpuRuntimeHost` and
  `ProGpuXamlExecutor` are superseded by the child host scoped below.
- Fix A's compile-time half (project assembly as a `MetadataReference`) remains valid for what it
  is - quick in-process feedback - but no longer feeds a product renderer; diagnostics for real
  projects come from the child process's own runtime parser instead.
- Roadmap item B (ProGPU.WinUI API completion) is **parked**: its only remaining consumer would be
  the in-process path this section now retires. If the in-process renderer is ever resurrected, B
  resumes by the frequency ordering recorded below.
- Out-of-process scoping: see "Out-of-process host scoping (2026-08-14)" below.

### Out-of-process host scoping (2026-08-14)

Verified ground truth this scoping rests on (all from the Uno source tree `uno-tools/uno`, package
line 6.6.184, and the DotUninstall project's restored graph):

1. **The project's runtime is loadable on macOS.** A Uno.Sdk `net10.0-desktop` project (e.g.
   DotUninstall) *is* a Skia desktop app; its `bin/Debug/net10.0-desktop` contains the full runtime
   (`Uno.WinUI.Runtime.Skia`, `...Skia.MacOS`, SkiaSharp, FluentTheme, fonts). No Windows runtime
   is involved at any point.
2. **Uno has a real runtime XAML parser.** `Microsoft.UI.Xaml.Markup.XamlReader.Load(string)`
   (`src/Uno.UI/UI/Xaml/Markup/XamlReader.cs`) runs the production `XamlStringParser` +
   `XamlObjectBuilder` (`src/Uno.UI/UI/Xaml/Markup/Reader/`), which resolves types from loaded
   assemblies and defers resource/template expansion via a post-action queue. This entirely
   replaces the ProGPU compiler pipeline (the XamlStudio/ProGPU.Xaml.Roslyn port in this repo) for
   the OOP path: no Roslyn compilation, no collectible ALC, no type-identity conflict, because
   "the runtime" and "the project's runtime" are the same assemblies in one process.
3. **Offscreen rendering needs no window.** `RenderTargetBitmap.RenderAsync(element)` on Skia
   (`src/Uno.UI/UI/Xaml/Media/Imaging/RenderTargetBitmap.skia.cs`) creates a CPU `SKSurface`,
   forces the software compositor (`Compositor.IsSoftwareRenderer = true`), clears the layout clip,
   and renders `element.Visual` via `RenderRootVisual` - no Metal, no NSApplication, no visible
   surface. `GetPixelsAsync()` returns BGRA8 premultiplied pixels. DPI resolves to 1 when there is
   no XamlRoot/current view.
4. **Headless boot is a dispatcher override, not a native host.** `NativeDispatcher` on Skia
   exposes `DispatchOverride` + `HasThreadAccessOverride` (the exact hooks `MacSkiaHost` sets via
   `MacOSDispatcher`); after setting them, `Application.Start(...)` initializes the full runtime
   (`Application.skia.cs`) without AppKit. The macOS native shim (`UnoNativeMac`/NSApplication) is
   only needed for real windows.
5. **Dependency-identity packaging.** `dotnet exec --runtimeconfig <project>.runtimeconfig.json
   --depsfile <project>.deps.json <host.dll>` makes the child's dependency graph *be* the
   project's dependency graph. The child is a thin shim; all Uno assemblies resolve from the
   project's bin, so the parent "OpenDevelop-side version" problem disappears - the child always
   runs the exact Uno the project references.

**Architecture.** Child process `UnoDesignHost` (Uno.Sdk `net10.0-desktop` console shim, no app
template): boots headless Uno (fact 4), runs in the project's dependency context (fact 5),
materializes the design surface's current XAML text via `XamlReader.Load` (fact 2), renders
offscreen via `RenderTargetBitmap` (fact 3), and answers an RPC over stdio. OpenDevelop's shell
(`WinUIXamlDesignerViewContent`, toolbox, property pad, outline, DevFlow actions) keeps its
contracts; only `WinUIXamlHost`'s backing changes from in-process ProGPU to child-process RPC. The
fixture (`UnoXamlSample`) is handled by the same child by pointing it at the fixture's own bin.

**Runtime adapters.** One protocol; per-runtime bootloaders. Uno headless Skia is the first
adapter and the only one reachable from macOS. A WinAppSDK adapter is Windows-only, needs in-child
Roslyn XAML compilation (WinAppSDK has no `XamlReader`), and is out of scope until the Uno adapter
is on its feet. ProGPU is retired as a rendering path entirely (see above). The protocol is
runtime-agnostic so a future adapter is additive.

**Protocol surface** (JSON-RPC 2.0 over stdio, LSP-style framing already used elsewhere in this
repo):

- `initialize` `{projectAssembly}` -> `capabilities`: toolbox catalog (categories, glyphs, default
  XAML template snippets, required namespaces), supported theme resources, parser profile name.
  The catalog is generated in-child by reflecting the *loaded* runtime assemblies and merged with a
  per-runtime design-time allowlist - the toolbox always matches the project's actual Uno version.
- `load` `{xaml, viewportWidth, viewportHeight, dpi}` -> `{elementTree, diagnostics}`: materialize
  via `XamlReader.Load`, measure/arrange at the viewport, return the namescope-backed element tree
  (x:Name, type, bounds via `TransformToVisual`) and parse/layout diagnostics. An implicit render
  follows.
- `render` -> `{bitmap (BGRA8), width, height}`: `RenderTargetBitmap` readback. On-demand only
  (source edit, viewport resize, theme change) - never a frame stream, so base64 in JSON is an
  acceptable first transport; shared-memory transport is a later optimization if latency demands.
- `hit-test` `{x, y}` -> `{chain}`: nearest named elements with bounds, resolved in-child, so
  selection mapping is authoritative (x:Name crosses the boundary, never UI objects - same rule as
  the in-process design's `FindName` rule).
- `shutdown`; `log`/`diagnostics` notifications (async layout/runtime errors outside a request).

**Editing model unchanged:** every operation is a versioned XAML source edit in OpenDevelop
(existing XML document model + `WinUIXamlElementPropertyAdapter`), followed by `load` + re-render.
No child-side mutation protocol in the first milestone.

**Milestones:**

- **M0 - Headless probe (this session):** throwaway console project boots headless Uno on macOS,
  `XamlReader.Load`s a small XAML, `RenderTargetBitmap` renders it, saves a PNG. Retires facts 2-4
  live; everything else is ordinary engineering.
- **M1 - Child host protocol:** `UnoDesignHost` with the stdio JSON-RPC surface above (load,
  render, hit-test, capabilities, shutdown), exercised by a CLI driver against the fixture's bin.
- **M2 - OpenDevelop wiring:** `WinUIXamlHost` remote mode - spawn child with the opened project's
  runtimeconfig/depsfile, present `render` bitmaps on the existing WPF surface, forward pointer
  input to `hit-test`, selection round-trip to outline/properties. Fixture renders end-to-end.
- **M3 - Real-project parity:** DotUninstall (`muxc:` types, converters, custom controls,
  code-behind gaps) renders; diagnostics from the child's parser shown in the status control;
  viewport resize/re-render on every edit; design-time allowlist for unsupported constructs.
- **M4 - (Windows-only, later):** WinAppSDK adapter with in-child Roslyn XAML materialization.

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

## Toolbox-to-design-surface drop was silently landing at the document root (fixed 2026-08-14)

A real synthetic-mouse-drag test enrichment (bringing the WinUI designer's drag-drop test coverage
up to parity with the WPF designer's) surfaced two independent, real bugs that a weaker
substring-based test assertion had been masking - every toolbox-to-canvas drop had been silently
falling back to inserting at the **document root** instead of the container the user visibly
dropped onto, and the original test's `Assert.Contains("<TextBlock", onDisk)` couldn't tell the
difference between that and success.

1. **`InputSystem.HitTest` was hit-testing against a stale/absent root.**
   `InputSystem.HitTest` (in `ProGPU.WinUI`) bails out immediately if `InputSystem.Current.Root`
   is null (`if (_root == null) return null;`). That root is only ever set by
   `ProGpuWinUIHostControl.SelectInput`, itself only called from real mouse move/down/up on the
   render surface - never from a WPF `DragEventArgs.Drop`. A real toolbox drag starts on the
   Toolbox pad and never first moves the mouse over the design surface, so `Current.Root` was
   simply never set (or stale from an unrelated host), and every drop's hit test silently
   returned null regardless of where the pointer actually was - confirmed live via
   `LastPickDiagnostic`: a drop dead-center on a button's own on-screen bounds reported a
   hit-test point that was numerically correct against that button's local bounds, yet resolved
   to nothing. Fixed in `ProGpuRuntimeHost.ResolveNameAt`
   (`WinUIXamlDesigner.ProGPUHost/ProGpuRuntimeHost.cs`) by explicitly setting
   `InputSystem.Current.Root = control.WinUIRoot` before hit-testing, rather than depending on
   incidental prior mouse traffic having set it.
2. **A resolved leaf control was used directly as the insertion container.** Once (1) was fixed,
   a drop onto `PrimaryButton` resolved the name correctly (matching click-to-select's own
   resolution), but `InsertFromToolbox` inserted the new element as `PrimaryButton`'s own child -
   which the real WinUI compiler correctly rejects for anything with a single-value content
   property ("Member '\$content' cannot contain multiple values"). `ResolveNameAt`'s "nearest
   named ancestor" is the right answer for click-to-select, but not for drop-target resolution: a
   drop onto an existing leaf control is aiming at its *container*, not asking to become that
   leaf's own content. Fixed in `WinUIXamlDesignerViewContent.InsertFromToolbox` by walking up
   from the resolved element to the nearest ancestor whose tag is one of the toolbox's two actual
   multi-child panel types (`Grid`, `StackPanel`), matching what a real design surface does.

A third, separate finding did **not** get a product fix (out of scope, upstream ProGPU.WinUI):
`ProGPU.WinUI`'s hit-test `HasBackground` check (`InputSystem.HitTestInternal`) only recognizes
`Control`/`Border`/`ContentPresenter`, not `Panel`/`StackPanel` - so setting `Background` on a
`StackPanel` has no effect on whether its own empty area is hit-testable, unlike real WinUI/UWP.
The `src/Samples/UnoXamlSample/MainPage.xaml` fixture's `StackPanel` was named `RootStack` for the
new test's parent-comparison assertion, but does **not** carry a `Background` (it would have no
effect and could mislead a future reader into thinking it does something).

See `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer`'s test body and
comments for the full before/after repro detail.

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

1. **Project's own types unresolved (~140 lines) — FIXED 2026-08-14, at the type-resolution
   layer only.** `conv:NullToVisibilityConverter`, `conv:BoolToVisibilityConverter`, ...
   (`using:DotNetUninstall.Presentation.Converters`) and `controls:TwoPartBadge`/`controls:SingleBadge`
   (`using:DotNetUninstall.Presentation.Controls`) failed to resolve, and every member of an
   unresolved owner followed ("Member 'Label' cannot be resolved because its owner type is
   unresolved", ~8 member errors per usage). Root cause: the preview compilation
   (`ProGpuXamlExecutor.EnsureProject`) built an `AdhocWorkspace` from the framework metadata
   references + the ProGPU runtime directory only — the project's own source files (converters,
   custom controls, code-behind) and its output assembly were never included. Fixed in
   `ProGpuXamlExecutor.CollectMetadataReferences` by resolving the opened project via
   `SD.ProjectService.FindProjectContainingFile` and adding its `OutputAssemblyFullPath` as a
   `MetadataReference` (deliberately NOT the project's own Uno.WinUI references - see
   "Out-of-process host decision" above for why that would create ambiguous-type errors instead).
   Verified live on a synthetic reproduction (a dependency-free marker class referenced from XAML
   as a resource): the diagnostic disappears entirely. **This closes the compile-time
   type-resolution half of the problem, not materialization** - see the out-of-process host
   decision above for the runtime-load half this surfaced (`PreviewAssemblyLoadContext` has no
   load path to the project's own build output).
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

### Fix roadmap (updated 2026-08-14)

Three workstreams; A is partly done, B and D are independent of each other, C is a quick, cheap
win done alongside A.

**A. OpenDevelop side — give the preview compilation project context.**
**Done (compile-time half):** the opened project's own output assembly is now a
`MetadataReference` in `ProGpuXamlExecutor.EnsureProject` (see the catalog entry above) — this
eliminates category 1 and 2 (~140 lines). **Not done (runtime half):** materialization still
fails to *load* that assembly (see "Out-of-process host decision" above) — teaching
`PreviewAssemblyLoadContext` to probe the project's output directory would close this for the
project's own assembly specifically, but does not help category 3a (`muxc:` types), which needs
the project's real Uno.WinUI references - blocked by the type-identity conflict, i.e. blocked on
workstream D.

**B. ProGPU side — extend `ProGPU.WinUI`/`ProGPU.Xaml.Roslyn` toward WinUI baseline.** Still
fully applicable regardless of the out-of-process decision: it's what makes the in-process host
(the fixture-sample renderer, and any real project restricted to the ProGPU.WinUI-covered
subset) more capable. Each item has a known landing site:

| Missing piece | Landing site | Rough size |
|---|---|---|
| `Grid.ColumnSpacing`/`RowSpacing` | `src/ProGPU.WinUI/Controls/Grid.cs` | ~50 lines |
| `FrameworkElement.Loaded` event | `src/ProGPU.WinUI/Core/FrameworkElement*.cs` | ~30 lines |
| `Pivot` content member | `src/ProGPU.WinUI/Controls/Pivot.cs` | ~10 lines |
| `ColumnDefinition`/`RowDefinition` text conversion | `src/ProGPU.Xaml.Roslyn/WinUiXamlProfile.cs` | ~50 lines |
| `InfoBadge` control | new, `src/ProGPU.WinUI/Controls/` | ~100-200 lines |
| `InfoBar` control | new, `src/ProGPU.WinUI/Controls/` | ~300-400 lines |
| StaticResource forward reference | `ProGPU.Xaml` checker | ~30-50 lines |

**C. Product improvement — diagnostics surfacing. Done 2026-08-14.** The design-surface status
control (`WinUIXamlDesignerViewContent`'s `status` field) is now a read-only, scrollable
`TextBox` instead of a plain `TextBlock` - diagnostics can be selected and copied like any other
text, without needing the full Error List integration this item originally proposed. Routing into
the shared Error List / Message View (line-navigable, filterable alongside build errors) remains
a further improvement, not yet done; `od.winui-designer.status` stays the DevFlow surface either
way.

**D. Out-of-process host for real-project support.** See "Out-of-process host decision
(2026-08-14)" and "Out-of-process host scoping (2026-08-14)" above - the only path past the
type-identity ceiling A hit, now the *only* renderer path (no in-process fallback, decided same
day). Required for category 3 (`muxc:` types, via the project's own runtime) and for any real
project whose XAML needs a WinUI API ProGPU.WinUI doesn't implement (category 4-6). Scoped;
M0 (headless probe) is underway.

**Suggested order (updated 2026-08-14):** A's compile-time half stays only as introspection
tooling. C is done. Scope D is done. Next: execute D's milestones M0 -> M3 (probe, protocol,
OpenDevelop wiring, real-project parity). B (ProGPU extension) is **parked** - its only consumer,
the in-process host, is retired.
