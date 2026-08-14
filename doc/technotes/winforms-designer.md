# WinForms Designer

This technote is the dedicated home for the WinForms designer (`FormsDesigner`): current state,
the Roslyn `BasicDesignerLoader` architecture, the round-trip pipeline, and known gaps. The
cross-designer roadmap (WinForms + WPF + WinUI together), framework detection, provider
contracts, phases, and the test matrix live in [`xaml-services.md`](xaml-services.md).

Current status: the C# backend is complete (CodeDOM-free Roslyn loader, `.Designer.cs`
round-trip, legacy migration, shared Toolbox Pad, real drag-drop tests); the VB Roslyn backend
is still pending (Phase 6 of `xaml-services.md`).

## Current Baseline

| Component | Location | Current Status |
|---|---|---|
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | The C# backend has moved to a CodeDOM-free Roslyn `BasicDesignerLoader`; `.Designer.cs` round-trip, legacy format migration, shared Toolbox Pad, and real drag-drop tests are all complete; the VB Roslyn backend is not yet done |

## Actual State of WinForms Round-Trip and Toolbox

The earlier claim that this was "not yet restored" came from an outdated exclusion comment in `FormsDesigner.csproj`, not from the current implementation. The actual pipeline is:

- `CSharpBinding.FormsDesigner.RoslynFormsDesignerSecondaryDisplayBinding` uses Roslyn to decide whether a C# partial class is designable;
- `RoslynDesignerLoader` reads the main file and `.Designer.cs`, converts a supported subset of `InitializeComponent` into a CodeDOM object graph, and rewrites methods and added fields on save;
- `FormsDesignerViewContent.ToolsContent` exposes the shared `WpfToolbox`; the latter shows WinForms categories and creates controls through a real `System.Drawing.Design.IToolboxService` and a WPF/WinForms drag bridge;
- `DragToolboxItem_OntoWinFormsDesignSurface_AddsControlToForm` verifies end-to-end drag-drop, visible sizing, persistence into `.Designer.cs`, and tool-selection reset.

This audit also added startup preloading for the FormsDesigner DevFlow actions, so that lazy AddIn loading does not lag behind DevFlow's one-shot action discovery and cause test 404s.

## Roslyn `BasicDesignerLoader` Architecture

The old implementation was a "Roslyn parser + CodeDOM serializer bridge"; it has been replaced. OpenDevelop's goal goes further than Microsoft 17.5's "Roslyn code generator": the active WinForms backend no longer treats CodeDOM as an intermediate model. The new `RoslynFormsDesignerLoader` derives directly from `BasicDesignerLoader`, not `CodeDomDesignerLoader`; on the read side it projects the project `Document`'s syntax/semantic models into a component graph, and on the save side it generates a C# syntax tree from the component graph. The `this.` prefixes, fully qualified types, and explicit delegates produced by the old CodeDOM generator must be accepted as compatible input, but the first designer save migrates to the Roslyn style; it will not fall back to CodeDOM serialization for compatibility with old files.

The implementation uses the project `Document`/`Workspace`, compilation, Simplifier, Formatter, and AnalyzerConfigOptions, and replaces only annotated fields and `InitializeComponent`. Resource reading/writing was also extracted from `ProjectResourcesComponentCodeDomSerializer` / `ProjectResourcesMemberCodeDomSerializer` into a syntax-tree-independent `RoslynDesignerResourceModel`, which the Roslyn backend uses to handle `ComponentResourceManager.ApplyResources`. Still pending are wiring the full project Workspace / `.editorconfig`, the VB backend, and the async/parallel, `nameof`, and high-DPI work from Microsoft's newer generator.

The core backend does not use `System.CodeDom` as its document model; the integration test also asserts that the runtime loader is not a `CodeDomDesignerLoader`. The old loader is not a runtime fallback. For compatibility with the third-party WinForms control ecosystem, explicitly declared custom `CodeDomSerializer`s are allowed to run inside a `LegacyCodeDomSerializerAdapter` boundary; their short-lived output is immediately converted into Roslyn statements and discarded, with the final write-back still done by the Roslyn formatter / project document. Return shapes that cannot be converted block saving and report the serializer/control type — properties are never silently dropped.

## Known Gaps

- VB Roslyn backend (Phase 6).
- Full project `Workspace` / `.editorconfig` wiring for the Roslyn loader.
- Async/parallel generation, `nameof`, and high-DPI work from Microsoft's newer generator.

## Out-of-process hosting (lower priority, 2026-08-14)

[`winui-designer.md`](winui-designer.md#out-of-process-host-decision-2026-08-14) made
out-of-process hosting the *required* architecture for real-project WinUI/Uno support, because
`ProGPU.WinUI` is a from-scratch reimplementation of `Microsoft.UI.Xaml` whose types cannot
coexist in one Roslyn compilation with the real Uno.WinUI SDK's types of the same name.

The WinForms designer does not have that forcing function: `DesignerViewContent`/`WpfToolbox`
already host the *real* `System.Drawing`/`System.Windows.Forms` in-process via `WindowsFormsHost`
and LibreWinForms (a compat shim over the real types, not a competing reimplementation like
ProGPU.WinUI), so there is no type-identity ceiling analogous to WinUI's. Microsoft's own
out-of-process WinForms designer
([devblogs post](https://devblogs.microsoft.com/dotnet/custom-controls-for-winforms-out-of-process-designer/))
solves a different problem for VS: crash/hang isolation from third-party control assemblies
loaded into the designer process, and .NET Core/Framework side-by-side hosting. Those benefits
are real but not structural here - a bad custom control can still take down OpenDevelop's own
process today, same as before this note - and are lower priority than closing WinUI's forced gap.
Revisit once the WinUI out-of-process host exists; much of its RPC/surface-capture plumbing would
be directly reusable for a WinForms designer host process.
