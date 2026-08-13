# XAML Services and Unified Designer Roadmap

This document records OpenDevelop's XAML language services and the current state, target architecture, and implementation order of the three designer backends: WinForms, WPF, and WinUI. UnoDevelop's XAML Designer is an important reference implementation for the WinUI route, but it is not functionality OpenDevelop currently owns.

## End Goal

OpenDevelop should select the correct designer for the same editor workflow based on the UI framework of the project and file:

| Project Type | Design Files | Designer Backend | Target Capabilities |
|---|---|---|---|
| WinForms | `.cs`/`.vb` + `.Designer.*` + `.resx` | Existing `FormsDesigner` | Load, selection, property editing, Toolbox, code round-trip |
| WPF | `.xaml` | Existing `WpfDesign` | XAML DOM, design surface, selection/Adorner, properties, Toolbox, source synchronization |
| WinUI 3 / Uno Platform | `.xaml` | New WinUI/Uno backend | Live preview, selection, properties, Toolbox, source synchronization; handle dialect/runtime differences per project profile |

"Supporting all three simultaneously" here means the three framework backends share IDE-level contracts and user experience — not forcing the three object models into a single control hierarchy. `System.Windows.Forms.Control`, `System.Windows.DependencyObject`, and `Microsoft.UI.Xaml.DependencyObject` must remain isolated.

## Current Baseline

### OpenDevelop

| Component | Location | Current Status |
|---|---|---|
| WPF Designer | `src/AddIns/DisplayBindings/WpfDesign/` and `externals/vscode-wpf/external/WpfDesigner/` | Added to the main solution, uses `LibreWPF.Sdk`; it is the official WPF backend |
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | The C# backend has moved to a CodeDOM-free Roslyn `BasicDesignerLoader`; `.Designer.cs` round-trip, legacy format migration, shared Toolbox Pad, and real drag-drop tests are all complete; the VB Roslyn backend is not yet done |
| XAML language server | `externals/vscode-wpf/` | The WPF language server for `.xaml` is wired up; framework detection cannot rely on file extension alone |
| WinUI/Uno Designer | `src/AddIns/DisplayBindings/WinUIXamlDesigner/` | New AddIn and unified routing established; the early WPF `XamlReader` compatibility preview was judged a wrong approach and reverted; wiring up the original XAML Studio renderer and the Uno WPF host |
| XAML Studio ProGPU port | `src/AddIns/DisplayBindings/WinUIXamlDesigner/XamlStudio.Toolkit.ProGPU/` | Standalone ProGPU WinUI runtime assembly; directly links the submodule's renderer models/preprocessing, adapts the instantiation boundary to the ProGPU XAML compiler, and does not depend on the Uno runtime |
| ProGPU-in-WPF host | `src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.ProGPUHost/` | Standalone WPF control implemented: offscreen WebGPU render, DPI/resize, BGRA presentation, mouse/wheel/text/focus forwarding, and deterministic disposal; `ProGpuXamlExecutor` now drives the ProGPU XAML compiler and the collectible preview-assembly session |
| ProGPU XAML packages | `librewpf/artifacts/local-feed` | `ProGPU.Xaml`, `ProGPU.Xaml.Roslyn`, `ProGPU.Xaml.Workspaces`, and `ProGPU.WinUI.Designer` are packed locally from the same preview.47 upstream commit that produced the published feed |

### Actual State of WinForms Round-Trip and Toolbox

The earlier claim that this was "not yet restored" came from an outdated exclusion comment in `FormsDesigner.csproj`, not from the current implementation. The actual pipeline is:

- `CSharpBinding.FormsDesigner.RoslynFormsDesignerSecondaryDisplayBinding` uses Roslyn to decide whether a C# partial class is designable;
- `RoslynDesignerLoader` reads the main file and `.Designer.cs`, converts a supported subset of `InitializeComponent` into a CodeDOM object graph, and rewrites methods and added fields on save;
- `FormsDesignerViewContent.ToolsContent` exposes the shared `WpfToolbox`; the latter shows WinForms categories and creates controls through a real `System.Drawing.Design.IToolboxService` and a WPF/WinForms drag bridge;
- `DragToolboxItem_OntoWinFormsDesignSurface_AddsControlToForm` verifies end-to-end drag-drop, visible sizing, persistence into `.Designer.cs`, and tool-selection reset.

This audit also added startup preloading for the FormsDesigner DevFlow actions, so that lazy AddIn loading does not lag behind DevFlow's one-shot action discovery and cause test 404s.

