# ILSpy AddIn Port

## Layout goal

OpenDevelop should expose an `ILSpy` workbench layout alongside `Default`, `Debug`, and
`Plain`. Selecting it should switch the IDE into a decompiler-oriented workspace by hosting the
real WPF ILSpy panes:

- left: ILSpy `AssemblyTreeModel` + `AssemblyListPane`
- center: decompiled output as an OpenDevelop document tab (a read-only, virtual file - see
  `DecompiledCodeViewContent`), not a dedicated pad
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

Decision: ILSpy's pane/tool model (`ToolPaneModel` + `PaneModel`/`ObservableObjectBase`) is the
design baseline going forward, but the common host-neutral model is owned by the OpenDevelop shell.
OpenDevelop's existing pane hierarchy adapts first; ILSpy pane exports then adapt to the same
contract. Concretely this means:

- Both composition containers initially feed a small OpenDevelop-owned pane/provider registration
  API. Replacing the application container is not a docking prerequisite.
- OpenDevelop's existing `ToolPaneModel` panes and ILSpy panes should eventually share one
  host-neutral `ToolPaneModel`/`PaneModel` hierarchy rather than remain parallel CLR types.
- The already-existing `#if CROSS_PLATFORM` dock abstraction in ILSpy's `ToolPaneModel` should be
  investigated as the actual seam Uno-hosting could reuse later, instead of inventing a new one.

Legacy pieces to remove once the in-process host covers them: the external-process launcher files
inside `OpenDevelop/src/AddIns/DisplayBindings/ILSpyAddIn/` (`LaunchILSpy/*` and related launch
commands). The AddIn itself remains the owner of the embedded decompiler feature.

## Earlier next-step sketch (superseded by the 2026-08 plan below)

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

## Current host implementation status (2026-08-02)

### What exists today (the MVP bridge)

`src/AddIns/DisplayBindings/ILSpyAddIn/` hosts real ILSpy panes in-process instead of launching
`ILSpy.exe`:

- `IlSpyWorkspaceHost.EnsureInitialized()` (glue, not linked from the ILSpy submodule):
  - `App.Initialize()` (linked ILSpy composition startup), `ExportProviderLocator.Register(...)`
    (ILSpy panes' visual tree ancestors are OpenDevelop's window, which never sets that attached
    property), dynamic data templates merged into `Application.Current.Resources`
    (`DataTemplateManager.CreateDynamicDataTemplates`), two hand-registered
    `BracketHighlight*` `ResourceKeys`.
  - Wraps each ILSpy pane model (`AssemblyTreeModel`, `SearchPaneModel`, `AnalyzerTreeViewModel`)
    in an `IlSpyToolPaneAdapter : ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel` (Title /
    ContentId / IsVisible / IsActive synced both ways) and registers them via
    `DockWorkspaceExtensibility.AddToolPane` -> `DockWorkspace.ToolPanes` (AvalonDock
    `AnchorablesSource`).
  - Decompiled output opens as a regular OpenDevelop document tab
    (`DecompiledCodeViewContent`, a read-only virtual file hosting a real ILSpy
    `DecompilerTextView`) - NOT a pad. ILSpy's own tab system is not used;
    `AssemblyTreeModel.TreeView_SelectionChanged` is fed a dummy `TabPageModel` so it doesn't NRE.
- DevFlow actions for the integration tests: `od.ilspy.open-assembly`, `od.ilspy.status`,
  `od.ilspy.show-pane`.

### Verified behavior (headless DevFlow probes, 2026-08-02)

- The four ILSpy surfaces render as REAL dock UI: "Assemblies"/"Search"/"Analyze" tool tabs and
  the "Decompiled Code" document tab are visible, walkable `TextBlock`s in `/api/v1/ui/tree`.
- The pane CONTENT AREA never renders: the pane views (`AssemblyListPane`, `AnalyzerTreeView`)
  are instantiated (the dynamic data template resolves) but end up as empty containers
  (`VisualTreeHelper` child count = 0, even when arranged to real size, e.g. 248x457) - the tree
  nodes are never walkable. This is why a UI-tree assertion on the assembly node is impossible
  today.
