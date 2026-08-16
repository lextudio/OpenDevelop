# WinUI Designer Runtimes: Uno, Windows App SDK, and ProGPU

This technote is the dedicated home for the WinUI-family designer: architecture decisions, the
XAML Studio/ProGPU integration boundary, packaging workflow, the current state, and the
real-world preview problem catalog (updated 2026-08-15). The cross-designer roadmap (WinForms + WPF +
WinUI together), framework detection, provider contracts, phases, and the test matrix live in
[`xaml-services.md`](xaml-services.md).

Current status: the out-of-process Uno host is implemented and is the preferred renderer for Uno
projects. It starts a real Uno 6.5.31 `net10.0-desktop` child, loads XAML and application
resources, renders a bitmap, returns a visual-tree snapshot and hit-test results, and supplies a
runtime-derived Toolbox catalog. The WPF-side surface also implements selection overlays, zoom,
pan, design-size changes, drag/resize source edits, inline text editing, unnamed-element picking,
and child-process lifecycle probing. The `WinUIDesigner_*` integration tests cover the shared
source-edit/render path against `src/Samples/UnoXamlSample`.

There are **three supported runtime profiles**, not two interchangeable names for one renderer:

| Project/runtime profile | Renderer | Host model | Current state |
|---|---|---|---|
| Uno Platform (`Uno.Sdk`, `Uno.WinUI`) | Bundled Uno 6.5.31 runtime today; project runtime is the target | Out of process | Implemented for the fixture; project-version loading remains |
| ProGPU WinUI (`ProGPU.WinUI`) | ProGPU's WinUI-shaped runtime and compositor | In process in the LibreWPF shell | Standard-control path implemented; must be routed explicitly |
| Native WinUI (`Microsoft.WindowsAppSDK`) | The project's Windows App SDK/WinUI runtime | Windows-only, out of process | Planned adapter |

The repository does not yet encode that routing completely. `XamlFrameworkKind` currently has
only `WinUI` and `Uno`; the detector recognizes Uno and Windows App SDK markers but not
`ProGPU.WinUI`. Both host factories are then registered globally, with the Uno factory tried
first whenever its child binary exists, regardless of framework kind. Before calling all three
profiles product-complete, add an explicit ProGPU profile (or an equivalent runtime discriminator)
and make each factory decline projects it does not own. ProGPU must not be a silent fallback for
an Uno or Windows App SDK project, and the Uno child must not claim a ProGPU or native WinUI
project.

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

### Runtime Routing Decision (2026-08-15)

The common `Microsoft.UI.Xaml` namespace is a source-level compatibility surface, not a CLR type
identity guarantee. Runtime selection therefore follows project evidence and is never chosen by
asking which renderer happens to be installed:

1. `Uno.Sdk`, `Uno.WinUI`, or `Uno.UI` selects the out-of-process Uno adapter.
2. `ProGPU.WinUI` (or an explicit future ProGPU project property) selects the in-process ProGPU
   adapter. This is the supported cross-platform **WinUI on ProGPU** profile.
3. `Microsoft.WindowsAppSDK`, `Microsoft.UI.Xaml`, or `UseWinUI=true` selects the native Windows
   App SDK adapter and is available only on Windows.

The routing order above is also the detector precedence when a project contains incidental
references from more than one family. A conflicting project should produce a diagnostic rather
than fall through to a different runtime. The host registry contract remains useful, but its
factories must be predicates over `XamlFrameworkContext`, not availability-based fallbacks.

ProGPU is a legitimate renderer for projects authored against `ProGPU.WinUI`; it is not a
compatibility renderer for assemblies built against Uno.WinUI or the Windows App SDK. Conversely,
the Uno child must run the project's Uno assemblies, and a native WinUI child must run the
project's Windows App SDK assemblies. No `Microsoft.UI.Xaml` object crosses into the WPF shell in
any profile.

### ProGPU WinUI Host

> **Updated 2026-08-15:** the in-process ProGPU path is supported for projects targeting
> `ProGPU.WinUI`. It remains retired as a renderer or fallback for projects targeting Uno.WinUI
> or the Windows App SDK because those assemblies have incompatible type identities.

