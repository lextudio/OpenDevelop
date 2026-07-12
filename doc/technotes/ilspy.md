# ILSpy AddIn Port

## Layout goal

OpenDevelop should expose an `ILSpy` workbench layout alongside `Default`, `Debug`, and
`Plain`. Selecting it should switch the IDE into a decompiler-oriented workspace by hosting the
real WPF ILSpy panes:

- left: ILSpy `AssemblyTreeModel` + `AssemblyListPane`
- center: ILSpy decompiler document panes, or an adapter that routes decompiled text into the
  existing AvalonEdit document area
- right/bottom: ILSpy `SearchPaneModel`, `AnalyzerTreeViewModel`, metadata panes, and any future
  exported `ToolPaneModel`

## Open assembly flow

The primary embedded ILSpy entry point should be `File > Open > Assembly`, but it must call into
ILSpy's own assembly tree model rather than an OpenDevelop-specific tree.

1. The command opens a native file picker filtered to `*.dll` and `*.exe`.
2. The selected file is passed to ILSpy's `AssemblyTreeModel`/`AssemblyList.Open(...)` path.
3. ILSpy builds its normal assembly tree nodes, resources, metadata nodes, package nodes, and
   analyzer context.
4. Selecting or double-clicking nodes should use ILSpy's existing navigation/decompilation
   commands.
5. Decompiler output can either stay in ILSpy `TabPageModel` documents hosted by OpenDevelop's
   AvalonDock surface, or be bridged into the existing `ilspy://`/AvalonEdit display binding.

This keeps ILSpy embedded in OpenDevelop instead of launching `ILSpy.exe`, while reusing ILSpy's
WPF panes and decompiler workflow instead of reimplementing them.

## Current layout constraint

`AvalonDockLayout.LoadLayout` does not currently restore the legacy SharpDevelop
`data/layouts/*.xml` files. The modern serializer path only works for MEF `ToolPaneModel` panes,
while most old pads still use AddInTree `Pad` descriptors.

This is actually the right direction for ILSpy: OpenDevelop already has a MEF-backed
`ToolPaneModel` path, while ILSpy exports its panes with `[ExportToolPane]`. The missing work is
composition and type compatibility, not custom UI.

Required infrastructure:

1. Make `externals/ilspy/ILSpy` consumable as a library or create a thin hostable facade project.
   Today it is a `WinExe` with app startup, single-instance handling, main window ownership, and
   WPF resources tied to the standalone app.
2. Teach `OpenDevelopMefHost` to compose parts from selected external assemblies, not only
   `Assembly.GetExecutingAssembly()`.
3. Bridge ILSpy's `System.Composition`/TomsToolbox export provider expectations with
   OpenDevelop's current `Microsoft.VisualStudio.Composition` host, or host ILSpy in its own
   child composition container and expose selected pane models to OpenDevelop.
4. Unify or adapt the pane base types. OpenDevelop has
   `ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel`; ILSpy has
   `ICSharpCode.ILSpy.ViewModels.ToolPaneModel`. They are conceptually equivalent but not the
   same CLR type.
5. Register ILSpy resource dictionaries, templates, images, command bindings, and services without
   replacing OpenDevelop's `Application.Current` or `MainWindow`.
6. Verify AvalonDock assembly/package compatibility. Both projects use AvalonDock APIs, but direct
   hosting only works if the runtime assembly identity and layout model types are compatible.

## AvalonDock 5 unification (blocking prerequisite)

Survey (2026-07-12) found two independent AvalonDock forks in the tree, which blocks any direct
pane hosting regardless of composition-layer work:

- ILSpy (`OpenDevelop/externals/ilspy`) references the NuGet package
  `Dirkster.AvalonDock.Themes.VS2013` 4.72.1 (`ILSpy.csproj:43`,
  `Directory.Packages.props:17`) — a community (Dirkster) fork, pulled in transitively (no
  direct core `AvalonDock` package reference).
- OpenDevelop vendors its own fork as a submodule at `OpenDevelop/src/Libraries/AvalonDock`
  (`lextudio/AvalonDock`, currently `v4.74.1-184-g15b60ee`, no upstream v5 tag exists anywhere).

Two different AvalonDock assembly identities cannot host each other's panes/layout models, so this
must be resolved before any pane-composition bridging:

1. Rebase/merge the latest upstream Dirkster/AvalonDock changes into the `lextudio/AvalonDock`
   fork submodule, reconciling with the 184 local commits already ahead of `v4.74.1`.