- Gotcha: a `DebugTestApp` TextBlock seen in early UI-tree probes was the Start Page's
  "recent projects" list entry (persisted memento), NOT the ILSpy tree - a false positive that
  silently "passed" the original node assertion in isolation.

### Root cause: linked theme resource names

The ILSpyAddIn.csproj links ILSpy XAML with `Link="ILSpy\Themes\..."`, which compiles the BAML to
`ilspy/themes/generic.baml` etc. (verified by listing `ILSpyAddIn.g.resources`). But:

- WPF's default-style lookup for a control type looks for `/themes/generic.xaml` in the control's
  OWN assembly - the name must be `themes/generic.xaml` (no prefix).
- `ThemeManager.Current.Theme = ...` loads themes via
  `new Uri($"/themes/Theme.{name}.xaml", UriKind.Relative)` - also resolved at the assembly root.

Both lookups MISS the prefixed resources, so every ILSpy control (SharpTreeView,
SharpTreeViewItem, ...) finds no default style/template and renders as an empty control - exactly
the observed empty panes. The theme dictionaries were also never loaded (no `ThemeManager`
initialization), and `SharpTreeNode.SetImagesProvider(...)` (tree icons) was never called.

Applied (but see the duplicate-type decision below, which may change part of this):
- csproj: theme `Page` items now link as `Themes\...` (assembly root) so `/themes/generic.xaml`
  and `/themes/Theme.*.xaml` resolve.