This profile uses the `Microsoft.UI.Xaml` implementation supplied by `ProGPU.WinUI`; it does not
load Uno or Windows App SDK UI assemblies. ProGPU currently materializes pages through its XAML
compiler/Roslyn preview assembly and does not provide `Microsoft.UI.Xaml.Markup.XamlReader`;
therefore XAML Studio's preprocessing, binding inspection, diagnostics, and result model remain as
original linked source, while the final instantiation point connects to the ProGPU pipeline
through `IProGpuXamlExecutor`. The WPF hosting part is built on the ProGPU render surface/
`IWindowHost` and plays a role similar to `WindowsFormsHost`.

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
packages without configuration changes. Upstream's default branch is at the preview.48 baseline
(`d63f5cfa`, 2026-08-12) and still marks all four packable; OpenDevelop remains pinned to the
internally consistent preview.47 package set. If ProGPU publishes the missing packages, the local
copies should be dropped rather than upgraded piecemeal.

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

The shell boundary stays replaceable:

- **In-process ProGPU WPF host.** A WPF hosting control similar to `WindowsFormsHost`; the
  renderer stays a separate assembly. This path serves the ProGPU WinUI profile only. It is a
  dead end for Uno and native Windows App SDK project assemblies, but that does not invalidate it
  for projects that actually reference ProGPU.WinUI.
- **Out-of-process preview host.** A small WinUI/Uno preview process that exchanges XAML, project
  context, viewport, and selection over JSON-RPC, hosting the preview in a native child window or
  a captured surface (the same shape as `DesktopWindowXamlSource` on Windows). This is now the
  target architecture for real-project support, not merely an isolation upgrade.

The WPF `XamlReader` compatibility renderer that was implemented at one point is not part of any official path: it conflates the object models, resource semantics, and control capabilities, and must be deleted — tests must not treat its successful rendering as a successful WinUI/Uno designer.

### Out-of-process host decision for Uno and native WinUI (2026-08-14; clarified 2026-08-15)