2. Re-point `externals/ilspy`'s AvalonDock dependency from the Dirkster NuGet package to a
   project reference against `OpenDevelop/src/Libraries/AvalonDock`, so both projects share one
   AvalonDock assembly identity.
3. Tag the unified fork `5.0.0` as the new baseline version.

Only after this lands does the pane/composition bridging below become buildable.

## Composition-layer facts (corrects earlier assumptions in this note)

- ILSpy does **not** use System.Composition/MEF2. It uses **TomsToolbox composition** layered over
  `Microsoft.Extensions.DependencyInjection` (`TomsToolboxVersion=2.24.0`,
  `Directory.Packages.props:9`).
  - `ExportToolPaneAttribute`: `ILSpy/Commands/ExportCommandAttribute.cs:105-111` (extends
    `ExportAttribute`, contract name `"ToolPane"`, base type `ViewModels.ToolPaneModel`).
  - `ToolPaneModel`: `ILSpy/ViewModels/ToolPaneModel.cs:21-45`, namespace
    `ICSharpCode.ILSpy.ViewModels`, extends `PaneModel` → `ObservableObjectBase`. Already has an
    `#if CROSS_PLATFORM` branch extending `Dock.Model.TomsToolbox.Controls.Tool` — upstream ILSpy
    has already started a cross-platform dock abstraction we should reuse rather than duplicate.
  - Host/adapter: `App.xaml.cs` — static `IExportProvider ExportProvider`, built from a DI
    `ServiceProvider` wrapped by `ExportProviderAdapter`.
  - Concrete panes: `AssemblyTreeModel.cs`, `Search/SearchPaneModel.cs`,
    `Analyzers/AnalyzerTreeViewModel.cs`, `ViewModels/DebugStepsPaneModel.cs`.
- OpenDevelop uses `Microsoft.VisualStudio.Composition` (a different MEF implementation), see
  `OpenDevelopMefHost.cs`, and its own `ToolPaneModel` in `ICSharpCode.SharpDevelop.ViewModels`
  (`SharpDevelop/ViewModels/ToolPaneModel.cs`) — a distinct CLR type from ILSpy's.

Decision: ILSpy's pane/tool model (TomsToolbox `[ExportToolPane]` + `ToolPaneModel` +
`PaneModel`/`ObservableObjectBase`) is the standard going forward. OpenDevelop's
`ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel` and its `Microsoft.VisualStudio.Composition`
host are the side that adapts, not the other way around. Concretely this means:

- OpenDevelop's `OpenDevelopMefHost` needs to grow (or be replaced by) a TomsToolbox composition
  container so both ILSpy's and OpenDevelop's own tool panes are exported/resolved the same way.
- OpenDevelop's existing `ToolPaneModel`-derived panes should eventually be migrated onto ILSpy's
  `ToolPaneModel`/`PaneModel` base rather than kept as a parallel type hierarchy.
- The already-existing `#if CROSS_PLATFORM` dock abstraction in ILSpy's `ToolPaneModel` should be
  investigated as the actual seam Uno-hosting could reuse later, instead of inventing a new one.

Legacy add-in to remove once the above lands: `OpenDevelop/src/AddIns/DisplayBindings/ILSpyAddIn/`
(external-process `ILSpy.exe` launcher integration — `ILSpyDisplayBinding.cs`,
`ILSpyDecompilerService.cs`, `LaunchILSpy/*`, etc.).

## Next steps after MVP

1. Remove all emulated ILSpy pads from the addin. They are not the desired architecture.
2. Add a hostable ILSpy facade, preferably upstream-friendly:
   - `ILSpy.Host` or `ILSpy.Controls`
   - exports `AssemblyTreeModel`, `AssemblyListPane`, `SearchPaneModel`, `AnalyzerTreeViewModel`,
     metadata views, and decompiler document services
   - excludes `App`, `MainWindow`, single-instance, update UI, and standalone menu/toolbar startup
3. Add an OpenDevelop bridge:
   - maps ILSpy `ToolPaneModel` instances to OpenDevelop dock panes
   - maps ILSpy document/tab output either to OpenDevelop documents or to hosted ILSpy tab models
   - maps File/Open commands to ILSpy `AssemblyTreeModel`
4. Re-enable modern AvalonDock serializer restore for layouts that contain only MEF pane models,
   then provide a real `data/layouts/ILSpy.xml`.