- `IlSpyWorkspaceHost`: mirrors ILSpy's App startup - `SharpTreeNode.SetImagesProvider(new
  WpfWindowsTreeNodeImagesProvider())` and `ThemeManager.Current.Theme = sessionSettings.Theme`.

### Duplicate-type policy (decision, 2026-08-02)

The addin may only LINK the useful cs files from the ILSpy checkout; wherever a linked ILSpy type
duplicates an OpenDevelop type, OpenDevelop's own must be used instead. The concrete duplicate:

- ILSpy's `Controls/TreeView/*` (namespace `ICSharpCode.ILSpy.Controls.TreeView`) is a fork of
  OpenDevelop's own tree library `src/Libraries/SharpTreeView/ICSharpCode.TreeView` (namespace
  `ICSharpCode.TreeView`, referenced by ProjectBrowser/Unit Tests pads). It must be excluded from
  the link set and replaced by the OpenDevelop library.

Findings that shape the swap:

- The fork differs from OpenDevelop's library only cosmetically (brace style, copyright) plus:
  1. namespace (`ICSharpCode.ILSpy.Controls.TreeView` vs `ICSharpCode.TreeView`)
  2. node base type = `ICSharpCode.ILSpyX.TreeView.SharpTreeNode` (a DIFFERENT CLR type from
     `ICSharpCode.TreeView.SharpTreeNode`)
  3. `using TomsToolbox.Wpf;` in `SharpTreeView.cs`
  4. a `LinesRenderer.OnRender` null-guard (debug-only improvement)
- The fork's files are linked into the addin assembly today via
  `$(ILSpySrc)\Controls\**\*.cs` + the `SharpTreeView.xaml` Page.
- ILSpy's tree node layer (`TreeNodes/*`, `Analyzers/*`, `App.xaml.cs`,
  `Images/WpfWindowsTreeNodeImagesProvider.cs`, `NavigationState.cs`) uses
  `ICSharpCode.ILSpyX.TreeView` (~20 files) - `ILSpyTreeNode : SharpTreeNode` from the ILSpyX
  project (`externals/ilspy/ICSharpCode.ILSpyX/TreeView/*`, separate assembly, namespace
  `ICSharpCode.ILSpyX.TreeView`, incl. `PlatformAbstractions/`).
- API deltas to reconcile when using OpenDevelop's library:
  - `ICSharpCode.ILSpyX.TreeView.SharpTreeNode` has
    `SetImagesProvider(ITreeNodeImagesProvider)`/`ImagesProvider`
    (`PlatformAbstractions/ITreeNodeImagesProvider.cs`, implemented by
    `Images/WpfWindowsTreeNodeImagesProvider.cs`) - OpenDevelop's `SharpTreeNode` has none of
    this.
  - ILSpy nodes override `ActivateItemSecondary(IPlatformRoutedEventArgs)`;
    `PlatformAbstractions` also carries `IPlatformDataObject`/`IPlatformDragDrop`/
    `IPlatformDragEventArgs`/`XPlatDragDropEffects` for the fork's drag-drop (WPF
    implementations are in the fork's `WpfWindows*` files).
  - The fork's `SharpTreeView` adds `LockUpdates()`/`Dispose()`/`SetSelectedNodes(...)` and a
    `DefaultItemContainerStyleKey` getter.
- Two XAML views use the fork namespace directly:
  `AssemblyTree/AssemblyListPane.xaml` and `Analyzers/AnalyzerTreeView.xaml`
  (`xmlns:treeView="clr-namespace:ICSharpCode.ILSpy.Controls.TreeView"`).
- `externals/OpenDevelop/externals/ilspy` is a git checkout (branch `release/10.1`, nearly clean
  - only `ICSharpCode.ILSpyX/packages.lock.json` modified), so in-place edits there are possible
  but should be minimized; the csproj's Link mechanism exists to avoid them.

Open design question (user thinking about next steps): whether to do the full swap (namespace
rewrite in the ILSpy checkout + port `SetImagesProvider`/platform-abstraction surface into
OpenDevelop's `ICSharpCode.TreeView` + exclude the fork from the link set + add a direct
`ICSharpCode.TreeView` project reference) - noting the `SharpTreeNode` used by
`SetImagesProvider` must be OpenDevelop's type per the policy - or keep the fork linked for now
and only fix the theme resource names.

### Other findings from the test work (2026-08-02)

- Test-environment pollution: the hosted ILSpy restores its assembly list + layout from
  `~/.config/ICSharpCode/ILSpy.xml` at startup (`ILSpySettingsFilePathProvider`). Leftovers from
  interactive ILSpy usage (e.g. an assembly list pointing at a different checkout) load stale
  assemblies, auto-select a dead node, and produce
  `DirectoryNotFoundException` error text in the decompiled view. The integration test fixture now
  deletes this file before each launch (same rationale as `LastViewStates.xml`/`preferences/`).
- Build flake: `ILSpyAddIn` XAML compilation intermittently fails with
  MC3074/MC3050 ("SharpTreeView does not exist in namespace ...") inside the LibreWPF `wpftmp`
  project; a plain rebuild succeeds. Likely a wpftmp race, not a real error.
- `LayoutAnchorable` materialization for runtime-added tool panes is unreliable (depends on dock
  state; panes dock as non-selected tabs in shared panes). `od.ilspy.show-pane` re-registers the
  pane (RemoveToolPane + AddToolPane + Show) so its TAB deterministically appears, but does not
  make the content area render (see root cause above). Activating via
  `DockingManager.ActiveContent = model` (reflection) did not help either.
- Search-and-replace related (adjacent addin): `SearchManager.FindAllParallel` +
  `ObserveOnUIThread` NRE in this host because `SD.MainThread.SynchronizationContext` is null;
  the IObservable overload of `SearchResultsPad.ShowSearchResults` is therefore broken and
  nothing in the app uses it. `od.search.show-results` deliberately uses the sequential path +
  the `IEnumerable<SearchResultMatch>` overload (the FindReferencesCommand call shape).

## 2026-08 architecture update: use ILSpy as the shell-modernization reference

SharpDevelop stopped before its workbench architecture received the later improvements that are
visible in ILSpy. Embedding ILSpy is therefore not only a decompiler feature. It is also an
opportunity to replace selected SharpDevelop-era shell mechanisms with the maintained descendants
of the same ideas: observable pane/document models, command-driven MVVM, a single docking
workspace, and application-wide light/dark resource dictionaries.

This does **not** mean turning OpenDevelop into a fork of `ILSpy.exe`, nor linking ILSpy's `App`,
`MainWindow`, updater, single-instance code, or complete menu. OpenDevelop remains the owner of the
IDE shell, projects, editors, debugger, AddIns, and document lifetime. ILSpy supplies reusable
shell primitives and a proven design; the decompiler AddIn supplies ILSpy-specific panes and
services.

### Architectural direction

The target ownership is:

```text
OpenDevelop shell
  Application resources + theme selection
  WorkbenchWorkspace (documents + tool panes + active item + layout persistence)
  command/menu/toolbar adapters
  compatibility adapters for legacy IViewContent/IPadContent/AddInTree pads

Shared modern shell primitives (derived from/linkable with ILSpy)
  ObservableObjectBase
  PaneModel
  ToolPaneModel
  DocumentPaneModel (the host-neutral part of ILSpy TabPageModel)
  pane commands and activation/visibility state
  theme resource keys/tokens

ILSpy AddIn
  AssemblyTreeModel, SearchPaneModel, AnalyzerTreeViewModel
  DecompilerTextView and decompiler document models
  ILSpy composition/services and ILSpy-specific resource dictionaries
```

`WorkbenchWorkspace` is intentionally an OpenDevelop-owned name. ILSpy's `DockWorkspace` contains
valuable collection/activation behavior, but it also assumes ILSpy's `App.ExportProvider`,
`SessionSettings`, `MessageBus`, decompiler output and `TabPageModel`. Linking that file unchanged
would invert ownership and make the IDE shell depend on the decompiler. We should extract or port
the host-neutral behavior, while keeping the API and tests close enough to ILSpy that future diffs
remain reviewable.

### Source-reuse policy: link narrowly, adapt explicitly

The current `ILSpyAddIn.csproj` links broad directory globs such as `Commands/**/*.cs`,
`Controls/**/*.cs`, `Docking/**/*.cs`, and `ViewModels/**/*.cs`. That made the MVP possible, but it
also silently imports standalone-shell code, duplicates OpenDevelop types, and makes an ILSpy
submodule update capable of changing the AddIn's architecture without an intentional review.

Replace those broad globs with three explicit categories:

1. **Direct link** — unchanged ILSpy source compiled into the ILSpy AddIn. Use only when the type is
   ILSpy-specific, has no OpenDevelop duplicate, does not own `Application`/`MainWindow`, and its
   dependencies are already in the approved host surface. Examples include the assembly tree,
   analyzer/search models and views, decompiler text view, metadata views, language/decompilation
   services, and their narrowly-required commands.
2. **Shared-shell extraction or synchronized port** — small generic files whose design should
   replace OpenDevelop's older equivalent: `ObservableObjectBase`, `PaneModel`, `ToolPaneModel`,
   generic close/show commands, pane style selection, and theme resource-key types. Prefer moving
   a host-neutral version into a small OpenDevelop shell library that ILSpyAddIn references. Until
   an upstream ILSpy library exists, preserve file provenance and an upstream commit/path comment,
   and keep a focused diff test or update checklist. Do not compile two CLR types with equivalent
   responsibilities under different namespaces.
3. **Reference only / do not link** — `App.xaml.cs`, `MainWindow.xaml(.cs)`, single-instance and
   update UI, standalone settings ownership, full menus/toolbars, ILSpy's complete
   `DockWorkspace`, and any duplicate AvalonDock/SharpTreeView implementation. These inform the
   OpenDevelop implementation but cannot own the IDE process.

Maintain the categories in the project file as named item groups (for example
`ILSpyLinkedModel`, `ILSpyLinkedView`, `ILSpyLinkedResource`) with explicit includes. Add a build
check that fails if a linked path disappears after an ILSpy submodule update. Every new linked file
must state why it belongs to category 1 rather than expanding a glob.

### Modern pane and document model

Adopt one model hierarchy for both built-in IDE panes and ILSpy panes:

```text
PaneModel
  ContentId             stable persistence identity
  Title
  IsVisible
  IsActive
  IsSelected
  IsCloseable
  CloseCommand

