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
| WPF Designer | `src/AddIns/DisplayBindings/WpfDesign/` and `externals/vscode-wpf/external/WpfDesigner/` | Added to the main solution, uses `LibreWPF.Sdk`; it is the official WPF backend. See [`wpf-designer.md`](wpf-designer.md) |
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | The C# backend has moved to a CodeDOM-free Roslyn `BasicDesignerLoader`; `.Designer.cs` round-trip, legacy format migration, shared Toolbox Pad, and real drag-drop tests are all complete; the VB Roslyn backend is not yet done. See [`winforms-designer.md`](winforms-designer.md) |
| XAML language server | `externals/vscode-wpf/` | The WPF language server for `.xaml` is wired up; framework detection cannot rely on file extension alone |
| WinUI/Uno Designer | `src/AddIns/DisplayBindings/WinUIXamlDesigner/` | New AddIn and unified routing established; the early WPF `XamlReader` compatibility preview was judged a wrong approach and reverted; wiring up the original XAML Studio renderer and the Uno WPF host. See [`winui-designer.md`](winui-designer.md) |
| XAML Studio ProGPU port | `src/AddIns/DisplayBindings/WinUIXamlDesigner/XamlStudio.Toolkit.ProGPU/` | Standalone ProGPU WinUI runtime assembly; directly links the submodule's renderer models/preprocessing, adapts the instantiation boundary to the ProGPU XAML compiler, and does not depend on the Uno runtime |
| ProGPU-in-WPF host | `src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.ProGPUHost/` | Standalone WPF control implemented: offscreen WebGPU render, DPI/resize, BGRA presentation, mouse/wheel/text/focus forwarding, and deterministic disposal; `ProGpuXamlExecutor` now drives the ProGPU XAML compiler and the collectible preview-assembly session |
| ProGPU XAML packages | `librewpf/artifacts/local-feed` | `ProGPU.Xaml`, `ProGPU.Xaml.Roslyn`, `ProGPU.Xaml.Workspaces`, and `ProGPU.WinUI.Designer` are packed locally from the same preview.47 upstream commit that produced the published feed |

### Actual State of WinForms Round-Trip and Toolbox

Moved to [`winforms-designer.md`](winforms-designer.md) (the WinForms designer's dedicated technote).

### Reuse Boundary with UnoDevelop and XAML Studio

Moved to [`winui-designer.md`](winui-designer.md) (the WinUI/Uno designer's dedicated technote).

## External References and Dependencies

Moved to [`winui-designer.md`](winui-designer.md).

## WinUI/Uno Host Decision

Moved to [`winui-designer.md`](winui-designer.md), including the Designer Chrome Decision and
the current real-world preview problem catalog (2026-08-14) with its fix roadmap.

### Designer Chrome Decision

Moved to [`winui-designer.md`](winui-designer.md).

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

Moved to [`winui-designer.md`](winui-designer.md).

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
- A host crash or renderer timeout does not exit OpenDevelop (required for WinUI's out-of-process
  host — see [`winui-designer.md`](winui-designer.md#out-of-process-host-decision-2026-08-14) —
  and a lower-priority hardening option for WinForms/WPF's in-process hosts);
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