The old implementation was a "Roslyn parser + CodeDOM serializer bridge"; it has been replaced. OpenDevelop's goal goes further than Microsoft 17.5's "Roslyn code generator": the active WinForms backend no longer treats CodeDOM as an intermediate model. The new `RoslynFormsDesignerLoader` derives directly from `BasicDesignerLoader`, not `CodeDomDesignerLoader`; on the read side it projects the project `Document`'s syntax/semantic models into a component graph, and on the save side it generates a C# syntax tree from the component graph. The `this.` prefixes, fully qualified types, and explicit delegates produced by the old CodeDOM generator must be accepted as compatible input, but the first designer save migrates to the Roslyn style; it will not fall back to CodeDOM serialization for compatibility with old files.

The implementation uses the project `Document`/`Workspace`, compilation, Simplifier, Formatter, and AnalyzerConfigOptions, and replaces only annotated fields and `InitializeComponent`. Resource reading/writing was also extracted from `ProjectResourcesComponentCodeDomSerializer` / `ProjectResourcesMemberCodeDomSerializer` into a syntax-tree-independent `RoslynDesignerResourceModel`, which the Roslyn backend uses to handle `ComponentResourceManager.ApplyResources`. Still pending are wiring the full project Workspace / `.editorconfig`, the VB backend, and the async/parallel, `nameof`, and high-DPI work from Microsoft's newer generator.

The core backend does not use `System.CodeDom` as its document model; the integration test also asserts that the runtime loader is not a `CodeDomDesignerLoader`. The old loader is not a runtime fallback. For compatibility with the third-party WinForms control ecosystem, explicitly declared custom `CodeDomSerializer`s are allowed to run inside a `LegacyCodeDomSerializerAdapter` boundary; their short-lived output is immediately converted into Roslyn statements and discarded, with the final write-back still done by the Roslyn formatter / project document. Return shapes that cannot be converted block saving and report the serializer/control type — properties are never silently dropped.

### Reuse Boundary with UnoDevelop and XAML Studio

UnoDevelop's `src/AddIns/DisplayBindings/XamlDesigner/` has implemented native `Microsoft.UI.Xaml` Source/Design secondary views, a Toolbox provider, an Outline provider, Properties Pad wiring, and integration tests. OpenDevelop reuses that IDE wiring approach but does not re-implement the renderer: the original `XamlRenderService` in `externals/xamlstudio/XamlStudio.Toolkit/Services/XamlRenderService/` and its models/extensions are the upstream code, consumed through linked source or a standalone toolkit project, with only the narrow adaptations required to compile. Its algorithms must not be rewritten as a WPF XAML parser, nor maintained as a behavior-forked copy.

UnoDevelop/XAML Studio's UI files cannot directly become OpenDevelop WPF visuals: the former's control types are `Microsoft.UI.Xaml.*`, while the latter's shell and document views are `System.Windows.*`. The two visual trees must be isolated by an explicit host, similar to how the WinForms designer embeds WPF through `WindowsFormsHost` rather than loading WinForms controls as WPF controls.

## External References and Dependencies

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

## WinUI/Uno Host Decision

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

## Target Architecture

```text
                         OpenDevelop Workbench
                                  │
              ┌───────────────────┼───────────────────┐
              │                   │                   │
       Designer registry     Shared Toolbox      Properties/Outline
       + project detector    and commands        host contracts
              │
       ┌──────┴──────┬───────────────┐
       │             │               │
 WinForms backend  WPF backend   WinUI backend
 FormsDesigner     WpfDesign     WPF host adapter or preview RPC
       │             │               │
 WinForms object   WPF XamlDom    XAML Studio renderer
 model/services    and designer   + isolated WinUI/Uno runtime
```

The framework-neutral contracts should be extracted from the provider patterns UnoDevelop has already validated and placed in the shell/base layer:

- `IDesignerProvider`: CanDesign, creating the secondary view, lifecycle, and saving;
- `IDesignerToolboxProvider`: categories, tool items, and framework-specific insertion payloads;
- `IDesignerSelectionService`: the current selection and selection changes;
- `IDesignerPropertyAdapter`: exposing backend objects to the unified Properties Pad;
- `IDesignerOutlineProvider`: the element/control tree and source locations;
- `IDesignerDocumentSynchronizer`: bidirectional synchronization of text versions, parse results, selection, and mutations.

Concrete types from the three UI frameworks must not appear in the contracts; use opaque handles, descriptors, and text edits. When existing contracts such as `IToolboxProvider` and `IOutlineContentHost` can satisfy a need, extend or adapt them instead of creating parallel synonymous interfaces.

## Framework Detection and Routing

`.xaml` can equally be WPF, WinUI, or Uno, so the designer/LSP cannot be chosen by extension. The routing order should be:

1. Read the owning project's SDK, TFM, PackageReferences, and XAML item metadata;
2. Detect Uno first (Uno projects also contain `Microsoft.UI.Xaml` and would otherwise be misclassified as WinUI);
3. Then detect WinUI/Windows App SDK, then WPF;
4. For loose XAML with no project context, let the user choose the profile, or open source-only without offering a wrong designer;
5. The designer and the language server must consume the same detection result and must not each re-guess independently.

The suggested unified result is `XamlFrameworkKind` (Wpf, WinUI, Uno, Unknown) plus an evidence-backed `XamlFrameworkContext`. OpenDevelop's new backend commits to both WinUI 3 and Uno Platform projects from the first phase: they share the `Microsoft.UI.Xaml` object model, presentation namespace, and most controls, but must keep separate profiles. The Uno profile has higher detection priority than WinUI and is responsible for the Uno SDK, target platforms, `Uno.WinUI` versions, and Uno-specific resource/custom-control resolution; Uno projects must not be treated as ordinary WinUI projects and loaded by luck after misdetection.

## WinUI Backend Layering

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

## Phased Implementation

| Phase | Content | Completion Criteria | Status |
|---|---|---|---|
| 0 | Fix the docs and inventory the three backends | Clear OpenDevelop/UnoDevelop boundaries and known gaps | done |
| 1 | WinUI/Uno host spike | Both the WPF compatibility renderer and Uno runtime routes are abandoned; the ProGPU-in-WPF host renders standard controls through the XAML Studio + ProGPU pipeline. Verified end-to-end against `src/Samples/UnoXamlSample/MainPage.xaml` on macOS: detected as Uno, compiled by ProGPU, materialized into a WinUI `FrameworkElement`, rendered offscreen, and presented into the WPF document tab | done |
| 2 | Unified framework detection | `XamlFrameworkDetector` routes in Uno→WinUI→WPF order; unit tests for WPF/WinUI/Uno/Unknown added; LSP consuming the same result still pending | partial |
| 3 | Extract common provider/selection/sync contracts | WPF and WinForms backends wire in through adapters with no feature regression | todo |
| 4 | WinUI/Uno read-only MVP | AddIn/Source/Design routing, ProGPU materialization, diagnostics, last-good preview retention, Outline, and Source-to-Design refresh are all in place and covered by integration tests | done |
| 5 | WinUI/Uno basic editing | Toolbox insertion, selection, Properties changes, deletion and Undo/Redo all land as source edits, driven through the shell's shared Toolbox and Properties pads. Selection works from the Outline *and* by clicking the rendered surface, and Toolbox insertion works via a real synthetic mouse drag that resolves the drop point to the container under the cursor. Covered by `WinUIDesigner_ToolboxInsertSelectEditDeleteUndoRedo_AllLandAsSourceEdits`, `WinUIDesigner_ClickOnDesignSurface_SelectsSourceElementInPropertiesPad` and `WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer` | done |
| 6 | Complete all three designers | WinForms VB backend with modern Roslyn codegen; consistent base experience for WPF/WinUI/Uno | todo |
| 7 | Advanced WinUI/Uno | Project resources, custom controls, and isolated loading for both profiles; evaluate `x:Bind`/code-behind | backlog |

Phase 3 must not block the Phase 1 spike; but the production WinUI addin must not directly copy the UnoDevelop view without framework detection and document-synchronization contracts.

## Test Matrix

Each backend must cover at least:

- The right project opens the right designer, and the wrong framework never takes over;
- Valid, invalid, and invalid→valid recovery documents;
- Source/Design switching and unsaved modifications of the same document;
- Toolbox, selection, Properties, Outline, and source-location coordination;
- Undo/Redo, save, close, reopen, and resource disposal;
- Plain `.cs`/`.xml` files without a designer show no leftover providers;
- A host crash or renderer timeout does not exit OpenDevelop (out-of-process option);
- Smoke tests on Windows and on each non-Windows platform ProGPU claims to support.

WinUI integration tests may reuse the intent of the UnoDevelop fixture, but the tests must run OpenDevelop's own app and backends; passing UnoDevelop's tests cannot substitute for OpenDevelop acceptance.

## Definition of Done

"OpenDevelop supports WinForms/WPF/WinUI designers" can only be declared when all of the following hold:

- Three real project types are reliably identified and open the correct design surfaces;
- Each type has at least preview, selection, Properties, Toolbox insertion, source synchronization, and Undo/Redo;
- Unsupported XAML produces diagnostics instead of crashing the IDE;
- Framework-specific types do not leak into the common shell contracts;
- Automated tests cover routing, editing round-trips, lifecycle, and target platforms;
- The docs record the actual ProGPU/LibreWPF/WinUI versions in use and the features still unsupported.