ToolPaneModel : PaneModel
  PreferredDockSide
  PreferredDockSize
  IconKey
  AssociatedCommand

DocumentPaneModel : PaneModel
  document identity / dirty / save / close contract
  optional view or view factory
```

The model must not expose `LayoutAnchorable`, `LayoutDocument`, `DockingManager`, WPF controls, or
ILSpy decompiler types. AvalonDock is the WPF renderer of this state, not the state itself. This is
also the seam a future UnoDock host can consume.

Replace `IlSpyToolPaneAdapter` after the common model is established: ILSpy panes should enter the
same `ToolPanes` collection as Project Browser, Properties, Output, Search Results, Unit Tests and
Debugger panes. Property mirroring between two pane base classes is transitional and must not
become the permanent composition boundary.

ILSpy's `TabPageModel` should not immediately replace OpenDevelop's file document model. First
introduce `DocumentPaneModel` and adapt both `AvalonWorkbenchWindow`/`IViewContent` and ILSpy
decompiler tabs to it. File-backed documents still delegate load/save/dirty handling to
`OpenedFile`; virtual decompiler documents carry an ILSpy navigation state and are read-only. Once
both use the same workspace collection, the special dummy `TabPageModel` and
`DecompiledCodeViewContent` lifecycle workarounds can be removed.

### Docking and layout replacement

The current split between `AvalonDockLayout`, legacy AddInTree `Pad` descriptors,
`AvalonPadContent`, `AvalonWorkbenchWindow`, runtime `DockWorkspaceExtensibility`, and two layout
formats is the old architecture to retire.

Build the replacement in this order:

1. Make `WorkbenchWorkspace` the sole owner of observable document/tool-pane collections, active
   document, active pane and show/close operations. `WpfWorkbench` and commands call this service;
   they do not manipulate AvalonDock objects directly.
2. Add a pane registry/factory keyed by stable `ContentId`. Modern panes register a model factory.
   Legacy AddInTree `Pad` entries are materialized through a `LegacyPadAdapter` and registered in
   the same registry, so migration is incremental.
3. Bind AvalonDock `DocumentsSource` and `AnchorablesSource` once. Replace reflection and
   remove/re-add activation tricks with explicit workspace selection/activation state plus one
   `ILayoutUpdateStrategy`.
4. Store layouts as an OpenDevelop-owned versioned DTO: pane identity, side/group/order,
   proportions, floating bounds and visibility. Treat AvalonDock XML as a WPF serialization
   detail or import format, not the durable application contract. Documents are restored by the
   document/session service, never deserialized as arbitrary CLR content.
5. Implement named layouts (`Default`, `Debug`, `Plain`, `ILSpy`) as DTO templates. Switching a
   layout applies placement/visibility to existing models without reconstructing services or
   losing open documents. The ILSpy template shows Assemblies left, Search/Analyze right or bottom,
   and shares the central document area with source editors.
6. After every built-in pad has a model/factory or compatibility registration, delete direct
   `AvalonPadContent` creation and the legacy SharpDevelop layout loader. Keep an importer for one
   release if existing user layouts need migration.

AvalonDock unification described earlier remains a prerequisite for one WPF visual tree and one
serializer. It is separate from the model contract: the workspace and layout DTO must compile and
be unit-testable without AvalonDock, so changing the docking renderer does not restart the shell
migration.

### Full application theming

`IdeThemeService` currently changes only `DockingManager.Theme`; that is not a complete dark theme.
ILSpy demonstrates the required application-level approach: semantic resource keys, paired base
light/dark dictionaries, named theme dictionaries, control default styles, and a single theme
manager that swaps resources consistently.

Create an OpenDevelop-owned theme contract and let ILSpy resources participate in it:

- Define semantic tokens (`WindowBackground`, `ToolWindowBackground`, `EditorBackground`,
  `ControlBackground`, `Border`, `Foreground`, `MutedForeground`, `Selection`, error/warning/info,
  syntax colors) instead of hard-coded brushes in views.
- Link or synchronously port ILSpy's generic `ResourceKeys`, `SyntaxColor` and `ThemeManager`
  behavior where it is host-neutral. Keep ILSpy control templates in the ILSpy AddIn; promote only
  generic tokens/styles into the shell.
- Make theme switching update application dictionaries, AvalonDock theme, AvalonEdit highlighting,
  icons and hosted ILSpy views in one transaction. AddIns consume dynamic semantic resources and
  do not choose a theme themselves.
- Provide Light and Dark first. Treat ILSpy's RSharp and VS Code variants as later theme packs,
  not additional conditionals in controls.
- Add a resource audit for hard-coded foreground/background colors and a visual integration test
  that opens representative editor, project, output, dialog and ILSpy panes under both themes.

The ILSpy AddIn must not set `ThemeManager.Current.Theme` independently once this bridge exists;
the shell theme service maps the selected OpenDevelop theme to ILSpy resources. This avoids two
theme authorities and fixes mixed light/dark windows.

### MVVM and command migration

MVVM migration follows user-visible shell boundaries, not a wholesale rewrite:

- Views contain bindings, templates and truly visual behavior only.
- Pane/document models own observable state and commands.
- Services own file/project/debug/decompilation operations and are injected into models.
- AddInTree may continue to contribute command descriptors, but the command target resolves a
  workspace/model service; it must not search the visual tree or cast active AvalonDock content.
- Prefer ILSpy's `ICommand`/delegate-command patterns where generic. Do not link commands whose
  implementation reaches `App.ExportProvider`; inject the required service through the
  OpenDevelop composition boundary instead.

The first built-in migration candidates are Project Browser, Properties, Output and Search
Results: they exercise tool-pane activation, selection, persistence and theme coverage without
the editor's more complex file lifetime. Debugger pads follow as one group because layout switching
must preserve their coordinated visibility. Dialog MVVM is useful but is not on the critical path
for ILSpy layout.

### Composition boundary

Do not require all OpenDevelop AddIns to adopt ILSpy's container in the first phase. Introduce a
small shell-facing registration API (`IToolPaneProvider`, `IDocumentPaneFactory`, theme resource
provider and command provider). Adapt both the existing Microsoft.VisualStudio.Composition exports
and ILSpy's TomsToolbox export provider into that API.

Once the runtime inventory shows that no required AddIn relies on the old MEF-specific pane
contract, decide whether TomsToolbox/DI becomes the single container. Container replacement is a
later cleanup, not a prerequisite for common pane models. This corrects the earlier plan that made
composition replacement the first step and reduces the risk of coupling docking work to AddIn
activation work.

### Phased implementation plan

#### Phase 0 — baseline and link audit

- Record the ILSpy submodule commit and generate an explicit inventory of every linked `.cs` and
  XAML file, its category, and why it is required.
- Remove broad globs and excluded-file lists in favor of explicit link manifests.
- Finish the SharpTreeView/AvalonDock single-type decisions before adding more linked UI.
- Add smoke tests for opening an assembly, rendering all three real ILSpy pane contents, opening a
  decompiler document, and switching away from/back to the ILSpy layout.

Exit: linked-source updates are reviewable; no duplicate tree/dock CLR types are loaded; current
ILSpy MVP behavior is green.

#### Phase 1 — common pane models and workspace

- Introduce the host-neutral pane/document contracts and `WorkbenchWorkspace` in a project below
  `SharpDevelop.exe` and above UI-specific AddIns.
- Port the useful ILSpy observable/command behavior with provenance; add property/command tests.
- Adapt existing `DockWorkspace`, `AvalonWorkbenchWindow`, modern `ToolPaneModel` panes and legacy
  pads to the new workspace.
- Adapt ILSpy pane exports directly to the common model and remove `IlSpyToolPaneAdapter`.

Exit: there is one pane collection and one activation path; ILSpy and built-in panes persist by
stable `ContentId`; the old APIs are compatibility façades only.

#### Phase 2 — versioned layout service and ILSpy layout

- Implement the renderer-independent layout DTO, migration/import, templates and persistence.
- Make `Default`, `Debug`, `Plain`, and `ILSpy` use the same service.
- Preserve open documents and service instances across layout switches.
- Remove runtime pane re-registration/reflection activation workarounds.

Exit: clean-profile and restored-profile tests produce the same pane groups; corrupt/unknown pane
entries degrade safely; ILSpy layout is a first-class named layout.

#### Phase 3 — application-wide Light/Dark theme

- Introduce semantic theme resources and bridge them to AvalonDock, AvalonEdit and ILSpy.
- Convert shell chrome and the Phase 1 candidate panes, then audit remaining hard-coded colors.
- Persist theme choice independently from layout choice and verify live switching without restart.

Exit: representative IDE and ILSpy surfaces have readable contrast in Light and Dark, with no
locally-owned ILSpy theme state.

#### Phase 4 — document unification

- Adapt source/file documents and ILSpy virtual tabs to `DocumentPaneModel`.
- Move active-document and close/save routing from `WpfWorkbench`/AvalonDock casts into workspace
  commands and document services.
- Remove the dummy ILSpy `TabPageModel` path and special decompiler tab lifetime glue.

Exit: source and decompiler tabs share navigation, activation, close and layout behavior while
retaining their distinct persistence/save semantics.

#### Phase 5 — retire legacy shell paths

- Migrate remaining built-in pads in coherent groups and remove their AddInTree `Pad` descriptors.
- Delete `AvalonPadContent`, obsolete layout XML restoration and redundant pane model/composition
  types when repository searches prove they have no live callers.
- Remove the external-process ILSpy launcher after every supported command has an in-process path.

Exit: no live feature creates a SharpDevelop-era pad or manipulates AvalonDock directly outside the
WPF renderer; compatibility code is either deleted or explicitly isolated for third-party AddIns.

### Verification matrix

Each phase must cover more than compilation:

```text
Workspace unit tests
  collection/activation/close invariants
  stable ContentId and duplicate registration
  legacy adapter behavior