**Decision: Uno and native Windows App SDK project support requires an out-of-process host running
the project's actual runtime.** This supersedes the original framing of out-of-process as merely "Option B, a fallback
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
WinForms does not have this specific same-name type-identity forcing function, but its own
out-of-process boundary is also required for target-runtime and third-party designer isolation
(see [`winforms-designer.md`](winforms-designer.md#out-of-process-host-decision-2026-08-15)).

**What ships now vs. later (updated 2026-08-15):**

- **There is no cross-runtime in-process fallback.** The out-of-process Uno host is the only valid
  renderer for Uno projects, including `src/Samples/UnoXamlSample`. `ProGpuRuntimeHost` and
  `ProGpuXamlExecutor` remain valid for the separate ProGPU WinUI profile; they must never be used
  to materialize a project assembly built against Uno.WinUI or the Windows App SDK.
- Fix A's compile-time half (project assembly as a `MetadataReference`) remains valid for what it
  is - quick in-process feedback - but no longer feeds a product renderer; diagnostics for real
  projects come from the child process's own runtime parser instead.
- Roadmap item B (ProGPU.WinUI API completion) remains relevant to the ProGPU WinUI profile, but
  does not unblock Uno or Windows App SDK compatibility.
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
5. **Dependency-identity packaging (target, not current implementation).** `dotnet exec --runtimeconfig <project>.runtimeconfig.json
   --depsfile <project>.deps.json <host.dll>` makes the child's dependency graph *be* the
   project's dependency graph. The child is a thin shim; all Uno assemblies resolve from the
   project's bin, so the parent "OpenDevelop-side version" problem disappears - the child always
   runs the exact Uno the project references.

**Architecture.** Child process `UnoDesignHost` (Uno.Sdk `net10.0-desktop` console shim, no app
template): boots headless Uno (fact 4),
materializes the design surface's current XAML text via `XamlReader.Load` (fact 2), renders
offscreen via `RenderTargetBitmap` (fact 3), and answers StreamJsonRpc calls over loopback TCP.
OpenDevelop's shell
(`WinUIXamlDesignerViewContent`, toolbox, property pad, outline, DevFlow actions) keeps its
contracts; only `WinUIXamlHost`'s backing changes from in-process ProGPU to child-process RPC. The
fixture (`UnoXamlSample`) is handled by that child using the controls/resources available in the
child's bundled runtime.
The checked-in child currently runs its own Uno 6.5.31 `.runtimeconfig.json`/`.deps.json`; it does
not yet accept a project assembly or run under the opened project's dependency context described
in fact 5. That is the main remaining boundary between fixture support and version-correct real
Uno project support.

**Runtime adapters.** One shell contract; per-runtime bootloaders. Uno headless Skia is the first
out-of-process adapter and is reachable on macOS. A Windows App SDK adapter is Windows-only and
remains future work. Contrary to the earlier version of this technote, current Windows App SDK
WinUI does expose [`Microsoft.UI.Xaml.Markup.XamlReader.Load`](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.markup.xamlreader);
the adapter investigation should begin with that supported runtime parser and determine its
custom-control, compiled resource, dispatcher, and offscreen capture constraints before choosing
Roslyn compilation. ProGPU is the separate in-process adapter for ProGPU.WinUI projects. The
wire/data contracts remain runtime-neutral so another out-of-process adapter is additive.

**Protocol surface** (the implementation uses StreamJsonRpc over a fresh loopback TCP connection;
the child connects back to the parent's listener and logs over redirected stdout/stderr):

- `initialize` -> `capabilities`: runtime name/version and toolbox catalog (categories, default
  XAML template snippets, required namespaces). The catalog is generated in-child by reflecting
  the loaded runtime assemblies and applying a design-time allowlist. Today it therefore matches
  the bundled Uno version; after project-context launch is implemented it must match the project's
  actual Uno version.
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

- **M0 - Headless probe: done.** The production child now boots headless Uno, uses
  `XamlReader.Load`, and renders through `RenderTargetBitmap`.
- **M1 - Child host protocol: done.** The implemented surface includes capabilities/toolbox,
  `design/load`, `design/layout`, `app/resources`, hit testing, and shutdown/lifecycle handling.
- **M2 - OpenDevelop wiring: done for the fixture.** The parent spawns the bundled child, presents PNG
  frames, consumes the element tree, and supports selection, editing, viewport, and lifecycle
  operations through the shared shell contracts.
- **M3 - Real-project parity: in progress.** First launch the child under the opened project's
  runtimeconfig/depsfile and pass its project assembly. App.xaml and merged-resource preprocessing
  already exists; then validate DotUninstall-style custom types, converters, compiled resources, and code-behind, and
  finish line-addressable diagnostics plus a design-time unsupported-construct policy.
- **M4 - ProGPU routing.** Detect ProGPU.WinUI explicitly, route only that profile to
  `ProGpuRuntimeHost`, and add a ProGPU-targeted fixture so the Uno child cannot mask regressions.
- **M5 - Windows-only native WinUI adapter.** Prototype `XamlReader.Load` in a child running the
  project's Windows App SDK dependency context, then select the capture/materialization strategy
  from measured limitations rather than assuming Roslyn is mandatory.

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
| Renderer | Runtime-specific materialization: ProGPU compiler pipeline, Uno `XamlReader` + `RenderTargetBitmap`, or the future native WinUI adapter | XAML Studio + ProGPU packages for ProGPU; project runtime for OOP adapters |
| Runtime host | Explicitly selected ProGPU in-process surface, Uno child, or future Windows App SDK child | `IWinUIXamlRuntimeHost` and optional capability interfaces |
| OpenDevelop adapter | WPF secondary view, host lifecycle, Toolbox/Outline/Properties wiring | OpenDevelop shell + provider contracts |
| Editing operations | Insert, delete, move, resize, set property → versioned text edits | Three backends share command semantics; each generates its own edits |

Do not treat the runtime visual tree as the only document model. Every operation must ultimately produce an undoable source edit; re-parse and refresh the preview afterwards. This supports invalid intermediate text, Undo/Redo, formatting preservation, and out-of-process renderers.

Standard controls remain the minimum acceptance fixture. The implemented Uno path also imports
App.xaml and local merged dictionaries; custom controls, compiled resources, `x:Bind`, and
code-behind require real-project validation and clear diagnostics where runtime loading cannot
reproduce application startup.

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

**D. Out-of-process hosts for project-native runtimes.** See "Out-of-process host decision for
Uno and native WinUI" and "Out-of-process host scoping" above. The Uno M0-M2 implementation is
present; M3 real-project validation remains. The Windows App SDK adapter is separate Windows-only
work and should start by probing its real `XamlReader`, dispatcher, resource, and capture behavior.

**E. Explicit ProGPU WinUI profile.** Add detector evidence and runtime discrimination, make the
ProGPU and Uno factories decline foreign profiles, and add a ProGPU-targeted integration fixture.
This turns the existing ProGPU implementation into intentional WinUI-on-ProGPU support instead of
an availability-based fallback.

**Suggested order (updated 2026-08-15):** fix routing (E) first so tests exercise the intended
runtime; finish Uno real-project parity (D/M3); extend ProGPU coverage according to B; then build
the Windows-only native WinUI adapter (D/M5). A remains useful only as ProGPU compilation
introspection, and C is done.

## Design-surface improvements (2026-08-15)

Follow-on work on the out-of-process host's OpenDevelop shell, all verified live via DevFlow
and by the `WinUIDesigner_*` integration tests:

### Toolbox is populated from the runtime catalog (18 -> the loaded runtime's controls)

`WinUIXamlToolbox` was a hardcoded 18-item whitelist. The child's `initialize` catalog (built
by reflecting the loaded `Microsoft.UI.Xaml.Controls` assembly: `FrameworkElement` subclasses
with a parameterless ctor, denylisted for shell/template parts and navigation hosts) is now
wired through `IWinUIXamlToolboxCatalog` (`GetToolboxCatalog`) into the shared Toolbox pad
when the design host reports ready. The fixture now lists 140 controls. The catalog filter was
widened from `Control`/`ContentControl` to `FrameworkElement` so panels, `TextBlock`, `Border`
and `Image` are included.

### Unnamed elements are auto-named on pick

Clicking a control without an `x:Name` previously resolved to nothing (the Properties pad
stayed empty - every pick walked up to a *named* ancestor). Now:

- the child reports the innermost hit's **tree path** (`ElementNode.Path`, `HitTestResult.PickPath`)
  alongside the name chain (template parts leak names like a ScrollViewer's internal `Root`,
  so the chain alone cannot tell "unnamed" from "template name");
- the shell (`IWinUIXamlPathPick.GetPickChain`) maps the path back to the source document
  (walking up to the first element type the source actually contains), auto-assigns a unique
  `x:Name` through the editor (undoable, dirtied), and selects it - VS-style.

The pick path mapping is index-based among same-type elements in tree order; template parts of
the same type as the picked control are a known divergence risk, acceptable for now.

### Toolbox drag keeps the dragged tool selected

`WinUIXamlToolbox` reasserts the dragged item against the ListBox's internal Selector, which
keeps moving `SelectedItem` to whichever row is under the cursor while the button is held
during a drag (the hazard `WpfToolbox` documents); the tool stays selected until the drop on
the design surface completes.

### Scrollbars actually scroll the canvas

The design rect was positioned at `origin + pan + scrollOffset` inside the scroll content, so
the offset cancelled on screen - dragging the scrollbar thumb did nothing. The rect is now
anchored at a fixed content position (top-left when zoomed in, centered at fit), the
ScrollViewer moves it natively, and the scroll range covers the whole design. `ToDesignPoint`,
`DesignToSurfacePoint` and zoom-at-cursor are scroll-aware; `FitView` resets the scroll.

### Test coverage

- `WinUIDesigner_PropertiesPadEdit_UpdatesSourceAndRender` (new): a property edited through
  the shared Properties pad lands as a source edit and the re-rendered surface reflects it
  (the button widens); polls the measured bounds because `rendered` stays true across re-renders.
- `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer` (existing):
  real synthetic pointer drag from the Toolbox onto the surface, verifying the drop resolves
  into the dropped container and lands as a source edit.
- The retired-ProGPU assertions were updated to the Uno host: `runtime-stats` now reports the
  child-process lifecycle (`IWinUIXamlLifecycleProbe`), `ClosingDocument` asserts the child
  dies on close, and `RendersButton` asserts a non-zero rendered button with no diagnostics.

### Project dependency context (A1, 2026-08-15)

The child now runs inside the designed project's dependency graph - the architecture's fact 5
landed. When the owning project has build output, `UnoDesignClient` spawns the child with

```
dotnet exec --runtimeconfig <project>.runtimeconfig.json --depsfile <project>.deps.json <host.dll> --port N --appbin <project-bin>
```

so Uno and every project assembly resolve from the project's bin (the project's real Uno
version, custom controls, converters, muxc types). Two child-side pieces make this work:

- **Own-dependency resolver**: with the project's deps, `AppContext.BaseDirectory` points at
  the project bin, not the child's deployment - so the resolver hook loads the child's own
  non-project dependencies (StreamJsonRpc etc.) from `typeof(Program).Assembly.Location`'s
  directory. Registered from a helper method because `Main`'s own JIT resolves StreamJsonRpc
  before the first line runs.
- **Project-assembly preload**: XamlReader's type resolution scans the *loaded* assemblies
  (`AppDomain.GetAssemblies()`), so the child preloads the project bin's dlls
  (`--appbin`); without this, `{using:UnoPropertyGrid}PropertyGridControl` reported
  "Unable to find type". Verified: `CustomControlPage.xaml` (a page referencing the sample's
  own `pg:PropertyGridControl`, no event handlers) renders with zero diagnostics, while the
  unbuilt `UnoXamlSample` fixture falls back to the child's own deployment unchanged.

The compile baseline stays Uno.Sdk 6.5.31 (the API floor); the project's runtime is whatever
the project references (verified against 6.6.42). The two reflection points into Uno internals
(`CoreDispatcher.DispatchOverride`, `RootScale._testOverrideScale`) remain the version-risk
surface; both fail with clear fallbacks.

## ProGPU host interface parity (2026-08-16)

The `IWinUIXamlRuntimeHost` contract grew several members (gridlines, simulated display scale,
render diagnostics, PNG export, pixel sampling, child log) when the out-of-process Uno host
implemented them; the in-process ProGPU host lagged behind and no longer compiled against the
shared interface. Both runtime profiles now implement the full contract, with the ProGPU side
(`ProGpuRuntimeHost`/`ProGpuWinUIHostControl`) mirroring the Uno host's semantics:

- **`RenderSample()`** samples the last frame's BGRA staging bytes at the same fixed points as
  the Uno host (center, top-left, mid-left) and returns `WxH center=#RRGGBB topleft=#RRGGBB
  midleft=#RRGGBB` — so a DevFlow pixel check reads identically for both runtimes.
- **`ExportPng(path)`** encodes the last frame from the staging bytes via
  `PngBitmapEncoder` (`Wrote <path> (WxH)`, or `Nothing to export (no design loaded)` /
  `Export failed: ...`), instead of relying on the child process the Uno host uses.
- **`RenderTiming()`** times the compositor pass with a `Stopwatch` around
  `Compositor.RenderOffscreen` + GPU readback and reports `(RenderMs, Width, Height, Dpi,
  CompressedBytes, RawBytes)`. In-process there is no wire compression, so compressed == raw.
- **`EffectiveDisplayDpi` / `SetSimulatedDpi()`** — the render loop now renders at the
  *effective* scale instead of the raw WPF DPI: the simulated override wins, then the
  `UNO_DESIGN_DPI` environment override (the Uno host's existing test hook, so the two runtimes
  share it), then the real `VisualTreeHelper.GetDpi` reading. The change is observable in
  `compositor-metrics` (`dpi=` field) and re-renders on the next composition tick, exercising
  the same DPI-aware render path a real monitor move would.
- **`Gridlines` / `SetGridlines()`** — a design-space gridlines overlay drawn in the host
  control's `OnRender` (24 px pitch, semi-transparent), matching the Uno surface's overlay.
- **`ChildLog`** returns `"(in-process host)"` since this runtime owns no child process.

The DevFlow actions that consume these members — `od.winui-designer.gridlines`,
`od.winui-designer.debug-dpi`, `od.winui-designer.render-timing`, `od.winui-designer.export-png`,
`od.winui-designer.render-sample`, and `od.winui-designer.child-log` — therefore behave
identically against both runtime profiles.