Layout tests
  clean template, round trip, unknown/missing pane, corrupt file
  switch Default <-> Debug <-> ILSpy without document loss
  floating/auto-hidden panes and multi-monitor bounds normalization

Theme tests
  resource completeness for Light/Dark
  live switch across shell, AvalonDock, AvalonEdit and ILSpy controls

ILSpy integration tests
  open/close/reopen assembly
  assembly tree node visible and selectable
  search/analyzer navigation opens the expected document
  decompiler document close/reopen and navigation history
  startup with no pre-existing ~/.config/ICSharpCode/ILSpy.xml
```

WPF integration tests should inspect actual rendered content, not merely tab headers or constructed
view instances; the empty-control false positive documented above must remain a regression test.

### Immediate next actions

1. Replace `ILSpyAddIn.csproj` directory globs with the audited explicit link manifest.
2. Resolve the SharpTreeView duplicate by converging on OpenDevelop's shared tree library (including
   the small ILSpyX image/platform API delta) and prove the three pane contents render.
3. Add the host-neutral pane/workspace contracts and adapt one built-in pane plus one ILSpy pane as
   a vertical slice.
4. Define the versioned layout DTO and encode `ILSpy` as a template rather than another legacy
   SharpDevelop XML layout.
5. Replace `IdeThemeService`'s dock-only switch with the semantic application resource contract,
   initially covering Light and Dark.

These actions deliberately establish one model and one source-reuse policy before expanding the
embedded ILSpy surface. That makes later ILSpy updates an input to OpenDevelop's architecture,
rather than allowing linked standalone-shell implementation details to become the architecture by
accident.
