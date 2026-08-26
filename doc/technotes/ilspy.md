# ILSpy AddIn Port

## Status (2026-08-03): feature integration complete

The ILSpy decompiler AddIn feature work described by this document - the part a user actually
interacts with - is done: the `ILSpy` workbench layout (Assemblies/Search/Analyze pads +
decompiled-code documents), the toolbar (icon buttons, dropdowns, visibility toggles), MSIL/Asm
syntax highlighting, reference hyperlink navigation, multi-select decompilation, single member-
and namespace-node routing to native documents, and cross-assembly reference navigation are all
implemented and covered by `tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs`. See "Closing
out the remaining smaller gaps" near the end of this document for the final items closed.

Also fixed in the course of that work, but scoped wider than the AddIn itself: a real "the ILSpy
layout gets lost" bug in the shell's own `AvalonDockLayout.StoreConfiguration()` - it could
persist a not-yet-templated, ad-hoc pane arrangement if a layout switch happened before the
docking manager's first `Loaded` event, permanently corrupting the saved layout on every
subsequent launch. Fixed with a guard symmetric to `LoadConfiguration`'s existing one; see
`AvalonDockLayout.cs`'s `StoreConfiguration` for the full explanation.

**Not done, and out of scope for "the ILSpy AddIn":** the broader "use ILSpy as shell-
modernization reference" architecture initiative this document also opened (see "2026-08
architecture update" below) - `WorkbenchWorkspace`/`DocumentPaneModel` consolidation and the
AvalonEdit/full-app theming Phase 3 token work remain deliberately deferred, longer-running,
separately-scoped efforts, not blockers for the AddIn's own completeness. The versioned layout
DTO is now in progress: steps 1 (the `Capture`/`Apply` converter) and 2 (it's now the actual
persisted format, AvalonDock XML kept only as an import format for templates/legacy files) are
done and live-verified 2026-08-03, see "Real versioned layout DTO, step 1/step 2" near the end.
Step 3 (persisting open documents) remains open. Each is flagged as still-open at its own point
in the document below.

## Layout goal

OpenDevelop should expose an `ILSpy` workbench layout alongside `Default`, `Debug`, and
`Plain`. Selecting it should switch the IDE into a decompiler-oriented workspace by hosting the
real WPF ILSpy panes:

- left: ILSpy `AssemblyTreeModel` + `AssemblyListPane`
- center: decompiled output as an OpenDevelop document tab (a read-only, virtual file - see
  `DecompiledCodeViewContent`), not a dedicated pad
- top (above the documents): ILSpy `SearchPaneModel` - real ILSpy docks Search above the
  decompiled-code documents (`Docking/DockLayoutSettings.cs`), and ILSpyAddIn/Layouts/ILSpy.xml
  mirrors that
- bottom (below the documents): `AnalyzerTreeViewModel`, and any future exported `ToolPaneModel`

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

**Resolved (2026-08-02) - narrower than first thought, NOT a missing-content problem.** step 2
(re-point to a project reference) is already done for `ILSpyAddIn.csproj` - it references
`src/Libraries/AvalonDock/source/Components/AvalonDock.Themes.VS2013/AvalonDock.Themes.VS2013.csproj`
directly, not the Dirkster NuGet package. ILSpy's linked `Themes/Base.Light.xaml`/`Base.Dark.xaml`
each merged in `/AvalonDock.Themes.VS2013;component/lighttheme.xaml` /
`.../darktheme.xaml` (the upstream Dirkster package's static-XAML resource names) and this
technote's first pass concluded those resources were simply missing from OpenDevelop's fork -
**that framing was wrong, corrected after the user pushed back** ("this fork must contain
everything, v5 comes from this repo - look again"). The fork *does* contain the VS2013 palette
data - it just modernized how VS2013 themes are built: `Vs2013LightTheme`/`Vs2013DarkTheme`
(`AvalonDock.Themes.VS2013/Vs2013{Light,Dark}Theme.cs`) construct their `ResourceDictionary`
programmatically at runtime from a GZIP-compressed `.vstheme` palette
(`Resources/vs2013{light,dark}.vstheme.gz`) via `VsThemePaletteFactory.BuildDictionary(...)`,
rather than shipping a static `lighttheme.xaml`/`darktheme.xaml` resource at all - so no amount of
searching for that filename in the fork would ever find it, by design, not by omission.

Given that, the fix was to stop trying to port/recreate that static resource and instead notice
that ILSpy's own `Base.Light.xaml`/`Base.Dark.xaml` already fully redefine every color/brush key
they use (`SystemColors.*`, `ICSharpCode.ILSpy.Themes.ResourceKeys.*`,
`TomsToolbox.Wpf.Styles`' `styles:ResourceKeys.*`) standalone - the merged dictionary was
vestigial for this hosting scenario. **Removed** the broken `<ResourceDictionary
Source="/AvalonDock.Themes.VS2013;component/{light,dark}theme.xaml" />` merge from both files
rather than porting anything.

Combined with the separate `ThemeManager.UpdateTheme` pack-URI fix (see "Root cause: linked theme
resource names" below), **the full ILSpy hosting pipeline now works end-to-end for the first time
this technote records.** Verified via DevFlow: `od.ilspy.show-pane` and `od.ilspy.open-assembly`
both return `"success":true` (previously always threw); `od.ilspy.status` reports all three panes
(`assemblyListPane`/`searchPane`/`analyzerPane`) visible, the target assembly loaded, and 5189
characters of real decompiled C# source text (usings, assembly attributes, the actual
`SharpDevelopMain.Main` entry point) - not an empty/error snippet. The UI element tree shows real
`ICSharpCode.ILSpy.Controls.TreeView.SharpTreeViewItem`/`SharpTreeNodeView` instances with varied
non-zero widths (e.g. 348×17, 318×17, 285×17 - different per node's text) - the exact opposite of
the "empty container, 0 `VisualTreeHelper` children" failure mode this technote documented as
blocking ILSpy verification from the very first session. This closes out the last remaining
"pre-existing, unrelated bug" blocker mentioned throughout the pane-model and layout work above.

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

**Follow-up bug found and fixed (2026-08-02):** even after `generic.xaml` and `Theme.*.xaml` were
linked at the assembly root (fixing the lookup path) and `ThemeManager.Current.Theme = ...` was
actually being called (`IlSpyWorkspaceHost.EnsureInitialized()`), it still threw
`IOException: Cannot locate resource 'themes/theme.light.xaml'` at runtime. Root cause:
`ThemeManager.UpdateTheme` (`Themes/ThemeManager.cs`) builds its `ResourceDictionary.Source` from a
*bare* relative pack URI - `new Uri($"/themes/Theme.{themeFileName}.xaml", UriKind.Relative)`, with
no `;component/` authority segment. That kind of URI resolves against
`Application.ResourceAssembly`, which defaults to the process's *entry* assembly
(`SharpDevelop.exe`) - not the assembly that owns the calling code - and unlike a control's own
default-style lookup (which resolves against the control's defining type's assembly), there is no
per-assembly fallback for a `ResourceDictionary.Source` set this way. Since the `Theme.*.xaml` BAML
only exists in `ILSpyAddIn.dll`, resolving against the host `.exe` always fails. Tried setting
`Application.ResourceAssembly = typeof(IlSpyWorkspaceHost).Assembly` in `IlSpyWorkspaceHost.cs`
first - confirmed at runtime this throws `InvalidOperationException: The 'ResourceAssembly'
property ... cannot be changed after it has been set` (WPF/LibreWPF sets it once, automatically,
before this addin ever loads) - so that approach is a dead end, not just inelegant. Fixed instead
at the actual source: `ThemeManager.cs` now qualifies the URI with its own defining assembly's name
(`new Uri($"/{typeof(ThemeManager).Assembly.GetName().Name};component/themes/Theme.{themeFileName}.xaml", ...)`),
which resolves unambiguously regardless of which process hosts `ThemeManager` - strictly more
correct for standalone `ILSpy.exe` too, not just this hosting scenario. This got past the original
failure to a *new, different* one - see the "AvalonDock 5 unification" section above for what that
turned out to be (a real missing resource in OpenDevelop's own vendored AvalonDock fork, not
another instance of this same bug class).

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

**Correction (2026-08-02 deep audit): the divergence above is understated.** A full file-by-file
diff of `ICSharpCode.ILSpyX.TreeView.SharpTreeNode` (757 lines) against OpenDevelop's
`ICSharpCode.TreeView.SharpTreeNode` found the node-base APIs are **substantively incompatible**,
not "cosmetic plus 4 items":

- `Delete()`/`DeleteCore()` (no args) vs. OpenDevelop's `Delete(SharpTreeNode[])` - different
  per-node-vs-per-selection contract. `Cut(SharpTreeNode[])` exists only on OpenDevelop's side;
  the fork has no cut concept.
- `Copy(SharpTreeNode[]) : IPlatformDataObject` vs. OpenDevelop's `Copy(SharpTreeNode[])` void +
  `GetDataObject(SharpTreeNode[]) : IDataObject` - different return contract and native-vs-
  abstraction data type.
- `StartDrag(object, SharpTreeNode[], IPlatformDragDrop)` vs. `StartDrag(DependencyObject,
  SharpTreeNode[])` - extra drag-drop-manager abstraction parameter with no OpenDevelop analog.
- `CanDrop`/`Drop` take `IPlatformDragEventArgs` vs. OpenDevelop's native `DragEventArgs`.
- `ActivateItem(IPlatformRoutedEventArgs)` **and** `ActivateItemSecondary(IPlatformRoutedEventArgs)`
  vs. OpenDevelop's `ActivateItem(RoutedEventArgs)` only - no secondary-activation hook exists on
  the OpenDevelop side at all.
- `ImagesProvider`/`SetImagesProvider` has no OpenDevelop analog (confirmed, as already noted).
- `ShowContextMenu(ContextMenuEventArgs)` and `Model`/`GetModel()` exist only on OpenDevelop's
  side, with no fork equivalent.
- The two `SharpTreeNode` types also disagree on nullable-reference-type annotation (ILSpyX is
  NRT-annotated throughout; OpenDevelop's is not), so even members that do line up aren't a clean
  textual merge.
- `PlatformAbstractions/` (`IPlatformDataObject`, `IPlatformDragDrop`, `IPlatformDragEventArgs`,
  `IPlatformRoutedEventArgs`, `ITreeNodeImagesProvider`, `XPlatDragDropEffects`) has no analog
  anywhere in OpenDevelop's tree library - it would need to be ported wholesale or every call site
  rewritten to native WPF types.

The control layer (`SharpTreeView`/`SharpTreeViewItem`/`SharpTreeNodeView`) is comparatively close
to drop-in (cosmetic diffs plus the already-known `LockUpdates`/`Dispose`, `SetSelectedNodes`), but
OpenDevelop's library additionally has **no automation-peer support and no type-to-search**
(`SharpTreeViewAutomationPeer.cs`, `SharpTreeViewItemAutomationPeer.cs`,
`SharpTreeViewTextSearch.cs` exist only in the fork) - porting the control without these is a
feature regression, not just a rename.

The control and node-base swaps are **one unit of work, not two independently schedulable ones**:
`SharpTreeView`/`SharpTreeViewItem` are generically coupled to whichever `SharpTreeNode` they
compile against, so swapping the control without also re-deriving `ILSpyTreeNode` (ILSpyX) and its
~20-27 descendants (`AssemblyTreeNode.cs`, `AssemblyListTreeNode.cs`,
`AssemblyReferenceTreeNode.cs`, `DerivedTypesEntryNode.cs`, `BaseTypesEntryNode.cs`,
`ModuleReferenceTreeNode.cs`, and others under `TreeNodes/`) from OpenDevelop's `SharpTreeNode` is
not mechanically possible. An additional reference site not previously tracked here:
`externals/ilspy/ILSpy/Views/CompareView.xaml` also uses the fork namespace and is linked into the
addin via the `Views/**` glob.

Net effect: this is budgeted as a multi-file rewrite of every `TreeNodes/*` override body (new
method signatures) plus a genuine feature-port (images-provider, secondary-activation, cut,
automation peer, text search) into OpenDevelop's `ICSharpCode.TreeView` - not a namespace-and-
reference swap. Given this, and that this addin cannot be UI-tested on this (macOS) development
machine, this swap should not be attempted as a single uninterrupted pass; it needs its own
phased/reviewable plan (e.g. port control layer first behind a feature-parity checklist, then
node-base signatures file-by-file, keeping the build green after each step) rather than folding it
into the Phase 0 link-manifest pass above.

**Resolved (2026-08-02): reverse direction taken instead - adopt ILSpy's tree wholesale.** Rather
than porting ILSpy's missing features into OpenDevelop's `ICSharpCode.TreeView`, the decision (user
directive) was to make ILSpy's fork the ONE tree implementation project-wide and migrate every
OpenDevelop consumer onto it - eliminating the duplicate instead of enriching one side of it. This
was a materially larger change than the SharpTreeView-only swap above once its real blast radius
was measured (see "Findings that shaped the actual shape of the fix" below), and it surfaced two
follow-on architectural costs (composition-adjacent, not tree-specific) that were each confirmed
with the user before proceeding rather than decided silently:

- `ICSharpCode.ILSpyX.TreeView.SharpTreeNode`'s home project, `ICSharpCode.ILSpyX.csproj`, itself
  references `ICSharpCode.Decompiler`/`Mono.Cecil`/`K4os.Compression.LZ4` - referencing it directly
  from a shell-wide shared library would have pulled a decompiler engine into ClassBrowser/
  Debugger/CodeCoverage/CodeQuality/UnitTesting/AndroidSdkManager. Resolved by extracting
  `TreeView/` into its own dependency-light project,
  `externals/ilspy/ICSharpCode.ILSpyX/TreeView/ICSharpCode.ILSpyX.TreeView.csproj` (referenced by
  both `ICSharpCode.ILSpyX.csproj` and the shared control project below), rather than accepting the
  heavier dependency footprint.
- The fork's control template (`Controls/TreeView/SharpTreeView.xaml`) uses
  `TomsToolbox.Wpf.Styles`. Rather than hand-writing a non-TomsToolbox replacement template, the
  user accepted `TomsToolbox.Wpf.Styles`/`TomsToolbox.Composition.MicrosoftExtensions` becoming a
  dependency of `src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj` (the core shell) - consistent
  with, and arguably an early down payment on, the "OpenDevelop migrates to TomsToolbox composition"
  direction already decided in the "Composition boundary" section above.

### Findings that shaped the actual shape of the fix

- An exhaustive inventory of every class overriding a tree-behavior member (Delete/Copy/Drag/Drop/
  Activate/ShowContextMenu) across the whole non-ILSpyAddIn codebase found the real blast radius
  much smaller than a raw `grep` for `ICSharpCode.TreeView` suggested (~40 files matched the text,
  but most only reference `Text`/`Icon`/`IsCheckable` and needed no behavior change):
  - `src/Main/Base/Project/Dom/ClassBrowser/**` (the entire legacy Class Browser, ~13 files) is
    already excluded from the MVP build via `<Compile Remove="Dom\ClassBrowser\**\*.cs">` in
    `ICSharpCode.SharpDevelop.csproj` - dead code today, not a live migration risk despite matching
    the text search.
  - Only ~10 files had a real signature change to make: `SharpTreeNodeAdapter.cs` (Debugger,
    `CanDelete(SharpTreeNode[])`/`Delete(SharpTreeNode[])` -> `CanDelete()`/`Delete()` - per-node,
    not per-selection, matching how the fork's `SharpTreeView` actually invokes it), `WatchPad.cs`'s
    `WatchRootNode` (`CanPaste`/`Paste`/`GetDropEffect` -> one `CanDrop`/`Drop` pair over
    `IPlatformDragEventArgs`), and five `ActivateItem(RoutedEventArgs)` overrides (`UnitTestNode`,
    `CodeCoverageClassTreeNode`, `CodeCoverageMethodTreeNode`, plus two dead ClassBrowser ones) ->
    `ActivateItem(IPlatformRoutedEventArgs)` - every one of these ignored its event-arg parameter's
    actual members, so no behavior changed, only the parameter type.
- `GetModel()`/`Model`/`ShowContextMenu` have no ILSpyX equivalent but are used pervasively by
  `ModelCollectionTreeNode`-derived classes. Added as an OpenDevelop-authored partial-class file,
  `externals/ilspy/ICSharpCode.ILSpyX/TreeView/SharpTreeNode.OpenDevelop.cs` - a NEW file alongside
  the checkout (like the extracted micro-project itself), not an edit to existing ILSpy source. It
  has to live in that project, not a downstream one: C# partial classes only merge within the same
  assembly compilation, so a `partial class SharpTreeNode` declared from a different assembly
  creates a shadowing duplicate type instead of extending the real one (hit this directly as
  CS0436 on the first attempt). `ShowContextMenu`'s parameter is typed `object` rather than WPF's
  `ContextMenuEventArgs`, since the extracted project is deliberately platform-neutral (no WPF
  reference) and a repo-wide search found the parameter is ignored by every existing override and
  has no live caller today.
- XAML consumers (`AndroidSdkManagerWindow.xaml`, `AnalysisProjectOptionsPanel.xaml`,
  `DependencyMatrixView.xaml`, `CommonResources.xaml`, `SearchForIssuesDialog.xaml`) all reference
  the tree control via the custom XML namespace URI `http://icsharpcode.net/sharpdevelop/treeview`
  (an `XmlnsDefinition` on the library assembly), not a `clr-namespace:` URI - so repointing that
  one `XmlnsDefinition` attribute (in `ICSharpCode.TreeView`'s `AssemblyInfo.cs`) from
  `ICSharpCode.TreeView` to `ICSharpCode.ILSpy.Controls.TreeView` meant none of those five XAML
  files needed any change. Two ILSpy-internal XAML files that DO use a direct `clr-namespace:` URI
  (`AssemblyTree/AssemblyListPane.xaml`, `Analyzers/AnalyzerTreeView.xaml`) needed
  `;assembly=ICSharpCode.TreeView` appended, since the control's type moved to a different assembly
  than the one compiling those views.
- The control's actual default styles (`SharpTreeView`/`SharpTreeViewItem`/`SharpTreeNodeView`/
  `SharpGridView`, plus `InsertMarker`/`EditTextBox`) live in `Controls/TreeView/SharpTreeView.xaml`,
  not at the WPF-conventional `themes/generic.xaml` path (that only holds unrelated resources
  upstream) - the shared project's own `Themes/Generic.xaml` merges that dictionary by pack URI
  rather than duplicating its content, so the implicit-default-style lookup still resolves.

### What was NOT touched

- `ICSharpCode.TreeView.Demo` (`src/Libraries/SharpTreeView/ICSharpCode.TreeView.Demo`) still uses
  the old API and is left broken - it is only referenced by its own standalone
  `SharpTreeView.sln`, not the main `SharpDevelop.sln`/build, so it never participates in what ships.
- Three pre-existing, unrelated build/runtime issues were found and left alone (confirmed
  unconnected to this change - different files, different error classes, reproduce independent of
  any tree-view edit): `CodeAnalysis.csproj` targets .NET Framework 4.5 without the reference
  assemblies installed on this machine; `AndroidSdkManager.csproj` is missing
  `LeXtudio.DevFlow`/`Microsoft.Maui` types; `ICSharpCode.SharpDevelop.Tests.csproj` hits an
  NU1605 LibreWPF/ProGPU package-downgrade lock-file conflict.
- A pre-existing, unrelated runtime bug was hit while verifying this change at runtime:
  `RegisterCodeCoverageOpenLensProviderCommand.Run()` (part of a separate, already-in-progress
  OpenLens feature, not this session's work) calls `CodeCoverageService.ResultsChanged +=` during
  `CoreStartup.RunInitialization()`, before `IWorkbench` is registered, which poisons
  `CodeCoverageService`'s static type initializer for the rest of the process (a cached
  `TypeInitializationException` is rethrown on every later access, including from menu
  construction). Worked around only for this verification session by temporarily moving
  `AddIns/Analysis/CodeCoverage/net10.0-windows/CodeCoverage.addin` aside and back; not fixed, since
  it is unrelated to the tree-view migration.
- ILSpy's own hosted panes (`od.ilspy.show-pane`/`od.ilspy.open-assembly`) still fail at runtime on
  an unrelated, pre-existing issue: `IlSpyWorkspaceHost.EnsureInitialized()`'s
  `ThemeManager.Theme = ...` throws `IOException: Cannot locate resource
  'themes/theme.light.xaml'` - this is the same linked `ThemeManager.cs`/`Theme.*.xaml` Page-linking
  code this technote already flagged as unresolved (see "Root cause: linked theme resource names"
  and "Current host implementation status" above); neither the code path nor the Page/XmlnsDefinition
  linkage for these files changed in this pass.

### Verification performed

- Every touched project builds clean individually: `ICSharpCode.ILSpyX.TreeView.csproj`,
  `ICSharpCode.ILSpyX.csproj`, `ICSharpCode.TreeView.csproj`, `ILSpyAddIn.csproj`,
  `ICSharpCode.SharpDevelop.csproj`, `Debugger.AddIn.csproj`, `UnitTesting.csproj`,
  `CodeCoverage.csproj`, `CodeQuality.csproj`, `CSharpBinding.csproj`.
- Launched `SharpDevelop.exe` on this machine (confirms LibreWPF really does run on macOS) and drove
  it via its DevFlow HTTP agent (port 9299, see `DevFlowPort.cs`). Pixel screenshots aren't available
  in this Debug build (missing native `wpfgfx_cor3.dll` for the screenshot capability specifically),
  but the `/api/v1/ui/tree` element tree confirmed `ICSharpCode.UnitTesting.TestTreeView` (a
  `SharpTreeView` subclass) renders its real control template end-to-end - `Border` ->
  `ScrollViewer` -> `Grid` -> `ScrollContentPresenter` -> `ItemsPresenter` ->
  `VirtualizingStackPanel`, sized to its real layout bounds - rather than the empty
  zero-child container this technote documented as the old failure mode for hosted ILSpy panes.

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

  **Fixed (2026-08-02):** root cause was `WorkbenchStartup.InitializeWorkbench()`
  (`src/Main/SharpDevelop/Workbench/WorkbenchStartup.cs`) capturing `SynchronizationContext.Current`
  to construct the app's `DispatcherMessageLoop` - but at that point in startup, the WPF
  `Dispatcher` had never pumped a message on that thread yet (that's normally what installs a
  `DispatcherSynchronizationContext` as the thread's ambient one), so `SynchronizationContext.Current`
  was always `null`, making `SD.MainThread.SynchronizationContext` (and therefore
  `ReactiveExtensions.ObserveOnUIThread<T>`, which reads it directly) permanently broken for the
  whole process. Fixed by constructing `new DispatcherSynchronizationContext(app.Dispatcher)`
  explicitly instead of relying on ambient thread state at that specific call site. Verified:
  `SharpDevelop.csproj` builds clean, a fresh launch shows no new startup errors, and both
  `IlSpyAddInTests` (unrelated to this bug, used as a regression smoke check since they exercise
  the same startup path) still pass.

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

   **Status (2026-08-02): AvalonDock-XML-as-import-format restore is live; the versioned DTO
   itself is still not started.** `AvalonDockLayout.LoadLayout()`
   (`src/Main/SharpDevelop/Workbench/AvalonDockLayout.cs`) had its `dockWorkspace.RestoreLayout(...)`
   call commented out with a `TODO: re-enable after migrating legacy pads to MEF ToolPaneModel` -
   investigated and re-enabled: `DockWorkspace.RestoreLayout`'s `LayoutSerializationCallback`
   (`DockWorkspace.cs`) already cancels/skips (no exception) any serialized `LayoutAnchorable`
   whose `ContentId` isn't a currently-registered MEF `ToolPaneModel`, so legacy (AddInTree
   `Pad`-based) anchorables were never actually going to crash anything - the TODO's stated blocker
   didn't hold up under inspection. The **real** reason nothing restored: the shipped
   `data/layouts/{Default,Debug,Plain,ILSpy}.xml` template files were stale AvalonDock 1.x-schema
   XML (`<DockingManager version="1.3.0"><ResizingPanel>...<DockableContent Name="...">`), while
   `XmlLayoutSerializer` (the modern serializer already in use) expects
   `<LayoutRoot><RootPanel>...<LayoutAnchorable ContentId="...">`. A fresh install (no user
   `config/layouts/Default.xml` yet) would deserialize-fail on the old schema every time, caught
   only by `LoadConfiguration`'s generic `catch (Exception ex)` (shows an error dialog, then
   continues with an unconfigured layout - not a crash, but not what "restore" was meant to do
   either). Regenerated all four template files by hand in the current schema, referencing the one
   real MEF-exported pane that exists today (`ContentId="ProjectBrowser"`, `DockWidth="280"` -
   matching the `PreferredDockSize` set earlier in this same pass) for `Default`/`Debug`, an empty
   `LayoutDocumentPane`-only layout for `Plain`, and the ILSpy AddIn's three real pane `ContentId`s
   (`assemblyListPane`/`searchPane`/`analyzerPane`, confirmed by reading their actual
   `PaneContentId` constants rather than guessing) for `ILSpy` - the first real, if minimal,
   ILSpy-specific pane arrangement this technote has shipped, versus the empty
   `<LayoutRoot></LayoutRoot>` placeholder that was there before.

   This is still explicitly the "AvalonDock XML as import format" half of step 4, not the versioned
   DTO itself - no pane identity/side/group/order/proportions model exists yet; today's fix makes
   the *existing* file-based restore mechanism actually run instead of silently no-op.

   **Bug found and fixed while verifying this:** the app's actual `LayoutConfiguration.ConfigDirectory`
   differs across runs on this machine (`~/Library/Application Support/UnoDevelop/config/...` vs
   `~/Library/Application Support/ICSharpCode/SharpDevelop5/...` seen in earlier sessions) - a
   pre-existing environment quirk, not something this pass changed or needed to fix, but worth
   noting for whoever next debugs "why didn't my saved layout load."

   Verified: `SharpDevelop.csproj` builds clean. Runtime-verified via the DevFlow UI tree with any
   pre-existing user `config/layouts/Default.xml` temporarily moved aside (simulating a fresh
   install): the app now loads `data/layouts/Default.xml` with **no exception** (previously fatal
   to the layout-loading step, shown via `MessageService.ShowException`), and the "Projects" pane's
   actual rendered `AvalonDock.Controls.LayoutAnchorablePaneControl` width is **280px** - the exact
   `DockWidth` value from the regenerated XML, itself matching the `PreferredDockSize` wired earlier
   in this pass - confirming the full chain (XML → `XmlLayoutSerializer` → AvalonDock →
   on-screen pixels) now actually runs end-to-end instead of being a no-op.
5. Implement named layouts (`Default`, `Debug`, `Plain`, `ILSpy`) as DTO templates. Switching a
   layout applies placement/visibility to existing models without reconstructing services or
   losing open documents. The ILSpy template shows Assemblies left, Search/Analyze right or bottom,
   and shares the central document area with source editors.
   - **`ILSpy` is a first-class, formally-named layout, not a demo/debug template.** It is
     contributed to OpenDevelop by the ILSpy AddIn itself (as an AddIn-owned layout template
     registration), not hard-coded into the shell's layout list alongside `Default`/`Debug`/`Plain`.
     This means the layout DTO/registry needs a new extension point — e.g. an
     `ILayoutTemplateProvider` (or an AddInTree `/OpenDevelop/Workbench/LayoutTemplates` path) that
     AddIns implement to register named layout templates at startup, mirroring how
     `IToolPaneProvider`/`IDocumentPaneFactory` let AddIns register panes/documents. The shell owns
     the layout DTO format and switching mechanism; AddIns own which named layouts exist.
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

### Composition boundary (updated 2026-08-02)

Two decisions now stand side by side and must not be conflated:

1. **Unification path, not a docking gate.** Do not require all OpenDevelop AddIns to adopt
   ILSpy's container before docking work proceeds. Introduce a small shell-facing registration API
   (`IToolPaneProvider`, `IDocumentPaneFactory`, theme resource provider and command provider).
   Adapt both the existing `Microsoft.VisualStudio.Composition` exports and ILSpy's TomsToolbox
   export provider into that API, so both containers can feed one pane/document registry starting
   in Phase 1. Finding this convergence point is required work, but it is explicitly **not** a
   prerequisite for the pane-model/workspace/layout phases above — those proceed against the small
   registration API regardless of which container(s) are still live underneath.
2. **TomsToolbox composition is the target for OpenDevelop as a whole.** Unlike the earlier
   "decide later" framing, the container-replacement direction is now settled: OpenDevelop should
   migrate its own composition host from `Microsoft.VisualStudio.Composition` to TomsToolbox
   composition (the same container ILSpy already uses), rather than the reverse or a permanent
   dual-container split. This is a larger, separate migration from the docking/pane-model work
   above and should be scoped and sequenced on its own track — it is not blocked on, and does not
   block, Phases 0-5. Concretely: the registration API in point 1 should be designed so that
   swapping the underlying container from `Microsoft.VisualStudio.Composition` to TomsToolbox is an
   implementation change behind the API, not a rewrite of pane/AddIn registration call sites.

This corrects the earlier plan that made composition replacement the first step (still wrong) while
also correcting the earlier "maybe TomsToolbox, maybe not" framing (now decided).

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

**Status (2026-08-02): link-manifest audit done, categorized, build-verified.**
`ILSpyAddIn.csproj` now carries a `Label="ILSpyLinkedModel"`/`"ILSpyLinkedView"` ItemGroup
structure with an inline manifest comment classifying every linked directory into Category 1
("Direct link"), Category 2 ("Shared-shell extraction candidate"), or Category 3
("Reference only" target, currently kept linked because Category-1 code transitively depends on
it). Findings:

- The large majority of the linked surface (Analyzers, AssemblyTree, AvalonEdit, Languages,
  Metadata, Options, Search, TextView, TreeNodes, Util, most of Commands/Controls/Views) is clean
  Category 1 with no OpenDevelop duplicate and no app-shell ownership.
- `Docking/**` (ILSpy's own `DockWorkspace`), `Updates/**`, and
  `AppEnv/{SingleInstance,CommandLineArguments,CommandLineTools}.cs` are Category 3 by the policy
  above, but a `grep` sweep of the whole ILSpy checkout (2026-08-02) found every reference to these
  types originates from an already-linked Category-1 file (`AssemblyTreeModel.cs`,
  `Options/MiscSettingsViewModel.cs`, `Search/SearchPane.xaml.cs`,
  `Commands/ScopeSearchTo*.cs`, `ILSpySettingsFilePathProvider.cs`) - none are dead weight pulled
  in only by excluded app-shell code. Excluding them now would break the build; their removal
  stays scheduled under Phase 4 (Docking - dummy `TabPageModel` removal) rather than Phase 0.
- `Controls/TreeView/**` (SharpTreeView) remains the known Category-3 duplicate; the swap to
  OpenDevelop's own `ICSharpCode.TreeView` is unstarted and out of scope for this pass (tracked
  separately, not folded into Phase 0).
- `Themes/{ResourceKeys,ThemeManager,SyntaxColor}.cs` and `ViewModels/{PaneModel,ToolPaneModel,
  Pane}.cs` are flagged as Category 2 (their OpenDevelop equivalents already exist at
  `src/Main/SharpDevelop/ViewModels/{PaneModel,ToolPaneModel,ObservableObjectBase}.cs`), left
  linked as-is pending the Phase 1/Phase 3 work that actually unifies the two sides.
- Added a `VerifyILSpyLinkedDirsExist` MSBuild target (`BeforeTargets="BeforeBuild"`) that fails
  the build if any manifest-listed top-level directory disappears from the ILSpy submodule -
  MSBuild globs otherwise match zero files silently, which would let a submodule bump quietly stop
  linking a whole category without any build error.
- `dotnet build ILSpyAddIn.csproj` verified green (0 errors) both before and after the manifest
  refactor, on this machine - the doc's earlier "CoffHeaderTreeNode.cs DataTemplateSelector" build
  blocker note no longer reproduces here (LibreWPF gap may already be fixed, or environment
  differs); left as still-linked-and-fine rather than re-investigated in this pass.

Not done in this pass (deliberately deferred to their own next actions): the SharpTreeView swap,
and the smoke-test suite for opening an assembly / rendering the three pane contents / switching
layouts (still blocked on the empty-pane content-area bug documented above).

**Stale as of this Phase 0 pass - both items above were completed later in the same session, see:**

- The SharpTreeView swap: done, but in the *opposite* direction than sketched here (port ILSpy's
  features into OpenDevelop's tree, category 2) - the actual approach taken was to adopt ILSpy's
  tree wholesale project-wide (see "Resolved" under "Open design question" above, and the
  `ICSharpCode.ILSpyX.TreeView` extraction under "Immediate next actions" #2/#3 near the end of
  this document).
- The smoke-test suite: done - `tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs` now covers
  opening an assembly, all three real pane contents rendering (not just tab headers), and
  switching to the ILSpy layout activating those panes (see the "Immediate next actions" #3/#4
  status updates and the "Verification matrix" section near the end of this document). The
  "empty-pane content-area bug" that blocked this is also fixed (see "AvalonDock 5 unification"
  and "Root cause: linked theme resource names" above).

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
- Add the `ILayoutTemplateProvider` (or equivalent AddInTree path) extension point so AddIns can
  register named layout templates; register built-in `Default`/`Debug`/`Plain` through it too, for
  one registration path rather than a shell-hardcoded list plus an AddIn-contributed exception.
- Make `Default`, `Debug`, `Plain`, and `ILSpy` use the same service, with `ILSpy` contributed by
  the ILSpy AddIn through the new extension point.
- Preserve open documents and service instances across layout switches.
- Remove runtime pane re-registration/reflection activation workarounds.

Exit: clean-profile and restored-profile tests produce the same pane groups; corrupt/unknown pane
entries degrade safely; ILSpy layout is a first-class, AddIn-contributed named layout.

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

**Done (2026-08-02):** `tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs`'s
`OpenAssembly_ShowsIlSpyPadsWithRealContent` previously only asserted tab headers ("Assemblies"/
"Decompiled Code" text) plus the DevFlow-reported `loadedAssemblies`/`decompiledTextLength` JSON,
with a comment explicitly noting the assembly tree's content area "does not render in this host's
visual tree" and "is never walkable" - written to match the bug that existed at the time. Now that
the theme-loading bug is fixed (see "AvalonDock 5 unification" and "Root cause: linked theme
resource names" above), strengthened the test to assert on the actual UI tree: at least one real
`ICSharpCode.ILSpy.Controls.TreeView.SharpTreeNodeView` element with non-zero width/height must be
present, not just tab headers - directly encoding the "empty container, 0 children" regression this
technote worried about into an automated check, replacing the outdated comment that documented it
as a known limitation. `dotnet test ... --filter-query "/*/*/IlSpyAddInTests/*"` passes with the
strengthened assertion.

### Immediate next actions

1. ~~Replace `ILSpyAddIn.csproj` directory globs with the audited explicit link manifest.~~ **Done
   (2026-08-02)** - see Phase 0 status above.
2. ~~Resolve the SharpTreeView duplicate by converging on a shared tree library and prove pane
   contents render.~~ **Done (2026-08-02)**, via the reverse direction (adopt ILSpy's tree
   wholesale, project-wide) - see "Resolved" note under "Open design question" above. Note: this
   proved the *control* renders (`TestTreeView` template end-to-end); the three ILSpy pane contents
   specifically remain unverified at runtime due to the separate, pre-existing ThemeManager
   resource-loading bug also noted there.
3. Add the host-neutral pane/workspace contracts and adapt one built-in pane plus one ILSpy pane as
   a vertical slice.

   **Partial progress (2026-08-02): built-in-pane half done, ILSpy-pane half deliberately
   deferred.** An inventory of the current pane hierarchy found OpenDevelop's own
   `ToolPaneModel`/`PaneModel` (`src/Main/SharpDevelop/ViewModels/`) is *already* the doc's intended
   host-neutral hierarchy going forward (per "OpenDevelop's existing pane hierarchy adapts first"
   above) - `ProjectBrowserViewModel` (Solution Explorer, the only real/live `ToolPaneModel`
   subclass; `LegacyToolPaneModel` exists but has zero constructors anywhere in the repo, so
   adapting it would prove nothing) already derives from it. The concrete, low-risk vertical slice
   done this pass: added `ToolPaneModel.PreferredDockSize`/`PreferredDockSide` (new, additive,
   default-null properties - the doc's target contract's dock-placement hints) and replaced
   `DockWorkspace.AfterInsertAnchorable`'s single `anchorableShown.ContentId == "ProjectBrowser"`
   special case with a generic `pane.PreferredDockSize` read, with `ProjectBrowserViewModel` setting
   it in its constructor instead of being hardcoded into the workspace. `PreferredDockSide` is added
   but not yet consulted anywhere (today's layout comes entirely from persisted AvalonDock XML) -
   scaffolding for Phase 2, not wired behavior yet.

   **ILSpy-pane half done too (2026-08-02, follow-up pass, explicitly confirmed first):**
   `externals/ilspy/ILSpy/Search/SearchPaneModel.cs` now derives directly from
   `ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel` (OpenDevelop's) instead of
   `ICSharpCode.ILSpy.ViewModels.ToolPaneModel` (ILSpy's own) - a genuine edit to the ILSpy
   checkout's existing class declaration line, not an additive file (C# only allows one
   partial-class part to declare the base type, so this couldn't be done the way
   `SharpTreeNode.OpenDevelop.cs` extended `SharpTreeNode` for the tree model). `[ExportToolPane]`
   (contract type `ICSharpCode.ILSpy.ViewModels.ToolPaneModel`) became a plain
   `[Export(typeof(SearchPaneModel))]`, since the only consumer of the "ToolPane" contract
   enumeration is ILSpy's own (unused-by-us) `Docking/DockWorkspace.cs` `ToolPanes` property, and
   `IlSpyWorkspaceHost` already fetches this pane by concrete type
   (`exportProvider.GetExportedValue<SearchPaneModel>()`), not by that contract. `Content = this;`
   was added to its constructor to match what `IlSpyToolPaneAdapter` used to set, so the existing
   `[DataTemplate(typeof(SearchPaneModel))]` view registration (`SearchPane.xaml.cs`) still
   resolves the same way via WPF's implicit DataTemplate lookup.

   `IlSpyWorkspaceHost.cs` now registers `searchPaneModel` directly with
   `DockWorkspaceExtensibility.AddToolPane(...)` instead of wrapping it in `IlSpyToolPaneAdapter` -
   `IlSpyToolPaneAdapter`'s property-mirroring remains in place only for `AssemblyTreeModel`
   (not attempted in this pass - it's 1111 lines with drag/drop, navigation and ILSpy's own
   `Docking.DockWorkspace` coupling already documented above as load-bearing; converting it is a
   much larger, separate unit of work than a small pane).

   **Follow-up (2026-08-02, later in this pass): `AnalyzerTreeViewModel` migrated too.** Same
   mechanical change as `SearchPaneModel` - `[ExportToolPane]` dropped (the bare `[Export]` this
   class already carried covers `IlSpyWorkspaceHost`'s concrete-type lookup), base type changed
   from `ICSharpCode.ILSpy.ViewModels.ToolPaneModel` to
   `ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel`, `Content = this;` added to its constructor
   (it previously had no `Content` at all - `IlSpyToolPaneAdapter` was the one setting it).
   Checked the same categories of external reference before changing: `AnalyzeCommand.cs`'s two
   constructor-injected `AnalyzerTreeViewModel` parameters and `AnalyzerTreeView.xaml.cs`'s
   `[DataTemplate(typeof(AnalyzerTreeViewModel))]` both resolve by concrete type, unaffected;
   `Docking/DockWorkspace.wpf.cs`'s `GetContainer<AnalyzerTreeViewModel>()` is ILSpy's own unused
   `DockWorkspace`, same as before. `IlSpyWorkspaceHost.cs` now registers `analyzerTreeViewModel`
   directly instead of wrapping it - only `AssemblyTreeModel` still goes through
   `IlSpyToolPaneAdapter`. Verified: `dotnet build -t:Rebuild` on `ILSpyAddIn.csproj` and a plain
   build of `SharpDevelop.csproj` both clean (0 errors, only the already-documented harmless
   `NativeMethods` CS0436 duplicate-type warning).

   **`AssemblyTreeModel` migrated too (2026-08-02, medium/large-item follow-up) - the deferral
   above turned out to be more pessimistic than necessary once actually investigated.** The 18
   `DockWorkspace` call sites inside `AssemblyTreeModel.cs`/`.wpf.cs` (decompiler-tab/navigation
   state) were the reason this was deferred longer than the other two panes - but on inspection,
   none of those 18 call sites needed to change at all. `DockWorkspace` was only ever reachable
   through a `protected static` property declared on ILSpy's *own* `PaneModel` base
   (`ViewModels/PaneModel.cs:33`,
   `protected static DockWorkspace DockWorkspace => App.ExportProvider.GetExportedValue<DockWorkspace>();`)
   - switching `AssemblyTreeModel`'s base type away from that hierarchy only removes the *source*
   of that property, not anything about what the 18 call sites do with it. Fix: added an
   equivalent `DockWorkspace` accessor directly on `AssemblyTreeModel` itself
   (`ICSharpCode.ILSpy.Docking.DockWorkspace DockWorkspace => exportProvider.GetExportedValue<...>();`),
   using the `exportProvider` field the class already carries (constructor-injected) instead of
   ILSpy's static `App.ExportProvider` - every one of the 18 sites keeps its exact existing
   behavior, unchanged, because only where the property comes from changed, not what it returns.
   Otherwise the same mechanical change as the other two panes: `[ExportToolPane]` →
   `[Export(typeof(AssemblyTreeModel))]`, base type → `ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel`,
   `Content = this;` added (the ctor already set `Title`/`ContentId`/`IsCloseable`/`ShortcutKey`,
   just never `Content`). `IlSpyWorkspaceHost.cs` now registers `assemblyTreeModel` directly
   (still overriding `Title = "Assemblies"` explicitly, since the ctor's own
   `Title = Resources.Assemblies` is an ILSpy-localized string, not necessarily "Assemblies").

   With all three ILSpy panes migrated, `IlSpyToolPaneAdapter.cs` had zero remaining references
   anywhere in the repo (confirmed by `grep`) and was deleted outright - it was OpenDevelop-authored
   glue, not linked ILSpy source, so this is a plain dead-code removal, not something needing the
   ILSpy-checkout-edit caution used elsewhere in this document.

   Verified: `dotnet build -t:Rebuild` on `ILSpyAddIn.csproj` and a plain build of
   `SharpDevelop.csproj` both clean (0 errors). More importantly, ran the actual
   `IlSpyAddInTests` integration tests (`dotnet test ... --filter-query "/*/*/IlSpyAddInTests/*"`)
   end-to-end after this change - both tests pass, meaning opening a real assembly, the assembly
   tree rendering real non-empty `SharpTreeNodeView` content, decompiling real source, and the
   ILSpy-layout-activates-panes behavior all still work correctly with `AssemblyTreeModel` no
   longer going through the adapter - not just a compile-clean claim.
   Checked before making this change that nothing else depends on `SearchPaneModel` remaining under
   the "ToolPane" MEF contract: `SearchPane.xaml.cs`'s `[DataTemplate(typeof(SearchPaneModel))]` and
   `ScopeSearchToAssembly.cs`/`ScopeSearchToNamespace.cs`'s constructor-injected `SearchPaneModel`
   parameter both resolve by concrete type, unaffected; `ShowSearchCommand.cs`/
   `DockWorkspace.wpf.cs` call into ILSpy's own (unused) `Docking.DockWorkspace.ShowToolPane`/
   `GetContainer<SearchPaneModel>()`, which would now just find no match (a no-op) rather than
   throw - not a regression, since nothing wires that command into a live OpenDevelop menu today.

   Verified: `ILSpyAddIn.csproj` builds clean (`dotnet build -t:Rebuild`, confirmed not a stale-cache
   false positive) with this change. Full runtime verification of ILSpy pane rendering remains
   blocked by the separate, pre-existing `ThemeManager`/`themes/theme.light.xaml` resource-loading
   bug already documented above (`EnsureInitialized()` throws before any pane, including this one,
   gets shown) - not something this pass could fix without going out of scope.

   Verified: `ICSharpCode.SharpDevelop.csproj`, `SharpDevelop.csproj` (the app), and
   `ILSpyAddIn.csproj` all build clean with the new properties. Runtime verification (launching the
   app and checking the Project Browser pane's actual docked width via DevFlow) was attempted but
   inconclusive - the Project Browser pane did not materialize in the DevFlow UI tree at all under a
   freshly-generated layout (consistent with the pane-materialization flakiness this technote
   already documents for runtime-added anchorables), so this was verified by code inspection
   (identical `GridLength` assignment at the identical trigger point as the special case it
   replaces, gated behind a null-check that preserves old behavior for every other pane) rather than
   by an on-screen pixel check.

   **Unrelated bug found and fixed while verifying this at runtime (2026-08-02):**
   `CodeCoverageService`'s static constructor (`src/AddIns/Analysis/CodeCoverage/Project/Src/
   CodeCoverageService.cs`) called `SD.Workbench.ViewOpened += ViewOpened` unconditionally.
   `RegisterCodeCoverageOpenLensProviderCommand.Run()` (registered at `/SharpDevelop/Autostart`,
   part of the separate, already-in-progress OpenLens feature - not this session's work) touches
   `CodeCoverageService.ResultsChanged` during `CoreStartup.RunInitialization()`, before
   `WorkbenchStartup.InitializeWorkbench()` has registered `IWorkbench` - so the static constructor
   threw `ServiceNotFoundException`, and because .NET permanently caches a failed static
   constructor as `TypeInitializationException` on every later access, this then also broke
   `WpfWorkbench.Initialize()`'s menu construction (`ToggleCodeCoverageCommand.IsChecked` →
   `CodeCoverageService.CodeCoverageHighlighted`), fatally crashing every launch that didn't have
   the CodeCoverage AddIn manifest removed. (An initial attempt to fix this by deferring the
   subscription via `SD.MainThread.InvokeAsyncAndForget` did not work: this early in startup,
   `SD.MainThread` resolves to `FakeMessageLoop`, whose `InvokeAsyncAndForget` runs the callback
   synchronously rather than actually deferring it - confirmed by re-hitting the exact same crash
   with a different top stack frame.) Fixed at the root: the static constructor now only subscribes
   to `SD.ProjectService.SolutionOpened` (available at this point) and lazily/idempotently attempts
   the `IWorkbench.ViewOpened` subscription via a new `TryHookViewOpened()` guarded by
   `SD.Services.GetService(typeof(IWorkbench))`, retried from the `CodeCoverageHighlighted`
   getter/setter (touched repeatedly via menu `IsChecked` checks) so it completes once `IWorkbench`
   actually exists. Verified: `CodeCoverage.csproj` and the app build clean, and a fresh launch (via
   the DevFlow-driven verification loop) no longer crashes at this point at all - previously every
   launch with the CodeCoverage AddIn enabled hit this fatal error.
4. Define the versioned layout DTO, add the `ILayoutTemplateProvider` extension point, and encode
   `ILSpy` as an AddIn-contributed template rather than another legacy SharpDevelop XML layout.

   **Partial progress (2026-08-02): extension point done, versioned DTO deliberately deferred.**
   Investigated the actual current state first: `data/layouts/LayoutConfig.xml` already listed
   `Default`/`Debug`/`ILSpy`/`Plain` as four hand-authored rows (the doc's target - AddIn-owned
   naming - was not yet true for `ILSpy`), and `AvalonDockLayout.LoadLayout()`
   (`src/Main/SharpDevelop/Workbench/AvalonDockLayout.cs`) has its actual
   `dockWorkspace.RestoreLayout(fileName)` call **commented out** (a pre-existing `TODO: re-enable
   after migrating legacy pads to MEF ToolPaneModel`) - so no layout, named or not, is actually
   deserialized/applied at runtime today; switching layouts only updates
   `LayoutConfiguration.CurrentLayoutName` and logs. This means the versioned-DTO/pane-placement
   work Phase 2 describes is genuinely greenfield, not a partial rewrite of working code, and
   attempting it now would be exercising a code path nothing currently uses.

   Given that, the reviewable slice actually done: a new `ILayoutTemplateProvider` interface +
   `LayoutTemplateDescriptor` (`src/Main/SharpDevelop/Workbench/ILayoutTemplateProvider.cs`),
   discovered via a new AddInTree path `/SharpDevelop/Workbench/LayoutTemplates` (mirroring the
   existing `ITreeNodeFactory`/`IMSBuildAdditionalLogger` plain-interface extension-point pattern,
   not a `ICommand`-wrapped one) and merged into `LayoutConfiguration.Layouts` from a new
   `LoadAddInContributedLayoutTemplates()` step (a name already present from XML config wins, so
   `Default`/`Debug`/`Plain` stay shell-owned as before). `ILSpyAddIn` now contributes the `ILSpy`
   named layout through this path (`IlSpyLayoutTemplateProvider.cs`) instead of a
   `LayoutConfig.xml` row, reusing the existing `data/layouts/ILSpy.xml` file as the template's
   content unchanged - exactly the "AvalonDock XML as an import format, not the durable contract"
   framing the doc's Phase 2 section already allows. `ChooseLayoutComboBox` needed no changes since
   it already just enumerates `LayoutConfiguration.Layouts`.

   **Gap found and closed (2026-08-02, user-flagged): registering the template as data wasn't
   enough - the AddIn also needed to *activate itself* when its layout is selected.** The first
   version of this change made `ILSpyAddIn` own the fact that the "ILSpy" named layout exists, but
   `ILSpy.xml`'s `assemblyListPane`/`searchPane`/`analyzerPane` anchorables would only actually
   restore if `IlSpyWorkspaceHost` had already registered those panes with `DockWorkspace` -
   which only happened as a side effect of the user separately using `File > Open > Assembly` or
   another `od.ilspy.*` action. Selecting "ILSpy" from `ChooseLayoutComboBox` *before* that would
   silently restore nothing for those three anchorables (`DockWorkspace`'s
   `LayoutSerializationCallback` skips any ContentId that isn't registered yet) - the layout would
   look empty, not like "the ILSpy workspace." Added `LayoutTemplateDescriptor.OnActivating`
   (an optional `Action`, null by default) and wired `LayoutConfiguration.CurrentLayoutName`'s
   setter (and `ReloadDefaultLayout()`) to invoke `GetLayout(value)?.onActivating?.Invoke()` before
   `LoadConfiguration()` runs. `IlSpyLayoutTemplateProvider` now passes
   `onActivating: IlSpyWorkspaceHost.EnsureInitialized` - idempotent (guarded by its own
   `initialized` flag), so this is a plain "activate me if I'm not already" hook, not a special
   case. This keeps the "shell owns the mechanism, AddIn owns what happens" split: the shell has no
   ILSpy-specific code, it just calls whatever activation callback the layout's own contributing
   AddIn supplied.

   **Verification gap closed (2026-08-02, same-day follow-up).** The UI-click-through problem
   above (WPF popup items not captured by the DevFlow UI-tree snapshot) and the "every `od.ilspy.*`
   action already initializes ILSpy as a side effect" problem were both solved the same way real
   ILSpy testing elsewhere in this doc solves "no native dialog automation": add a narrow,
   test-only DevFlow seam instead of fighting the UI. Added two small building blocks:
   - `IlSpyWorkspaceHost.IsInitialized` (a plain `bool` property reading the existing `initialized`
     field) and `od.ilspy.is-initialized` (`IlSpyDevFlowActions.cs`) - the one ILSpy status read
     that does NOT itself trigger `EnsureInitialized()`, unlike `status`/`show-pane`/
     `open-assembly`, so a test can tell "the layout switch did this" apart from "some other ILSpy
     action already had."
   - `od.workbench.switch-layout` (`OpenDevelopDevFlowActions.cs`) - drives
     `LayoutConfiguration.CurrentLayoutName` directly (the same setter
     `ChooseLayoutComboBox.cs:105` reaches on a real selection), bypassing the combo box UI
     entirely rather than fighting its popup.

   Verified live via DevFlow before writing the automated test: fresh launch,
   `od.ilspy.is-initialized` → `{"initialized":false}`, then `od.workbench.switch-layout "ILSpy"` →
   `{"found":true,"layoutName":"ILSpy"}`, then `od.ilspy.is-initialized` again →
   `{"initialized":true}` - the layout switch alone, with zero prior ILSpy interaction, activated
   the AddIn. Then added `IlSpyAddInTests.SwitchToIlSpyLayout_ActivatesPanesWithoutPriorIlSpyInteraction`
   encoding the same check as a permanent regression test (the `wasAlreadyInitialized` read is
   informational only, not asserted on, since `OpenDevelopAppFixture`'s app instance is shared
   across the whole "OpenDevelop app" xUnit collection and another test may have already
   initialized ILSpy first depending on run order - what the test does hard-assert is that after
   switching to "ILSpy", the addin ends up initialized and its three panes are present, regardless
   of how that came about). `dotnet test ... --filter-query "/*/*/IlSpyAddInTests/*"` passes both
   tests in the file.

   Explicitly NOT done in this pass (real Phase 2, tracked as still-open): the versioned layout DTO
   itself (pane identity/side/group/order/proportions/floating bounds), re-enabling
   `dockWorkspace.RestoreLayout()` in `AvalonDockLayout.LoadLayout()` (blocked on the legacy-pad
   migration noted above, unrelated to *who registers* a named layout), and switching layouts
   without reconstructing/losing open documents.

   **Bug found and fixed while verifying this at runtime:** the first version of this change added
   an explanatory XML comment inside `data/layouts/LayoutConfig.xml`. `LoadLayoutConfiguration`'s
   parser did `foreach (XmlElement el in doc.DocumentElement.ChildNodes)` - an implicit cast that
   throws `InvalidCastException` on any non-`XmlElement` child node. `XmlDocument` with
   `PreserveWhitespace = false` (the default) already stripped insignificant whitespace text nodes,
   which is why the four bare `<Layout>` rows previously worked with no `OfType<XmlElement>()`
   guard - but XML comments are never treated as insignificant whitespace and are always kept as
   `XmlComment` nodes, so adding one crashed every startup. Fixed both the immediate cause (removed
   the comment from the XML data file - explanatory text belongs in this technote/commit, not in
   parsed config data) and the underlying fragility (`ChildNodes.OfType<XmlElement>()` in
   `LoadLayoutConfiguration`, so a future comment or stray text node in either `LayoutConfig.xml`
   can't reintroduce the same crash). Verified: `SharpDevelop.csproj` and `ILSpyAddIn.csproj` build
   clean, and a fresh launch (DevFlow-driven, as with the earlier verifications in this technote)
   starts up with no fatal error and shows `ChooseLayoutComboBox` in the live UI tree.
5. Replace `IdeThemeService`'s dock-only switch with the semantic application resource contract,
   initially covering Light and Dark.

   **Partial progress (2026-08-02): main-shell-chrome slice done, AvalonEdit/ILSpy explicitly
   deferred.** Confirmed first that `IdeThemeService.Apply()`
   (`src/Main/SharpDevelop/Workbench/IdeThemeService.cs`) really did only ever set
   `DockingManager.Theme` (`Vs2013Light/Dark/BlueTheme`) - nothing else - and that no semantic
   light/dark resource-key pair existed anywhere in the main shell (only per-control
   `themes/generic.xaml` implicit-style dictionaries, a different WPF mechanism). Added
   `src/Main/SharpDevelop/Themes/Theme.{Light,Dark}.xaml` defining six semantic tokens
   (`WindowBackground`, `ToolWindowBackground`, `Border`, `Foreground`, `MutedForeground`,
   `Selection` - a deliberately small starting set, not the full token list the doc's "Full
   application theming" section eventually wants). `IdeThemeService.Apply()` now merges the
   matching dictionary into `Application.Current.Resources.MergedDictionaries` (removing the
   previous one first) in the same call that sets `DockingManager.Theme`, so both change together
   from the one existing call path (`Attach`/`SetTheme` via the Options panel) - no new call sites
   needed. `Blue` maps to the Light semantic dictionary for now (doc only asks for Light/Dark this
   pass).

   Repointed two real elements to prove this end-to-end rather than leaving unused resource keys:
   `WpfWorkbench.xaml`'s main-window `Background` (was a raw `SystemColors.ControlBrushKey`
   binding, OS-theme-only and independent of the app's own IDE theme choice) and
   `AboutDialog.xaml`'s quote-canvas background (was a hardcoded `#F5F5F5` hex literal, one of
   several such literals found in a scan of shell XAML - `NumericUpDown.xaml`,
   `ICSharpCode.Core.Presentation/themes/generic.xaml`, `FontSelector.xaml`,
   `AddServiceReferenceDialog.xaml` have similar hardcoded brushes not touched in this pass).

   Explicitly deferred (real Phase 3, not attempted here): AvalonEdit editor-background/syntax-color
   tokens for the shell's own editor, the rest of the shell's hardcoded-brush inventory, and a
   proper resource-completeness audit/visual-contrast test.

   **ILSpy's own `ThemeManager` bridged too (2026-08-02, follow-up - this was the actual "big
   problem" flagged when asked what remained).** `DecompilerTextView.cs:1457` calls
   `ThemeManager.Current.ApplyHighlightingColors(highlightingDefinition)` on every decompile, and
   `ThemeAwareHighlightingColorizer` reads `ThemeManager.Current.IsDarkTheme` for its fallback text
   color - so ILSpy's decompiled-code syntax highlighting is a real, functional consumer of
   `ThemeManager.Current.Theme`, not a cosmetic detail. Before this fix,
   `IlSpyWorkspaceHost.EnsureInitialized()` seeded it once from ILSpy's own, independently
   persisted `SessionSettings.Theme` and never touched it again - meaning switching OpenDevelop's
   own IDE theme (`IdeThemeService`) had no effect on decompiled code colors at all, two
   unsynchronized theme authorities in the same window.

   Fixed by adding `IdeThemeService.ThemeChanged` (a new `event EventHandler<string>`, raised from
   both `Attach` and `SetTheme` with the theme name that was just applied) to
   `src/Main/SharpDevelop/Workbench/IdeThemeService.cs`, and having
   `IlSpyWorkspaceHost.EnsureInitialized()` seed `ThemeManager.Current.Theme` from
   `IdeThemeService.CurrentTheme` (mapped via a small `ToIlSpyTheme` helper: OpenDevelop's
   `Light`/`Dark` map directly, `Blue` - the one OpenDevelop dock theme with no ILSpy analog -
   falls back to `Light`, consistent with the "Light/Dark only, initially" scope of this whole
   theming slice) instead of ILSpy's own settings, then subscribing to `ThemeChanged` to keep them
   in sync for the rest of the process. This is the shell-owns-the-event/AddIn-owns-its-own-
   reaction pattern already used for the ILSpy-layout-activation fix earlier in this document -
   `IdeThemeService` has no ILSpy-specific code in it at all.

   Verified live via DevFlow, not just compilation - added `od.ilspy.theme` (reads
   `ThemeManager.Current.Theme`/`IsDarkTheme` without side effects) and `od.workbench.set-theme`
   (drives `IdeThemeService.SetTheme` directly, the same DevFlow-bypasses-the-UI pattern as
   `od.workbench.switch-layout`) as small, reusable diagnostic actions rather than one-off
   scaffolding. Sequence: opened an assembly (`od.ilspy.theme` → `{"theme":"Light",
   "isDarkTheme":false}`, matching OpenDevelop's default) → `od.workbench.set-theme "Dark"` →
   `od.ilspy.theme` again → `{"theme":"Dark","isDarkTheme":true}` - the bridge fires correctly.
   Known remaining nuance, not fixed here: `ThemeManager` doesn't itself broadcast a
   re-render notification, so switching themes only affects the *next* decompile, not
   already-open decompiled documents retroactively - matches how `ApplyHighlightingColors` is only
   ever called from the decompile path (`DecompilerTextView.cs`), not from any live-update
   subscription, so this isn't a regression introduced by the bridge, just an existing limitation
   it doesn't newly solve.

   Verified: `SharpDevelop.csproj` and `ICSharpCode.SharpDevelop.csproj` (Base, where
   `AboutDialog.xaml` lives) both build clean. Runtime-verified via the DevFlow UI tree (not just
   compilation): the live `WpfWorkbench` window's actual rendered
   `frameworkProperties.background` reports `#F0F0F0` - exactly `Theme.Light.xaml`'s
   `WindowBackground` color, confirming the `DynamicResource` binding resolves through the new
   dictionary end-to-end rather than falling back to the old `SystemColors` binding.
6. Scope the separate OpenDevelop-wide `Microsoft.VisualStudio.Composition` → TomsToolbox
   composition migration as its own track (see "Composition boundary" above); do not let it block
   or be blocked by Phases 0-5.

   **Scoped (2026-08-02): decision + target + sequencing already recorded above; this closes item 6
   with a concrete first step rather than leaving it an open-ended pointer.** Actually executing the
   migration is deliberately NOT started here - doing so would contradict the whole point of "its
   own track, not folded into Phases 0-5" established earlier in this pass. What's added instead is
   the scoping itself:

   - Re-confirmed (from the earlier "Composition-layer facts" investigation this session) that
     OpenDevelop's own direct use of `Microsoft.VisualStudio.Composition` is narrow: exactly 3 files
     carry `[Export]` (`ILSpyCompositionHost.cs` - ILSpy's own bridge, not OpenDevelop's;
     `DecompiledCodeViewContent.cs` - a comment-only reference; and
     `ProjectBrowserViewModel.cs` - the one real export, `ProjectBrowserViewModel` itself plus its
     `"ToolPane"`-contract `ToolPaneModel`), zero uses of `[ImportingConstructor]`/`[Import]`
     anywhere (no dependency-injection graph to untangle), and only 4 call sites read
     `OpenDevelopMefHost.ExportProvider` directly (`DockWorkspace.cs`'s `ToolPanes` enumeration,
     `ProjectBrowserPad.cs`, and `OpenDevelopDevFlowActions.cs` x2, all via
     `GetExportedValue<ProjectBrowserViewModel>()`). This is the "low-risk/small footprint" claim
     behind the "TomsToolbox is the settled target" decision - not an assumption.
   - Concrete first step for whoever picks up this track: replace `OpenDevelopMefHost.cs`'s
     `Microsoft.VisualStudio.Composition` `ExportProvider`/`ComposableCatalog`/
     `CompositionConfiguration` host with a TomsToolbox `IExportProvider` built over
     `Microsoft.Extensions.DependencyInjection` (mirroring `ILSpyCompositionHost.cs`'s existing
     pattern almost exactly, since ILSpy already does this in the same process), re-attribute
     `ProjectBrowserViewModel` from `[Export(typeof(ProjectBrowserViewModel))]`/
     `[Export("ToolPane", typeof(ToolPaneModel))]`/`[Shared]` (System.Composition-style) to
     TomsToolbox's export attributes, and update the 4 `GetExportedValue`/`GetExportedValues` call
     sites to the TomsToolbox `IExportProvider` API. No `[ImportingConstructor]` graph means no
     constructor-injection call sites to rewire - the entire migration is bounded by those 3 files
     plus 4 call sites, not a sweep across the shell.
   - Once that lands, `IToolPaneProvider`/`IDocumentPaneFactory` (the small registration API named
     in "Composition boundary" above) can be introduced as the one seam both ILSpy's TomsToolbox
     container and OpenDevelop's (now also TomsToolbox) container feed - closing the loop the
     `IlSpyToolPaneAdapter`/`SearchPaneModel` work earlier in this pass already started proving out
     one pane at a time.

   **Done (2026-08-02, executed the same day it was scoped).** `OpenDevelopMefHost.cs` now builds
   its `IExportProvider` via `Microsoft.Extensions.DependencyInjection` +
   `TomsToolbox.Composition.MicrosoftExtensions`'s `BindExports`/`ExportProviderAdapter`, mirroring
   `ILSpyCompositionHost.cs`'s `App.Initialize()` almost line-for-line - both composition
   containers in the process now use the same underlying technology. One correction to the scoping
   above found while actually doing it: `ProjectBrowserViewModel` needed **no attribute changes at
   all** - it already used `System.Composition`'s `[Export(typeof(ProjectBrowserViewModel))]`/
   `[Export("ToolPane", typeof(ToolPaneModel))]`/`[Shared]` (confirmed via its own `using
   System.Composition;`), and `BindExports` scans exactly those attributes (it's the same
   attribute set ILSpy's own `SearchPaneModel`/`AnalyzerTreeViewModel`/`AssemblyTreeModel` already
   carried before their own migrations earlier in this pass) - so the "re-attribute" step in the
   scoping note above turned out to be unnecessary, not just easy. The 4
   `GetExportedValue`/`GetExportedValues` call sites (`DockWorkspace.cs`,
   `OpenDevelopDevFlowActions.cs` x2, `ProjectBrowserPad.cs`) needed zero code changes either -
   `TomsToolbox`'s `IExportProvider` exposes the identical `GetExportedValue<T>()`/
   `GetExportedValues<T>(contractName)` generic-extension-method shape as
   `Microsoft.VisualStudio.Composition.ExportProvider` (confirmed: ILSpy's own linked
   `Docking/DockWorkspace.cs:125` already calls `exportProvider.GetExportedValues<ToolPaneModel>
   ("ToolPane")` against its TomsToolbox provider, the exact same call shape), so the entire
   migration was contained to rewriting one file's internals.

   Verified beyond compilation: launched the app and confirmed via the DevFlow UI tree that the
   "Projects" pane (the one real MEF-exported pad, `DockWorkspace.ToolPanes` →
   `OpenDevelopMefHost.ExportProvider.GetExportedValues<ToolPaneModel>("ToolPane")`) still renders
   with no startup error. Also ran the **full** `OpenDevelop.IntegrationTests` suite (89 tests, not
   just the ILSpy-focused ones) before and after this change to catch any wider regression from
   swapping the whole shell's composition host - 4 tests failed both before and after
   (`DebuggerIntegrationTests.DebugOutput_AfterStart_CapturesDebuggerText`,
   `ErrorListTests.ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors`,
   `UnitTestingTests.UnitTestPad_RendersTestNamesInUiTree`,
   `SearchAndReplaceTests.ShowResults_PopulatesSearchResultsPadUiTree`), confirmed by `git stash`-ing
   just `OpenDevelopMefHost.cs` back to its original `Microsoft.VisualStudio.Composition` form and
   re-running the two spot-checked failures against that baseline, where they failed identically -
   pre-existing environment/timing flakiness unrelated to this migration, not a regression it
   introduced. 85/89 passed with the migration in place.

These actions deliberately establish one model and one source-reuse policy before expanding the
embedded ILSpy surface. That makes later ILSpy updates an input to OpenDevelop's architecture,
rather than allowing linked standalone-shell implementation details to become the architecture by
accident.

### Real versioned layout DTO (2026-08-02/03)

Picked as the next big task after the ILSpy theme bridge (see "AvalonEdit theme bridging" above).
Investigated what `DockWorkspace.RestoreLayout`/`SaveLayout` (`src/Main/SharpDevelop/Workbench/
DockWorkspace.cs`) actually persist today - they wrap AvalonDock's own `XmlLayoutSerializer`
directly, no OpenDevelop-level DTO existed at all. Found three real gaps, not just a missing
version number:

1. `LayoutSerializationCallback` unconditionally cancels every `LayoutDocument` (line ~150) - open
   editor tabs were never part of the persisted layout to begin with. Out of scope for this pass
   (would need a real document-identity model - file path/project item - to round-trip; noted here
   so it isn't rediscovered as a surprise later).
2. **Fixed**: the callback forced `pane.IsVisible = true` unconditionally for every restored
   anchorable, regardless of what was actually saved - a tool pane the user explicitly hid before
   closing the IDE silently reappeared on every restart/layout switch. Now preserves
   `anchorable.IsVisible` (the deserialized value) instead of overwriting it.
3. **Fixed**: "versioning" was `AvalonDockLayout.TryLoadConfiguration` catching
   `FileFormatException` from `XmlLayoutSerializer.Deserialize` and silently falling back to the
   read-only template - indistinguishable from an actual XML parse error, and no way to react
   differently to "this is an old/foreign schema" vs. "this file is corrupt." Added an explicit
   `OpenDevelopLayoutSchemaVersion` attribute stamped onto the `<LayoutRoot>` root element by
   `SaveLayout` (serializes to a `MemoryStream` first, sets the attribute via `XmlDocument`, then
   writes the file) and checked by `RestoreLayout` before deserializing at all - a mismatch now
   throws `FileFormatException` with an explicit `LoggingService.Warn` explaining why, reusing the
   existing template-fallback path deliberately rather than replacing it (there is nothing to
   migrate *from* yet - version 1 is the first version - so "log and fall back to template" is the
   correct behavior for now; a real migration function has an obvious seam to slot into
   `HasCompatibleSchemaVersion`/`RestoreLayout` whenever version 2 exists). The four shipped
   `data/layouts/{Default,Debug,Plain,ILSpy}.xml` templates were stamped with the same attribute so
   they pass the compatibility check unchanged.

Verified live via targeted DevFlow calls (not the full integration suite, per the "focus on big
ILSpy problems" constraint): built `SharpDevelop.csproj` clean, launched the app, called
`od.workbench.switch-layout "ILSpy"` twice (once implicitly via `Default` on startup, once
explicitly), confirmed `od.ilspy.is-initialized` returns `true` and the layout-changed path logs
"Saving layout file" with no `FileFormatException`/template-fallback warning, then read the actual
saved file at `~/Library/Application Support/ICSharpCode/SharpDevelop5/layouts/ILSpy.xml` and
confirmed its root element is `<LayoutRoot OpenDevelopLayoutSchemaVersion="1">` - the round-trip
writes and is accepted back without triggering the new compatibility check's warning path.

### ILSpy layout file moved into the AddIn's own folder (2026-08-03)

User question: since `IlSpyLayoutTemplateProvider` already declares that the ILSpy AddIn owns the
fact that the "ILSpy" named layout exists (see its own header comment above/`ILayoutTemplateProvider`
doc comment), shouldn't the physical template file live inside the AddIn's folder too, instead of
the shell's `data/layouts/`? Yes - fixed:

- Moved `data/layouts/ILSpy.xml` → `src/AddIns/DisplayBindings/ILSpyAddIn/Layouts/ILSpy.xml`, copied
  to the AddIn's own output folder via `<None Include="Layouts\ILSpy.xml"
  CopyToOutputDirectory="Always" />` in `ILSpyAddIn.csproj` (same pattern as `ILSpyAddIn.addin`
  itself just above it).
- `LayoutTemplateDescriptor.TemplateFileName` now accepts either a bare filename (existing
  shell-relative behavior, unused by ILSpy now) or a rooted absolute path -
  `IlSpyLayoutTemplateProvider` resolves one via `Path.GetDirectoryName(Assembly
  .GetExecutingAssembly().Location)` + `"Layouts/ILSpy.xml"`.
- `LayoutConfiguration.LoadAddInContributedLayoutTemplates` splits this into two fields: `fileName`
  (always a bare name, e.g. `"ILSpy.xml"`, used for the per-user saved copy under
  `ConfigLayoutPath`) and a new `templateFilePath` (the rooted path, used only for the initial
  read-only template). This split matters: if the AddIn's absolute path were also used as the save
  target, switching to/from the ILSpy layout would silently overwrite the AddIn's own shipped
  template file on every `StoreConfiguration()` call (the layout is `readOnly: false`) - instead
  user customizations still land in `ConfigLayoutPath/ILSpy.xml`, same as before.
- Net effect: deleting `src/AddIns/DisplayBindings/ILSpyAddIn/` now also removes its layout
  template - nothing orphaned in the shell's own `data/layouts/`, matching the ownership the
  provider already claimed declaratively.

Verified live: build succeeded (`ILSpyAddIn.csproj` + `SharpDevelop.csproj`), confirmed
`Layouts/ILSpy.xml` present under `AddIns/DisplayBindings/Decompiler/` output, and `data/layouts/`
now only contains `Default.xml`/`Debug.xml`/`Plain.xml`/`LayoutConfig.xml`.

### Folded using-block placeholder was an unlabeled "..." (2026-08-03)

User report: a folded `using` block in the decompiled-code view showed nothing useful, so there was
no way to tell what had been collapsed. Traced through AvalonEdit's real rendering code
(`ICSharpCode.AvalonEdit.Folding.FoldingElementGenerator.ConstructElement`,
`src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/Folding/FoldingElementGenerator.cs:137-139`): a
folded region always renders *something* - if `FoldingSection.Title` is null/empty it falls back to
the literal string `"..."` - so this was never truly blank, just an uninformative placeholder
indistinguishable from every other collapsed region (method bodies, folded braces at
`TextTokenWriter.cs:275` use the same default). Root cause: `ICSharpCode.Decompiler.Output.
TextTokenWriter.cs:104` calls `output.MarkFoldStart(defaultCollapsed: !settings
.ExpandUsingDeclarations)` with no `collapsedText` argument for the using-block fold, so it always
got the generic `"..."` default - unlike OpenDevelop's own C# editor, where `CSharpBinding`'s
`FoldingVisitor.AddUsings` already sets `folding.Name = "using...";` for the exact same construct
(`src/AddIns/BackendBindings/CSharpBinding/Project/Src/Parser/FoldingVisitor.cs:75`).

Fixed by passing an explicit `"using ...;"` collapsedText at the `MarkFoldStart` call site (this is
linked ILSpy/Decompiler upstream source, edited in place per the existing "link, don't fork" policy
- see "WPF port prefer linking" precedent). Added a small permanent diagnostic action,
`od.ilspy.foldings` (lists the hosted `DecompilerTextView`'s `FoldingManager.AllFoldings` -
offsets/Title/IsFolded - added to `IlSpyDevFlowActions.cs`), since there is no screenshot capability
available through DevFlow in this environment to visually confirm folding placeholder text.
Verified live: opened `OpenDevelop.dll` into the hosted ILSpy tree via `od.ilspy.open-assembly` +
`od.ilspy.select-node`, then called `od.ilspy.foldings` and confirmed the using-block folding now
reports `{"Title":"using ...;","IsFolded":true}` instead of an empty/default title.

**Open, larger issue raised alongside this** (not addressed in this pass - scoping note only): the
user pointed out that OpenDevelop now has *two* independent ways of displaying/configuring C# text
(its own `AvalonEdit.AddIn`/`CSharpBinding` editor, and ILSpy's own `DecompilerTextView` with its own
`Options/DisplaySettingsPanel` - confirmed real and separate: `externals/ilspy/ILSpy/Options/
DisplaySettings.cs` has its own `SelectedFont`/`SelectedFontSize`/`EnableWordWrap`/
`ExpandMemberDefinitions`/`ExpandUsingDeclarations`/etc., bound directly in `DecompilerTextView.cs`
via `settingsService.DisplaySettings`, entirely independent of whatever font/wrap/folding options
OpenDevelop's own C# editor exposes in its Options dialog). This is exactly the "one model, one
activation path" goal already stated as this document's Phase 1 exit criterion (see "Phased
implementation plan" above: "adapt ILSpy pane exports directly to the common model," "there is one
pane collection and one activation path") - but Phase 1 as scoped so far only covers *panes*
(tool windows), not the *document/text-editing* stack. Folding this in would mean either (a)
routing `DecompilerTextView`'s settings through OpenDevelop's own text-editor options service
instead of ILSpy's private `DisplaySettings` singleton, or (b) going further and making decompiled
code just another OpenDevelop editor document (reusing `AvalonEdit.AddIn`'s existing font/wrap/
folding options wholesale) instead of ILSpy's own bespoke `DecompilerTextView` control. (b) is the
bigger, more invasive move - it would also have to reconcile `DecompilerTextView`'s ILSpy-specific
behavior (reference hyperlinks, `AvalonEditTextOutput`'s incremental fold/UI-element writing,
`NavigateToReferenceEventArgs` handling) with OpenDevelop's generic text editor, which currently
knows nothing about any of that. Flagging this as the next architecture decision to make rather than
executing either option speculatively.

### Decision: option (b) - decompiled code becomes a normal OpenDevelop document (2026-08-03)

User decided: go with (b), not (a) - decompiled code should become just another OpenDevelop editor
document instead of pointing ILSpy's own `DecompilerTextView`/`DisplaySettings` at OpenDevelop's
options.

**Research finding (research-only pass, no edits): this is ~90% already built, just not wired up
for the tree-driven "Assemblies" pane.** There are two parallel, independent decompiled-code
integrations in this codebase already:

1. **The already-complete native path** - `ilspy://` is a real, registered OpenDevelop `FileName`
   URI scheme (`DecompiledTypeReference.ToFileName()`/`FromFileName()`,
   `ILSpyDecompilerService.cs`), with its own `Parser` (`ILSpyParser.cs`,
   `ILSpyAddIn.addin`'s `supportedfilenamepattern="^ilspy://"`) and `DisplayBinding`
   (`ILSpyDisplayBinding.cs`) registered in the AddIn manifest. `DecompiledViewContent.cs` hosts a
   plain `CodeEditor` (OpenDevelop's own AvalonEdit-based editor, the exact same one real `.cs`
   files use), sets it read-only, applies C# syntax highlighting, and decompiles via
   `ILSpyDecompilerService.DecompileType` - i.e. it already is "decompiled code as a normal
   OpenDevelop document." `NavigateToDecompiledEntityService.NavigateTo` (used by go-to-definition)
   already opens/reuses these documents through the ordinary `SD.Workbench.ShowView` pipeline, not
   through `IlSpyWorkspaceHost`.
2. **The still-bespoke path** - `IlSpyWorkspaceHost.cs`'s tree-driven "Assemblies" pane /
   `AssemblyTreeSelectionChangedEventArgs` handler decompiles straight into a dedicated, shared
   `DecompilerTextView` (real ILSpy's own text view + `DisplaySettings`), hosted as a single
   `DecompiledCodeViewContent` document tab - this is the path everything built so far in this
   technote (theme bridging, folding fix, etc.) has been improving.

So the actual gap for (b) isn't "build a native document type" (already exists) - it's "make the
tree-driven pane use path 1 instead of path 2."

**Attempted this pass, found a real blocker, reverted before it could regress anything:**

Added `IlSpyWorkspaceHost.OnSelectionChangedAsync` (not wired to the `MessageBus`
subscriber - see the method's own comment): for a single selected `TypeTreeNode`, resolve its
top-level reflection name (`TypeTreeNode.TypeDefinition.FullTypeName.TopLevelTypeName
.ReflectionName`) and assembly path (`TypeTreeNode.ParentAssemblyNode.LoadedAssembly.FileName`),
then call `NavigateToDecompiledEntityService.NavigateTo` directly - the exact same call go-to-
definition already makes. Added a permanent DevFlow diagnostic, `od.ilspy.navigate-to-type`
(`IlSpyDevFlowActions.cs`), since there's no screenshot capability in this environment.

Verified live: the native document plumbing itself works correctly - `od.ilspy.navigate-to-type`
confirmed a `DecompiledViewContent` gets created/reused with the correct `ilspy://...cs` identity,
correct `[TypeName]` title, `IsReadOnly: true`, and becomes the active view (needed one extra poll
to settle, matching the same async-activation timing seen elsewhere in this doc).

**But decompilation itself failed** for a real assembly (`ILSpyAddIn.dll`, which has external
framework references) with:

```
ICSharpCode.Decompiler.Metadata.ResolutionException: Failed to resolve assembly:
'System.Runtime, Version=10.0.0.0, ...'
   at ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver.ResolveInternal(...)
```

Root cause: `ILSpyDecompilerService.DecompileType` (`ILSpyDecompilerService.cs:86`) constructs a
brand-new `CSharpDecompiler(name.AssemblyFile, settings)` from just a file path, which builds its
own `UniversalAssemblyResolver` from scratch with no search-path/reference context - unlike the
tree-driven path, which decompiles through the already-loaded `LoadedAssembly`/`AssemblyList`
(which already resolved all references when the assembly was opened) and therefore doesn't hit
this. This isn't a corner case - it's the common case for anything beyond a self-contained
assembly with no external dependencies, which is why this needed to be caught *before* wiring it
up rather than after: flipping the `MessageBus` subscriber over to `OnSelectionChangedAsync` today
would make selecting almost any real type in the tree show a decompiler error instead of code, a
real regression on the primary browsing workflow this whole technote has been hardening.

**Reverted the wiring, kept the code**: `OnSelectionChangedAsync` exists in `IlSpyWorkspaceHost.cs`
but the `MessageBus<AssemblyTreeSelectionChangedEventArgs>` subscriber still calls
`RefreshDecompiledViewAsync()` (the old, working, bespoke-pane path) directly, unchanged from
before this pass. `od.ilspy.navigate-to-type` stays as a working diagnostic/regression check for
whoever picks this back up.

**Concrete next steps for finishing (b)**, in dependency order:

1. Fix `ILSpyDecompilerService.DecompileType`'s resolver gap - likely means passing it an
   `AssemblyList`/resolver context sourced from the already-loaded assembly (mirroring what
   `DecompilerTextView.DecompileAsync`'s language/decompiler setup does) instead of constructing a
   bare `CSharpDecompiler` from a file path alone.
2. Once (1) holds, wire the `MessageBus` subscriber to call `OnSelectionChangedAsync` (already
   written) instead of `RefreshDecompiledViewAsync` directly, at least for the single-`TypeTreeNode`
   case.
3. Extend coverage to whole-module selection (`AssemblyTreeNode` → `DecompiledTypeReference
   .IsWholeModule`, already supported by `DecompiledTypeReference`/`ILSpyDecompilerService` - just
   needs a second branch in `OnSelectionChangedAsync`) and multi-node selection (harder - no
   existing native-document equivalent for "decompile N arbitrary nodes at once").
4. Reference hyperlink navigation (click a type/member inside decompiled code to jump) has no
   native-editor equivalent yet - would need a small AvalonEdit `VisualLineElementGenerator` +
   click handler mapping into `NavigateToDecompiledEntityService.NavigateTo`, replacing ILSpy's own
   `ReferenceElementGenerator`/`JumpToReference` mechanism used only by the bespoke path today.
5. Only once all tree-selection cases are covered by path 1 should `DecompiledCodeViewContent`/the
   shared `DecompilerTextView` singleton actually be removed - until then it stays as the fallback
   for whatever `OnSelectionChangedAsync` doesn't yet handle.

### Step 1 done: resolver gap fixed, single-TypeTreeNode routing now wired up (2026-08-03)

Followed the plan above. Fixed `ILSpyDecompilerService.DecompileType`'s reference-resolution gap
(`ILSpyDecompilerService.cs`, new `CreateDecompiler` helper): when the target assembly is already
loaded in the hosted `AssemblyList` (always true for anything reached through the tree), reuse its
`LoadedAssembly.GetMetadataFileOrNull()` + `GetAssemblyResolver()` instead of building a bare
`CSharpDecompiler(fileName, settings)` with no framework/search-path context - falls back to the
old bare constructor only when the file isn't loaded there (standalone usage outside the
tree-hosted workflow, unchanged from before). Then wired `IlSpyWorkspaceHost`'s
`MessageBus<AssemblyTreeSelectionChangedEventArgs>` subscriber to call `OnSelectionChangedAsync`
(previously written but deliberately left unreferenced pending this fix) instead of
`RefreshDecompiledViewAsync` directly.

Verified live: `od.ilspy.navigate-to-type` against `ICSharpCode.ILSpyAddIn.DecompiledTypeReference`
(a real type with real external dependencies - `System.Linq`, `ICSharpCode.Core`,
`ICSharpCode.SharpDevelop`, ...) now decompiles cleanly through the native `DecompiledViewContent`
document - no `ResolutionException`, correct C# output. The single-`TypeTreeNode`-selection path is
now live end-to-end for the realistic case.

**Bonus find while verifying, fixed in passing**: decompiling a different type
(`IlSpyWorkspaceHost`) surfaced `System.PlatformNotSupportedException: COM Interop is not supported
on this platform` from `SDTraceListener.Fail` - completely unrelated to decompiling or this
migration. Root cause: `SDTraceListener.Fail` (`src/Main/SharpDevelop/Logging/SDTraceListener.cs`,
Debug-build-only via `[Conditional("DEBUG")]`) unconditionally calls `thread.SetApartmentState
(ApartmentState.STA)` before showing its WPF assertion dialog - STA depends on COM, which throws on
any non-Windows platform, so *any* `Debug.Assert`/`Trace.Fail` firing anywhere in a Debug build on
macOS crashed with this instead of ever showing the dialog. Fixed by gating the call behind
`OperatingSystem.IsWindows()` - the WPF `MessageBox.Show` below it has no real STA dependency on
this host. Verified live: after the fix, decompiling `IlSpyWorkspaceHost` correctly surfaced the
*real* underlying issue instead - a genuine, separate, pre-existing upstream ILSpy decompiler bug
(`ICSharpCode.Decompiler.CSharp.SequencePointBuilder.EndSequencePoint`: `Debug.Assert` failure,
"missing startLocation", while generating debug sequence points for that type's lambda-heavy code)
now correctly shows its assertion dialog (Yes=Debug/No=Ignore/Cancel=Ignore All) rather than
crashing the whole decompile silently. That `SequencePointBuilder` bug itself is out of scope here -
it's upstream ILSpy decompiler behavior unrelated to hosting/document routing, and would have hit
identically via the old bespoke `DecompilerTextView` path once triggered.

> **WRONG - see "Root cause of the \"missing startLocation\" assert storm" near the end of this
> document.** It was not an upstream bug and not out of scope: `ILSpyDecompilerService` was calling
> `CreateSequencePoints` on an AST whose locations had never been populated. Fixed 2026-08-03.

### Step 3 (whole-module) is NOT the trivial follow-up it looked like (2026-08-03)

Went to extend `OnSelectionChangedAsync` to also route single `AssemblyTreeNode` selection (whole-
module decompile) through the native path, since `DecompiledTypeReference.IsWholeModule` already
exists and looked like a two-line change. Added the missing piece -
`NavigateToDecompiledEntityService.NavigateToModule(FileName)` (and refactored the shared
reuse-lookup/`ShowView` logic out of the existing `NavigateTo` into a private helper both overloads
now share) - but stopped short of wiring it into `OnSelectionChangedAsync`, because doing so would
silently break already-tested behavior:

- **Opening an assembly selects its `AssemblyTreeNode`** - i.e. the exact case this step would
  route natively. `IlSpyWorkspaceHost.OpenAssemblyAsync` (`IlSpyWorkspaceHost.cs:290-294`) awaits
  `lastDecompile` and, on cancellation, polls `decompilerTextView.textEditor.Text` specifically -
  the *bespoke* pane's text - as its readiness signal. Routing whole-module selection natively
  would leave that field permanently empty for this flow.
- **`tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs`'s `OpenAssembly_ShowsIlSpyPadsWithRealContent`**
  asserts `decompiledTextLength > 0` and inspects `decompiledTextSnippet` from `od.ilspy.status`
  (`IlSpyDevFlowActions.GetStatus`), which reads that same `decompilerTextView.textEditor.Text`
  field. This is a real, existing, presumably-passing test that would start failing.

So step 3 needs `OpenAssemblyAsync`'s readiness-wait and `od.ilspy.status`'s status reporting
updated to also account for the native-document case *before* whole-module routing can be safely
flipped on - it's coupled to already-tested surface area the single-`TypeTreeNode` case (step 2)
never touched (opening an assembly never selects a `TypeTreeNode` directly). `NavigateToModule`
itself is done and ready to use once that coupling is resolved; just not wired up yet. Correcting
the "trivial follow-up" characterization from the earlier plan - it's a real, if small, second
migration, not a one-line follow-on to step 2.

### Step 3 done: whole-module routing wired up, resolved the coupling correctly (2026-08-03)

Resolved the coupling identified above rather than working around it:

- **`DecompiledViewContent.InitializeView`** changed from `async void` to `async Task`, exposed as
  a new public `DecompilationTask` property. Previously there was no way for any caller to actually
  await a native document's decompile completing (it was pure fire-and-forget) - this was a latent
  gap even in the already-shipped single-`TypeTreeNode` case from step 2, not something step 3
  introduced.
- **`NavigateToDecompiledEntityService.NavigateTo`/`NavigateToModule`** now return that
  `DecompilationTask` (the existing-document's if reused, a fresh one if just created) instead of
  `void`. Checked all call sites first (`ILSpyDisplayBinding.cs`, `IlSpyDevFlowActions.cs`,
  `IlSpyWorkspaceHost.cs`) - none awaited the old `void` return, so this is a non-breaking signature
  change.
- **`IlSpyWorkspaceHost.OnSelectionChangedAsync`** now `return`s that task for both the
  single-`TypeTreeNode` and new single-`AssemblyTreeNode` branches, so `lastDecompile` - what
  `OpenAssemblyAsync` awaits - now means "decompile actually finished" for the native path too,
  not just "ShowView returned."
- **`od.ilspy.status`** (`IlSpyDevFlowActions.GetStatus`) now reads decompiled text from the active
  view content when it's a native `DecompiledViewContent`, falling back to the bespoke
  `DecompilerTextView` otherwise - keeps `decompiledTextLength > 0`-style assertions meaningful
  regardless of which path handled the current selection, without needing to touch the test file
  itself.

**Found and fixed one more real, previously-unreachable bug while wiring this up**:
`DecompiledViewContent`'s constructor unconditionally computed its title via
`ReflectionHelper.SplitTypeParameterCountFromReflectionName(typeName.Type.Name)` - `Type.Name` is
`null` for a whole-module `DecompiledTypeReference` (`IsWholeModule`), so this threw
`NullReferenceException` the moment `NavigateToModule` (the first caller ever to actually construct
a whole-module `DecompiledViewContent`) was exercised. Fixed with an explicit `IsWholeModule` check,
title becomes `"[Module]"`.

Verified live: `od.ilspy.open-assembly` (which selects the assembly's own `AssemblyTreeNode`) now
opens a native `[Module]` document (confirmed via app log: `ActiveWorkbenchWindowChanged to
[AvalonWorkbenchWindow: [Module]]`, no crash) instead of the bespoke pane. Decompiling the *whole*
module of a real multi-hundred-KB assembly surfaced yet another separate, pre-existing upstream
ILSpy decompiler assertion (`ICSharpCode.Decompiler.TypeSystem.Implementation
.NullabilityAnnotatedType..ctor`, via the same `SDTraceListener.Fail` dialog fixed above) -
unrelated to this work, same category as the `SequencePointBuilder` one found earlier, and expected
to occur more often on whole-module decompiles than single-type ones simply because there's more
code to hit an edge case in.

> **Treat the "unrelated / upstream" claim here as UNVERIFIED.** The `SequencePointBuilder` assert it
> was grouped with turned out to be our own bug (see the root-cause entry near the end of this
> document), so that grouping is not evidence of anything. `NullabilityAnnotatedType..ctor`'s assert
> has not been re-investigated since; it surfaced only while whole-module selection was briefly
> routed through `ILSpyDecompilerService`, so it could equally be another consequence of this
> service's pipeline rather than upstream behavior. Do not assume either way without checking.

### Multi-select and reference hyperlink navigation: still out of scope, on purpose

> **Both since done - see "Reference hyperlink navigation - implemented earlier, now actually
> verified" and "Multi-select decompilation - the last item with zero code either way" further
> down this document (2026-08-03).** Left below for the historical reasoning, which is still
> accurate as of when it was written.

Per the plan's steps 4-5, these were not attempted this batch:

- **Multi-node selection** (several tree nodes selected at once) has no native-document equivalent
  at all - `DecompiledViewContent`/`DecompiledTypeReference` model exactly one type or one whole
  module, never "these N arbitrary nodes decompiled together" (which is what ILSpy's own
  `DecompilerTextView.DecompileAsync(Language, IEnumerable<ILSpyTreeNode>, ...)` supports natively).
  Building that would mean either a new native-document type or extending
  `DecompiledTypeReference` to represent a node-set, and there's no existing partial groundwork to
  build on the way there was for the single-type/whole-module cases - a real, separate feature, not
  a follow-up.
- **Reference hyperlink navigation** (click a type/member inside decompiled code to jump to it) has
  no AvalonEdit-based equivalent in OpenDevelop's own editor - it would need a new
  `VisualLineElementGenerator` + click handler mapping into
  `NavigateToDecompiledEntityService.NavigateTo`, replacing ILSpy's own
  `ReferenceElementGenerator`/`JumpToReference` (used only by the bespoke path, which still exists
  as the fallback for exactly these two cases). No code exists for this yet in either direction.

`OnSelectionChangedAsync` now correctly falls back to `RefreshDecompiledViewAsync` (the bespoke
pane) for both of these, so neither is a regression - just not yet migrated, same as before this
pass, now with the "why" (and "what specifically is missing") spelled out precisely instead of
gestured at.

### Correction: step 3 (whole-module routing) reverted again - broke an existing test (2026-08-03)

The "done" write-up above for step 3 was wrong to ship without checking
`tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs` first. That test's
`OpenAssembly_ShowsIlSpyPadsWithRealContent` explicitly asserts, after opening an assembly (which
selects its `AssemblyTreeNode` - exactly the case step 3 routed natively):

- the active view's type name is `ICSharpCode.ILSpyAddIn.DecompiledCodeViewContent` (the bespoke
  pane), not `DecompiledViewContent` (the native document) - `od.active-view`'s `typeName` would
  now read `DecompiledViewContent` instead;
- a "Decompiled Code" tab renders in the UI tree - the native document's title is `"[Module]"`
  (from this pass's own fix), not `"Decompiled Code"`.

Both assertions would have failed had this shipped. Reverted `OnSelectionChangedAsync`'s
`AssemblyTreeNode` branch back to `RefreshDecompiledViewAsync` (the single-`TypeTreeNode` branch is
untouched and still safe - opening an assembly never selects a type node directly, so this test
never exercised that path). Ran the two `IlSpyAddInTests` tests specifically (not the full 89-test
suite) to confirm: both pass with the revert in place.

**What's still real and kept**: `NavigateToDecompiledEntityService.NavigateToModule`, the
`DecompilationTask` plumbing, and the whole-module `DecompiledViewContent` title fix are all still
in place and correct - just not reachable through tree selection. `od.ilspy.navigate-to-type`-style
direct exercising still works. Whole-module tree-selection routing is back to "not yet done," same
status as multi-select/hyperlink-nav above, now for the same class of reason (existing pinned
behavior, not a technical blocker) rather than the resolver gap from earlier.

**Lesson for whoever picks this back up**: check `IlSpyAddInTests.cs`'s exact assertions (active
view type name, expected tab title) *before* wiring any further tree-selection case into the native
path - the single-`TypeTreeNode` case (step 2, still live) happened to be safe only because nothing
in that test file ever selects a type node directly, not because the test was checked and found
compatible.

### Root cause of the "missing startLocation" assert storm - it was OUR bug (2026-08-03)

**Correction to the two earlier entries above** that called this "a genuine, separate, pre-existing
upstream ILSpy decompiler bug ... out of scope here." That was wrong. It was a defect in
`ILSpyDecompilerService`, and it is now fixed.

`CSharpDecompiler.CreateSequencePoints(syntaxTree)` reads `node.StartLocation`/`EndLocation` off the
AST. A *decompiled* AST is synthesized, never parsed, so every node's location is
`TextLocation.Empty` until the tree has been rendered once through a token writer wrapped in
`TokenWriter.WrapInWriterThatSetsLocationsInAST` (`ITokenWriter.cs:82` → an
`InsertMissingTokensDecorator`, which populates locations - and inserts the implicit tokens - as it
writes). Both upstream callers of `CreateSequencePoints` do exactly that render-then-compute
sequence:

- `ICSharpCode.Decompiler/DebugInfo/PortablePdbWriter.cs`: `SyntaxTreeToString` (which wraps, line
  ~390) at line ~123, *then* `CreateSequencePoints` at line ~126.
- `ILSpy/Languages/CSharpILMixedLanguage.cs`: `WriteCode` (which wraps, line ~79) *then*
  `CreateSequencePoints` at line ~105.

`ILSpyDecompilerService.DecompileType` did neither - it called `CreateDebugSymbols` →
`CreateSequencePoints` on the raw tree straight out of `DecompileType`/
`DecompileWholeModuleAsSingleFile`. Hence `Debug.Assert(!startLocation.IsEmpty, "missing
startLocation")` (`SequencePointBuilder.cs:418`) firing for essentially every statement. Compounding
it, `CreateDebugSymbols(...)` sat in an *argument position* of the `WriteSyntaxTree(...)` call, so
C#'s left-to-right argument evaluation ran it *before* that method's own render pass - no incidental
location side effect could ever have helped.

This was never cosmetic: `Debug.Assert` execution *continues* after the listener returns, so the
sequence points were still being produced - from empty locations. `ILSpySymbolSource.cs` feeds those
to the debugger for stepping into decompiled code, so line mappings there were silently wrong.

**Fix**, in `ILSpyDecompilerService.DecompileType` + new `SetLocationsInAst` helper: render display
text from the pristine tree first, *then* run the location pass, *then* compute sequence points
(`DecompiledTypeResult.WithDebugSymbols` carries the result forward).

**Order matters, and not in the obvious way** - measured, not assumed. `InsertMissingTokensDecorator`
*mutates* the AST, and that mutation shows up in anything rendered afterwards: doing the location
pass before the display render moved a comment out of an attribute's argument list
(`DebuggerBrowsable(/*Could not decode attribute arguments.*/)` →
`/*Could not decode attribute arguments.*/DebuggerBrowsable()`) and changed output length 5104 →
5095. That is upstream's *mixed IL/C#* rendering behavior (`CSharpILMixedLanguage.WriteCode` wraps,
so it displays the mutated tree) but NOT upstream's plain C# view (`CSharpLanguage.WriteCode` does
not wrap) - and this document is the latter. Rendering first restores byte-identical output.

Verified live, all four at once, on `ICSharpCode.ILSpyAddIn.DecompiledTypeReference` (a type with
real external dependencies): `missing startLocation` occurrences in the app log went **23 → 0**;
output length back to **5104**, the exact pristine value from before any of this pass's changes, with
the attribute comment back in its original position; `debugSymbols` now **10 methods / 67 sequence
points** (i.e. real mappings, where before they were built from empty locations); and the two
`IlSpyAddInTests` still pass.

**Known remaining fidelity gap (not fixed, deliberately):** both upstream `WriteCode`
implementations run `syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true })`
before rendering; `ILSpyDecompilerService` never has. So our decompiled output can lack the
readability parentheses real ILSpy shows. Fixing it is a one-liner but *changes output text*, so it
is left as its own deliberate change rather than bundled into a root-cause fix.

### The assert dialog itself was also a real (separate) bug: it deadlocked the IDE

Independently of the above: `SDTraceListener.Fail` spun up a dialog thread and `thread.Join()`ed it -
a hard block until a human clicked a button. In a codebase that links large amounts of third-party
source full of `Debug.Assert` calls (ILSpy's decompiler especially), every such assert froze the
entire IDE on the UI thread; and because DevFlow actions dispatch to the UI thread, it deadlocked all
automation and integration tests too, producing no output at all and making unrelated work
impossible to verify. Their diagnostic value does not justify halting the process.

`Fail` now dedupes by stack trace, writes the assert to the log via `LoggingService.Warn`, and
continues. Set `OPENDEVELOP_ASSERT_DIALOG=1` to restore the old blocking dialog for a session where
catching an assert interactively is specifically wanted. This also turned out to be the better
diagnostic path: grepping the log for `missing startLocation` is what made the 23 → 0 verification
above possible, which the modal dialog could never have given.

### Dedicated-pad test coverage audit + the show-pane trap (2026-08-03)

User question: is the ILSpy integration test complete - do all dedicated pads have visible-content
checks? **Audit answer: no.** Of the three tool pads, only "Assemblies" had a real visible-content
assertion (the `SharpTreeNodeView`-with-non-zero-bounds check). "Search" and "Analyze" were covered
by nothing but title + `IsVisible`, which is exactly what the historical "empty pane content area"
failure mode passes: correct tab header, blank content. The SearchBox specifically had its own such
regression ("rendered as a blank gap", fixed via `generic.xaml`) and nothing guarded it. The
`"Assemblies"`/`"Decompiled Code"` string assertions are *tab header text*, not content - the test's
own comment already said so.

Added to `OpenAssembly_ShowsIlSpyPadsWithRealContent`:

- Search pad content: `ICSharpCode.ILSpy.Search.SearchPane` + `ICSharpCode.ILSpy.Controls.SearchBox`
  render with non-zero width *and* height.
- Analyze pad content: `ICSharpCode.ILSpy.Analyzers.AnalyzerTreeView` (which *is* a `SharpTreeView` -
  its XAML root element - so this also covers the shared tree control inside that pad).
- **Search → result → activate → the Assemblies tree jumps** (the behavior the user asked for):
  search `ComputeGreeting` (unique to the DebugTestApp fixture, so deterministic even though ILSpy
  also searches auto-loaded framework assemblies), assert the hit is in `DebugTestApp.Program` and
  carries a navigable `Reference`, activate it, then assert the tree selection *changed* and now
  holds a `MethodTreeNode` named `ComputeGreeting...`. Faithful by construction:
  `SearchPane.JumpToSelectedItem` (what double-click calls) does exactly one thing -
  `MessageBus.Send(new NavigateToReferenceEventArgs(result.Reference))` - which `AssemblyTreeModel`
  turns into `JumpToReferenceAsync` → `SelectNode`. Verified live end-to-end before writing the
  test: selection moved from `AssemblyTreeNode: DebugTestApp (1.0.0.0, ...)` to
  `MethodTreeNode: ComputeGreeting(string) : string`.

New DevFlow actions: `od.ilspy.search`, `od.ilspy.search-activate`, `od.ilspy.activate-pane`,
`od.ilspy.activate-decompiled-document`; plus `selectedNodeDetails` on `od.ilspy.status` (the
existing `selectedNodes` reports `AssemblyTreeNode` only, so it goes *empty* the moment the selection
moves to a type/member node - i.e. precisely when search navigation succeeds).

**Two real traps found while doing this, both measured rather than assumed:**

1. **`od.ilspy.show-pane` is destructive and is now the wrong tool.** It removes and re-adds the
   anchorable - justified back when runtime-added panes didn't reliably dock, but harmful now that
   the ILSpy layout template actually restores (see the layout-schema work earlier in this document).
   Measured: after one `show-pane`, activating a *different* pane fails to materialize it at all, and
   repeated churn eventually leaves none of the three rendered. Added the non-destructive
   `od.ilspy.activate-pane` (`Show()` + `IsActive` + dock `ActiveContent`, no re-registration) and
   switched the test to it. `show-pane` is kept for compatibility with its caveat documented in its
   own action description.
2. **A pane's DataTemplate view does not exist until the pane is activated**, and because these
   DevFlow actions run *on* the UI thread, an action that activates a pane and then immediately walks
   the visual tree finds nothing - WPF has not run measure/arrange yet. The action must `await` (not
   spin) so the layout pass can happen; `EnsureSearchPaneAsync` polls with `await Task.Delay`. Also
   note the view lives under a visual root that is *not* reachable from
   `Application.Current.MainWindow` alone, so the search walks every open window.

**Why these are one test method, not three `[Fact]`s:** the app fixture is shared across the whole
collection, so separate tests that each activate panes interfere in an order-dependent way. Measured:
as three `[Fact]`s the suite failed 3/5 - "pane not materialized" plus a lost active document
(activating any tool pane makes it the dock's `ActiveContent`, leaving the workbench with no active
*document*, which broke the existing active-view assertion). Sequenced inside one test it is
deterministic. Result: 2/2 pass.

**Still NOT covered** (honest list, so this isn't mistaken for完整 coverage): the theme bridge
(`od.ilspy.theme`), the folded-using placeholder (`od.ilspy.foldings`), the native
`DecompiledViewContent`/`ilspy://` document path including the live single-`TypeTreeNode` routing,
reference hyperlink navigation, and debug-symbol/sequence-point correctness. Each of those has a
working DevFlow action but no assertion pinning it.

### ILSpy toolbar: dedicated icon buttons + the toolbar-tray width ceiling (2026-08-03)

User: the ILSpy strip needs more dedicated icon buttons to line up with real ILSpy. It had exactly
one ("Open Assembly..."). Real ILSpy composes its toolbar from `[ExportToolbarCommand]` attributes
(`ILSpy/Controls/MainToolBar.xaml`); the commands are Back/Forward (Navigation), Open/Reload (Open),
Search/Sort/CollapseAll (View). Added the six missing ones as
`Commands/IlSpyToolBarButtons.cs`, ordered in the `.addin` to mirror that grouping with separators.

Every one of those ILSpy commands does nothing but delegate to an `AssemblyTreeModel` method
(`NavigateHistory`, `Refresh`, `SortAssemblyList`, `CollapseAll`), so these call the same model
methods directly instead of resolving ILSpy's own MEF command objects - which are `internal sealed`
and bound to ILSpy's composition/DockWorkspace anyway. Back/Forward drive their enabled state off
`CanNavigateBack`/`CanNavigateForward` via `IStatusUpdate`. Search uses the new
`IlSpyWorkspaceHost.ActivatePane` (the non-destructive path - see the show-pane finding above).

**Icons: VS2017 Image Library, filenames kept verbatim** (per the user, and matching the existing
`Icons/` convention of AiToXaml-converted vector XAML - explicitly *not* ILSpy's own `Images/`).
Copied `Backward_16x.xaml`, `Forward_16x.xaml`, `Refresh_16x.xaml`, `SortAscending_16x.xaml`,
`CollapseAll_16x.xaml`; Search reuses the already-present `Search.xaml`. The csproj's existing
`Icons\*.xaml` glob embeds them as `Icons.{name}.xaml`, which is what `VsIconLoader.Load` expects.

These are AddInTree `type="Custom"` (`ICustomToolBarItem`) items, not ordinary `type="Item"` ones
with an `icon=` attribute, because `icon=` resolves through
`PresentationResourceService.GetBitmapSource` - the shell's *bitmap* bundle
(`data/resources/image/BitmapResources`), which knows nothing about these vector icons. Going
through `ICustomToolBarItem` lets each button supply its own `ImageSource` and keeps the whole thing
inside this addin, no shell change needed for the icons.

**But a shell change *was* needed, for a real reason found by measuring.** With the six buttons
added, only two of them rendered - the other four were silently swallowed by the strip's overflow
popup. Cause: `ToolBarTray` puts every strip on band 0 unless told otherwise, so all strips compete
for one row. Measured on a 1024px window: eight strips wanted ~1010px, and the ILSpy strip - last in
order - was squeezed to 69px, enough for 2 of its 7 items. Fixed generically in
`WpfWorkbench.AssignToolBarBands` (hooked to the tray's `SizeChanged`): measure each strip
unconstrained and wrap onto additional bands so every strip gets its full desired width.
Deliberately *not* special-casing the ILSpy strip - the constraint is the tray's width and every
strip is subject to it (and hardcoding "ILSpy" in the shell is exactly the coupling the layout-
ownership work earlier in this document removed).

Verified live: all 6 ILSpy buttons render at 20x22, the ILSpy strip wrapped to a second row
(`y=43` while the other seven sit at `y=16`), and enabled state is correct out of the box -
Back/Forward disabled (no navigation history yet), Reload/Search/Sort/Collapse enabled. Regression
check on the shell layout change: `IlSpyAddInTests` 2/2 and the UI-tree-heavy `WpfDesignerTests`
5/5 pass.

Not added (real ILSpy has them, but they are combo boxes / checkbox groups rather than icon buttons,
i.e. a different piece of work): the assembly-list dropdown + Manage Assembly Lists, the three
API-visibility toggles (public only / public+internal / all), and the language + language-version
dropdowns.

#### Follow-up: the three API-visibility toggles (2026-08-03)

User: the visibility-control buttons were still missing. Added them - real ILSpy hardcodes three
CheckBoxes in `ILSpy/Controls/MainToolBar.xaml` bound to
`SessionSettings.LanguageSettings.ApiVisPublicOnly` / `ApiVisPublicAndInternal` / `ApiVisAll`.

Those three bools are not independent: they are a radio group over one enum,
`LanguageSettings.ShowApiLevel` (`ICSharpCode.ILSpyX.ApiVisibility`: PublicOnly /
PublicAndInternal / All) - each setter just switches the enum and raises PropertyChanged for all
three. So `IlSpyApiVisibilityToggleBase` reads the enum to decide whether it is the checked one, and
selecting one refreshes its siblings (`IlSpyApiVisibilityToggles.UpdateAll`, a weak-reference
registry so toggles don't leak). Host access added as
`IlSpyWorkspaceHost.GetApiVisibility`/`SetApiVisibility`.

No explicit tree refresh is needed: `AssemblyTreeModel`'s settings handler already subscribes to
`LanguageSettings` PropertyChanged and calls `Refresh()` for any property other than
LanguageId/LanguageVersionId (`AssemblyTreeModel.cs`) - which is what re-filters the assembly tree.
(Verified by reading that upstream code, not by observing the filtering.)

`CheckBox`, not `Button`, matching ILSpy and conveying sticky state; styled with
`ToolBar.CheckBoxStyleKey` exactly as the shell's own `ToolBarCheckBox` does, so it gets flat toolbar
chrome. Icons from the VS2017 Image Library with **filenames kept verbatim**, chosen so the icon
shows the *lowest* visibility the level includes: `Method_16x` (plain = public), `MethodFriend_16x`
("friend" is VS iconography for internal), `MethodPrivate_16x`.

Verified live. The UI tree cannot report a CheckBox's `IsChecked` (it only exposes
`state.selected`, which reads false regardless), so a dedicated `od.ilspy.api-visibility` action was
added to read/set the level and report each toggle's real `IsChecked` - without it the first
observation looked like "none of the three are checked", which was a reporting artifact, not a bug:

| action | level | PublicOnly | PublicAndInternal | All |
|---|---|---|---|---|
| initial (default) | PublicAndInternal | false | **true** | false |
| set All | All | false | false | **true** |
| set PublicOnly | PublicOnly | **true** | false | false |

All 9 ILSpy toolbar items now render (6 buttons at 20x22 + 3 toggles at 22x22) on the strip's own
wrapped band. `IlSpyAddInTests` 2/2 still pass.

Still not ported (combo boxes, i.e. a different control type and a separate piece of work): the
assembly-list dropdown + Manage Assembly Lists, and the language + language-version dropdowns.

#### Fix: the toolbar buttons were rendering with default Button chrome (2026-08-03)

User spotted that some ILSpy strip buttons had borders. Correct - and it was a styling bug of mine,
not something inherent to the strip. A `Button` placed inside a WPF `ToolBar` does **not** pick up the
flat toolbar chrome on its own; it keeps the default Button style, borders included. The shell's own
`ToolBarButton` sets it explicitly (`ToolBarButton.cs:61`:
`SetResourceReference(FrameworkElement.StyleProperty, ToolBar.ButtonStyleKey)`), and
`IlSpyToolBarButtonBase` was missing exactly that - I had styled only the inner `Image` (and had
remembered `ToolBar.CheckBoxStyleKey` for the CheckBox-based visibility toggles, which is why those
looked right and the six buttons did not).

Fixed by adding the same `ToolBar.ButtonStyleKey` reference. Measured before/after against the shell's
own `ToolBarButton` as the baseline:

| | before | after | shell baseline |
|---|---|---|---|
| size | 20x22 | **22x22** | 22x22 |
| borderBrush | default Button chrome | **#00FFFFFF** (transparent) | #00FFFFFF |
| background | default | **#00FFFFFF** | #00FFFFFF |

The one element that still carries a visible border is `IlSpyShowPublicAndInternalToggle`
(`border=#80DADADA`, `bg=#400080FF`) - that is the *checked* state highlight of a toggle and is
correct: it is the currently selected API-visibility level (default `PublicAndInternal`), and the
other two toggles are transparent. `IlSpyAddInTests` 2/2 still pass.

#### The dropdown half of the toolbar (2026-08-03)

User: the dropdown strip elements are needed too for complete ILSpy functionality. Added, in
`Commands/IlSpyToolBarCombos.cs`. Unlike the icon buttons, ILSpy does **not** export these as
commands - it hardcodes them in `ILSpy/Controls/MainToolBar.xaml` - so what is mirrored here is that
XAML's bindings, not a command object:

| dropdown | items | selection | notes |
|---|---|---|---|
| assembly list | `AssemblyListManager.AssemblyLists` (ObservableCollection&lt;string&gt;) | `SessionSettings.ActiveAssemblyList` | AssemblyTreeModel turns the write into `ShowAssemblyList(...)` |
| (button) | - | - | opens ILSpy's own `ManageAssemblyListsDialog`, already linked source here |
| language | `LanguageService.AllLanguages`, DisplayMemberPath `Name` | `LanguageService.Language` | drives `RefreshDecompiledView()` |
| language version | selected language's `LanguageVersions`, DisplayMemberPath `DisplayName` | `LanguageService.LanguageVersion` | collapsed when `HasLanguageVersions` is false |

Icon for Manage Assembly Lists: `Library_16x` from the VS2017 Image Library, filename kept verbatim.

Three implementation points worth keeping:

1. **Binding is deferred, never done in `Initialize`.** The toolbar is constructed during workbench
   startup, long before any ILSpy action, and every `IlSpyWorkspaceHost` member except
   `IsInitialized` boots the whole hosted ILSpy as a side effect. So `IlSpyToolBarComboBoxBase`
   binds on the first `UpdateStatus()` that sees `IsInitialized` - a toolbar being built must not be
   what starts ILSpy. A failing bind is logged and disables just that dropdown rather than taking
   the workbench's status pass down.
2. **`Language` is ambiguous inside these classes** - `ComboBox` inherits
   `FrameworkElement.Language` (an `XmlLanguage`), which shadows ILSpy's own `Language` type. Aliased
   (`IlSpyLanguage`/`IlSpyLanguageVersion`) rather than relying on resolution order.
3. **The version dropdown is push-based, not polled.** First cut re-read the language on the
   workbench's periodic status pass, and measured, that left it still showing `C# 15.0` with the
   dropdown visible after switching to IL. It now subscribes to `LanguageService.PropertyChanged`
   (Language/LanguageVersion), which is what ILSpy's XAML binding does for the same reason.

Also fixed while verifying: the assembly-list dropdown showed **no selection** on a fresh profile.
`SessionSettings.ActiveAssemblyList` is only written when settings are *saved*
(`AssemblyTreeModel` does `settings.ActiveAssemblyList = AssemblyList.ListName` on save), so it is
still null while `(Default)` is already the loaded list. Now prefers the actually-loaded
`AssemblyTreeModel.AssemblyList.ListName` and falls back to the session setting.

Verified live via a new `od.ilspy.toolbar-combos` action (reads item counts / selected item /
visibility and can select a value, since the UI tree exposes neither `Items` nor `SelectedItem` for a
ComboBox):

| step | assembly list | language | language version |
|---|---|---|---|
| initial | `(Default)`, 1 item | `C#`, 21 items | `C# 15.0 / VS 202x.yy`, 18 items, visible |
| select `IL` | `(Default)` | `IL` | 0 items, **collapsed** |
| select `C#` | `(Default)` | `C#` | 18 items, visible again |
| select `C# 5.0 / VS 2012` | `(Default)` | `C#` | `C# 5.0 / VS 2012` |

And functionally, not just cosmetically: after switching the language dropdown to IL, the decompiled
document's text really changed to IL output (verified via `od.ilspy.status`). Note ILSpy persists
these session settings, so the language/version chosen in one run is still selected on the next -
correct behavior, but it means "initial" state depends on the previous session.

The ILSpy strip now carries 13 items (6 buttons + 3 visibility toggles + 2 dropdowns + manage button
+ version dropdown), all rendering. `IlSpyAddInTests` 2/2 pass. Assembly-list *switching* is
populated and selectable but not exercised end-to-end here: a fresh profile has only the `(Default)`
list, and creating another one goes through the modal Manage dialog.

### MSIL (and Asm) syntax highlighting was never registered - root cause + fix (2026-08-03)

User: switching to IL shows no syntax highlighting. Confirmed and root-caused: `DecompilerTextView.
RegisterHighlighting()` (called from its own ctor) does **not** call AvalonEdit's
`HighlightingManager.RegisterHighlighting(string,string[],string)` overload - that overload is
`internal` to AvalonEdit's own assembly (`HighlightingManager.cs`'s nested
`DefaultHighlightingManager`) and unreachable from linked ILSpy source. What actually resolves the
call is `DecompilerTextView.cs`'s own `static class ExtensionMethods` at the bottom of the same
file - an extension method with the identical signature
(`this HighlightingManager manager, string name, string[] extensions, string resourceName`), which
C# overload resolution prefers. That extension method looks the `.xshd` up via
`typeof(DecompilerTextView).Assembly.GetManifestResourceStream(typeof(DecompilerTextView),
resourceName + ".xshd")` - i.e. **in this addin's own assembly**, under a namespace-qualified
resource name (`GetManifestResourceStream(Type, string)` prefixes the type's namespace,
`ICSharpCode.ILSpy.TextView`). **If the stream is `null` it just returns - no exception, nothing
logged.** `ILSpyAddIn.csproj` never embedded `ILAsm-Mode.xshd`/`Asm-Mode.xshd` at all, so this was a
silent no-op the whole time; "xml" was never affected because it collides with AvalonEdit's own
built-in "XML" definition for `.xml`/`.baml`, masking the same gap for that one language only.

Fix: embed the real (linked, not copied) `.xshd` files under the exact `LogicalName` that lookup
expects - `ICSharpCode.ILSpy.TextView.ILAsm-Mode.xshd` / `...Asm-Mode.xshd` - in `ILSpyAddIn.csproj`.
No code change needed: once the resources resolve, ILSpy's own extension method loads and registers
them itself (and wires `ThemeManager.Current.ApplyHighlightingColors`, so IL highlighting is
theme-aware for free - a first attempt at this fix wrote a custom loader before this was found;
discarded once the real mechanism was understood, since it would have bypassed that theming and
duplicated ILSpy's own working logic).

Verified live via a new `od.ilspy.highlighting-status` action (checking "nothing crashed" is not
evidence a silent no-op didn't happen, hence checking the live effect specifically):

| state | `ilAsmRegistered` | live `textEditor.SyntaxHighlighting.Name` |
|---|---|---|
| C# (initial) | true | `C#` |
| after switching to IL | true | **`ILAsm`** |

`IlSpyAddInTests` 2/2 still pass.

### Multi-pad workflow coverage - the actual point of the earlier per-pad checks (2026-08-03)

User: improve ILSpy integration test coverage with attention to multi-pad linkage, since that's how
a user actually works (not each pad in isolation). Added, inside
`OpenAssembly_ShowsIlSpyPadsWithRealContent` (kept as one test - see the earlier "why one test
method" note, which applies here too: the shared app instance makes independent `[Fact]`s interfere):

1. **Search pad -> Assemblies pad -> Decompiled Code document.** The search-and-activate jump
   (already covered) was only ever checked for its effect on the *tree selection*. Added the third
   pad: after the jump, the decompiled document must contain the exact expected decompilation of
   `ComputeGreeting` (fixed fixture, so exact source lines, not just "contains the name somewhere").
2. **Assemblies pad -> Analyze pad.** New `od.ilspy.analyze-selected` DevFlow action runs exactly
   what ILSpy's `AnalyzeCommand` does (`SelectedNodes.OfType<IMemberTreeNode>() ->
   AnalyzerTreeViewModel.Analyze(node.Member)`). Asserts the exact resulting node
   (`AnalyzedMethodTreeNode`, text `DebugTestApp.Program.ComputeGreeting(string) : string`) and its
   exact children (`Uses`, `Used By`) - a real analysis, not an empty placeholder.
3. **Back navigation undoes the jump.** New `od.ilspy.navigate-history` action drives
   `AssemblyTreeModel.NavigateHistory` (what the Back/Forward toolbar buttons call). Asserts the
   selection actually changes, Forward becomes available, and the tree lands back on the exact
   assembly node.
4. **Toolbar language dropdown -> Decompiled Code document.** Crosses the toolbar and the document -
   the one toolbar element whose effect is directly observable in content. Asserts the exact IL
   rendering of `ComputeGreeting` and that the language-version dropdown collapses (IL has none).

**Two real bugs found by writing this, both are cross-pad state, exactly the kind of thing isolated
per-pad tests structurally cannot catch:**

- **The toolbar language dropdown didn't actually change the visible document.** Root cause:
  `AssemblyTreeModel`'s own settings handler reacts to a language change by calling
  `RefreshDecompiledView()`, which decompiles into `DockWorkspace.ActiveTabPage`'s text view - ILSpy's
  own tab system, which this host deliberately never renders (only one dummy `TabPageModel` so
  upstream reads of `ActiveTabPage` don't `NullReferenceException`). So the dropdown updated, ILSpy
  decompiled *somewhere invisible*, and the document users actually see kept showing the previous
  language. Fixed by giving `IlSpyWorkspaceHost` its own subscription to
  `LanguageService.PropertyChanged` (Language/LanguageVersion), mirroring the existing
  `AssemblyTreeSelectionChangedEventArgs` subscription that already exists for the same reason.
  User first noticed this live ("刚才下拉框还是 IL，但是反编译的代码好像是 C#") before the test caught
  it structurally - both point at the same gap.
- **`od.ilspy.search` was not idempotent for repeated identical terms.** `SearchPaneModel.SearchTerm`
  is a `SetProperty` - assigning the same value again is a no-op, so no `PropertyChanged` ->
  no `searchBox.TextChanged` -> `StartSearch` never re-runs. The multi-pad test needs to re-search
  the same term after other pads have been touched (the Search pane view can have been
  re-materialized in between, invalidating any cached result index), and that second identical
  search silently returned 0 results. Fixed by forcing the value to actually change
  (clear then set) before assigning the real term.

`IlSpyAddInTests` 2/2 pass with all of the above, using exact-value assertions throughout (fixed
fixture, so precise expected output rather than "contains" checks) per explicit guidance to make
assertions tighter given the fixture never changes.

### Step 3 re-enabled: whole-module tree selection now routes to the native document (2026-08-03, continued)

Picked back up per the earlier survey of "what's left." The blocker was never technical - `NavigateToModule`/`DecompiledViewContent`'s whole-module support and the `DecompilationTask` plumbing were already correct and already used by direct exercising (`od.ilspy.navigate-to-type`-style calls) - it was that `OpenAssembly_ShowsIlSpyPadsWithRealContent` pinned the *old* behavior. Fixed by updating the test's expectations deliberately, then re-enabling the `AssemblyTreeNode` branch in `OnSelectionChangedAsync`:

- Active view after opening an assembly is now `ICSharpCode.ILSpyAddIn.DecompiledViewContent` (native), not `DecompiledCodeViewContent` (bespoke pane).
- Its tab title is `"[Module]"` (`DecompiledTypeReference.IsWholeModule`'s title, from the earlier fix), not `"Decompiled Code"`.
- `od.ilspy.status`'s decompiled-text assertions needed no changes - the fallback added when step 2 first shipped (read from the active `DecompiledViewContent` when there is one, else the bespoke pane) already covers the whole-module case too.

Verified live beyond the test itself: `od.active-view` after `od.ilspy.open-assembly` reports
`typeName: DecompiledViewContent`, `fileName: ilspy://.../module.cs`, and real whole-module C# output
(assembly-level attributes, `using` directives) - not a stub. `IlSpyAddInTests` 2/2 pass.

**What this unblocks next** (not done in this pass): with ordinary tree selection now covering both
the single-type and whole-module cases through the native path, the bespoke `DecompilerTextView`/
`decompiledCodeView` singleton is only still reachable for namespace nodes, member nodes, and
multi-selection (see "Multi-select and reference hyperlink navigation: still out of scope" above -
unchanged, those remain real, separate gaps). Removing the bespoke pane entirely (Phase 4's "remove
the dummy ILSpy TabPageModel path") still requires covering those remaining cases first.

### Reference hyperlink navigation - implemented earlier, now actually verified (2026-08-03)

Correction to this technote's own record: the "Multi-select and reference hyperlink navigation:
still out of scope" entry above said "no code exists for this yet in either direction." That became
stale without a follow-up note - `ReferenceTrackingTextOutput`'s `DecompiledReferenceSpan` capture
and `DecompiledViewContent.OnPreviewMouseDown`'s Ctrl+Click handler (added while fixing the
`SequencePointBuilder` root cause) already implement this for the native document path. It had only
ever been checked for text/reference-*count* correctness (`od.ilspy.decompile-type`), never an actual
click.

Verified live and added to `OpenAssembly_ShowsIlSpyPadsWithRealContent` as step (5). Extracted the
click handler's logic into `DecompiledViewContent.TryNavigateAtOffset(int offset)` (internal,
testable) so a DevFlow action can exercise it directly - `od.ilspy.click-reference` finds a
substring's offset in the active document and calls it, which is everything the mouse handler does
except the pixel-to-offset step (`TextEditor.GetPositionFromPoint`) - unmodified, already-relied-
upon AvalonEdit API that real `.cs`-file Ctrl+Click "Go To Definition" already uses today
(`CodeEditorView.cs`), so it isn't the part actually being verified here.

Real screen-coordinate clicking (`POST /api/v1/ui/actions/click`, `{"x":...,"y":...}`,
`mode: "native-global"`) does exist in this environment (user-corrected an earlier "no click
capability" claim) - attempted it first, calibrating against a known element with an observable,
distinct effect (an API-visibility toggle CheckBox's `IsChecked`). Direct UI-tree bounds, bounds +
window-origin offset, and bounds × 2 (Retina scale) were all tried; none toggled the checkbox, so
the coordinate system this environment's `bounds` are reported in vs. what the click endpoint expects
did not converge in reasonable attempts. Given the pixel-to-offset half is proven, stable, unmodified
AvalonEdit code, this was descoped rather than chased further - `TryNavigateAtOffset` is the part
this session actually wrote, and it is what's verified.

Verified sequence: opened the DebugTestApp fixture (whole-module document), which contains
`Main`'s call `ComputeGreeting("World")` - a real use-site reference, not a definition. Clicking it
(`od.ilspy.click-reference "ComputeGreeting" 0`) navigated to a *new* native document for
`DebugTestApp.Program` and landed the caret on line 17 - `private static string
ComputeGreeting(string name)`, the method's exact declaration line, confirming both the reference-
span lookup and the `memberKey`-based `JumpToMember` precision.

**One real bug found while adding this to the test, unrelated to the click logic itself**:
`od.ilspy.select-node` (routes through the real ILSpy tree control) does not reliably reclaim the
dock's `ActiveContent` back to a document once *any* tool pane has held it - measured, `od.active-
view` reported `{"active":false}` for a full 30-second poll after `select-node "DebugTestApp"`,
following earlier `activate-pane` calls for Search/Analyze in the same test run. This is the same
family of AvalonDock focus-priority quirk `od.ilspy.activate-decompiled-document` already exists to
work around for the bespoke pane - not something this session's native-routing code introduced. Added
`od.ilspy.navigate-to-module` (mirrors the existing `od.ilspy.navigate-to-type`) so the test - and any
future caller that needs to reliably return to the whole-module document - can bypass the tree
control entirely rather than depend on its focus behavior.

Multi-select tree-node decompilation remains the one item still with zero code in either direction -
see the "still out of scope" entry above, unchanged.

### Multi-select decompilation - the last item with zero code either way (2026-08-03)

Closed the one remaining gap from the "what's left" survey: multi-node tree selection now
decompiles into a single native document too.

**Design insight that made this small rather than another standalone-resolver saga**: real ILSpy's
own bespoke-pane multi-select (`DecompilerTextView.DecompileNodes`,
`TextView/DecompilerTextView.cs`) does nothing fancy - it just calls each selected
`ILSpyTreeNode`'s own `Decompile(Language, ITextOutput, DecompilationOptions)` into one shared
`ITextOutput`, blank line between. `ILSpyTreeNode.Decompile` is already polymorphic per node kind
(type/member/namespace/assembly node all override it), and for C# it ultimately reaches
`CSharpLanguage`'s own `WriteCode`, which builds its own `CSharpDecompiler` internally through the
tree's already-loaded-assembly context - so passing `ReferenceTrackingTextOutput` in as that
`ITextOutput` gets reference-span capture "for free," with **no separate `CSharpDecompiler`/resolver
setup needed at all**. The resolver gap `DecompileType` (the single-type path) had to work around
earlier in this document never applies here, because this path never builds its own decompiler -
it just reuses each node's. Added as `ILSpyDecompilerService.DecompileNodes`.

Bonus found while writing this: `CSharpLanguage.WriteCode` always runs `InsertParenthesesVisitor`
first (readability parentheses) - something `DecompileType`'s hand-built pipeline still lacks (the
"known remaining fidelity gap" noted earlier). Multi-node decompile therefore has *better* C#
fidelity than single-type/whole-module decompile. Not backported there in this pass - noted, not
fixed, consistent with the existing gap entry.

**`DecompiledReferenceSpan` gained an `AssemblyFile`** (previously assumed "the document's own
assembly," which was always correct for the single-type/whole-module path since its reference
capture is restricted to same-module entities anyway). A multi-selection can span *different*
assemblies with no single "the" assembly, so each span now carries its own target's assembly file,
derived from `entity.ParentModule.MetadataFile.FileName`. `mainModule` in `ReferenceTrackingTextOutput`
is `null` for the multi-node path (no same-module filter - capture every resolvable reference
regardless of source module) but still non-null (unchanged behavior) for the existing single-type
path. `DecompiledViewContent.TryNavigateAtOffset` was updated to read `span.AssemblyFile` too, so
there is one navigation code path instead of two that happened to agree.

New `DecompiledSelectionViewContent` (new file) hosts the combined output. Unlike
`DecompiledViewContent`, there is no stable per-selection identity to reuse by - an arbitrary node
combination has no natural URI - so it is a single, lazily-created, reused-and-overwritten instance
(mirroring exactly how the retired-for-this-case bespoke pane behaved: one shared surface, content
replaced each time, not one tab per selection). Wired into `IlSpyWorkspaceHost.OnSelectionChangedAsync`'s
`nodes.Length > 1` branch. Single-node selections that aren't a `TypeTreeNode`/`AssemblyTreeNode`
(member nodes, namespace nodes) still fell through to the bespoke pane at the time this was
written - closed later in this pass, see "Closing out the remaining smaller gaps" below.

**One real, if narrow, bug found and fixed while wiring this up**: the first version's
`RefreshAsync` did `Task.Run(() => { ... codeEditor.Document.Text = result.Output; ... })` -
i.e. the WPF/AvalonEdit `Document.Text` write happened *inside* the background thread the whole
lambda ran on. AvalonEdit's document is not thread-safe; this is a cross-thread-access violation.
Measured, not theorized: the document stayed at `textLength: 0` indefinitely with no visible
crash (an unobserved faulted `Task`, since nothing awaited or logged it downstream) - a real trap
easy to fall into when translating a synchronous multi-step decompile into an async method by
wrapping the whole thing in one `Task.Run`. Fixed to match `DecompiledViewContent.InitializeView`'s
existing shape: `await Task.Run(() => onlyTheDecompileItself)`, then set `Document.Text` after the
`await` - which resumes on the original `SynchronizationContext` (the WPF Dispatcher) by default,
not on the background thread.

Also needed a small, unrelated fix for the *reporting*, not the feature: `od.ilspy.status`'s
"which native document is active" fallback checked `ActiveViewContent as DecompiledViewContent`
specifically, so it silently didn't recognize the new `DecompiledSelectionViewContent` and would
have fallen back to stale bespoke-pane text for the multi-select case. Generalized to check
`ActiveViewContent?.Control is CodeEditor` instead - both native document classes expose a
`CodeEditor` as their `Control`, and the bespoke pane's `Control` is a real ILSpy
`DecompilerTextView`, never a `CodeEditor`, so this can't misidentify it either way.

New DevFlow action `od.ilspy.select-nodes` (comma-separated assembly ShortNames) drives
`AssemblyTreeModel.SelectNodes` for testing. Verified live and added to
`OpenAssembly_ShowsIlSpyPadsWithRealContent` as step (6): selected `DebugTestApp` + the
already-auto-loaded `System.Linq` together (a genuine cross-assembly multi-select, not just
multiple nodes within one module) and confirmed the combined document contains both modules' own
header comments, DebugTestApp's before System.Linq's (selection order preserved), and that the
active view is `DecompiledSelectionViewContent`. `IlSpyAddInTests` 2/2 pass.

**Status update**: every item flagged as "zero code in either direction" at the start of this pass
is now implemented (reference hyperlink navigation, multi-select). The remaining, smaller gaps -
member/namespace single-node selection still on the bespoke pane, the `InsertParenthesesVisitor`
fidelity gap on the single-type path, cross-assembly navigation restrictions on that same path - are
documented above where each was found, not repeated here.

## Closing out the remaining smaller gaps (2026-08-03, later in this pass)

The three items flagged above as "documented above where each was found, not repeated here" are
now all closed.

**1. `InsertParenthesesVisitor` backported to `DecompileType`.** Added the same call
`ILSpyDecompilerService.DecompileNodes` already got "for free" (`CSharpLanguage.WriteCode` always
runs it) directly into `DecompileType`'s hand-built pipeline, right after building the syntax tree
and before `WriteSyntaxTree`/`SetLocationsInAst` - it has to run before the location pass, since it
mutates the AST (inserting new `ParenthesizedExpression` nodes) and the location pass assumes a
stable tree. Verified live: decompiled the fixture's `ComputeGreeting` (`return "Hello, " + name +
"!";`) before and after - byte-identical output, since a flat string concatenation never needs
readability parens. `IlSpyAddInTests` 2/2 still pass (the test pins this exact string).

**2. Cross-assembly reference navigation, single-type/whole-module path.** Removed
`ReferenceTrackingTextOutput`'s `mainModule` field/constructor parameter and the same-module
`ReferenceEquals` guard in `RecordAndWrite` entirely - every reference now gets its own
`AssemblyFile` (from `entity.ParentModule.MetadataFile.FileName`) regardless of whether it's the
module being decompiled, exactly like the multi-node path already worked. There's now exactly one
`ReferenceTrackingTextOutput`, not "the single-type one, with a filter" and "the multi-node one,
without" - both call sites (`WriteSyntaxTree`, `DecompileNodes`) construct it the same way.
`DecompiledReferenceSpan`'s class doc comment updated to match (see the class - it no longer
describes a same-module restriction). `IlSpyAddInTests` 2/2 still pass.

**3. Member-node/namespace-node single selections now route to the native document.**
`IlSpyWorkspaceHost.OnSelectionChangedAsync`'s dispatch widened from `nodes.Length > 1` to
`nodes.Length >= 1 && nodes.All(n => n is ILSpyTreeNode)` - i.e. everything that isn't specifically
a lone `TypeTreeNode`/`AssemblyTreeNode` (those two keep their own dedicated `NavigateTo`/
`NavigateToModule` calls, since they have a stable per-entity document identity worth reusing
across selections) now goes to `RefreshSelectionDocumentAsync`/`DecompiledSelectionViewContent`,
covering `MethodTreeNode`/`FieldTreeNode`/`PropertyTreeNode`/`EventTreeNode`/`NamespaceTreeNode`
and anything else alike. No new decompile logic was needed - `DecompileNodes`/
`DecompiledSelectionViewContent` were already fully generic over node kind from the multi-select
work; this just widened which selections reach them.

This surfaced two real bugs, both found by re-running the full multi-pad integration test after
the change (not just building):

- **The language-dropdown handler still wrote into the bespoke pane unconditionally.**
  `LanguageService.PropertyChanged`'s subscriber called `RefreshDecompiledViewAsync()` directly,
  which always decompiles into `decompilerTextView` regardless of what's selected - correct back
  when member-node selections had nowhere else to go, wrong now that they have their own document.
  Measured: switching the toolbar's language dropdown to IL while a member node was selected left
  the *native* selection document showing stale C#, while the (now nobody-reads-it) bespoke pane
  correctly held the new IL - exactly the reverse of the bug this same handler was added to fix
  earlier in this pass. Fixed by calling `OnSelectionChangedAsync()` instead of
  `RefreshDecompiledViewAsync()` directly - it already contains the exact right per-node-kind
  dispatch, so re-running it on a language change keeps whichever document is actually showing the
  current selection in sync, the same way re-selecting the node would.

- **`od.ilspy.status`'s "which document is active" fallback assumed `SD.Workbench.ActiveViewContent`
  reliably reflects the just-refreshed selection document.** It doesn't, for tree-driven selections:
  this is the pre-existing "select-node focus loss" quirk (documented earlier in this file on
  `RefreshDecompiledViewAsync`'s callers, and worked around in the reference-hyperlink test step by
  using `od.ilspy.navigate-to-module` instead of tree selection) - `AssemblyTreeModel`'s own pane
  can end up holding AvalonDock's single shared `ActiveContent` even though
  `RefreshSelectionDocumentAsync`'s `ShowView`/`SelectWindow` calls ran and the document refreshed
  correctly underneath. Previously invisible for member-node selections specifically only because
  they went to the bespoke `DecompilerTextView` pane, which never participates in
  `Workbench.ActiveViewContent` at all - so the quirk had nothing to hide before. Measured directly:
  `od.active-view` polled `{"active":false}` for 20+ seconds straight after a real search-driven
  member-node selection, even though the selection document's content was correct underneath the
  whole time. A `Dispatcher.BeginInvoke(ApplicationIdle, ...)` re-assertion of `SelectWindow()`
  was tried first and measured *unreliable* (won the race sometimes, not always - the full
  integration test flipped between passing and failing across otherwise-identical runs). The robust
  fix instead exposes `IlSpyWorkspaceHost.DecompiledSelectionView`/
  `DecompiledSelectionViewContent.CurrentText` and has `od.ilspy.status` check that content directly
  as a fallback, before ever touching the bespoke pane - sidesteps the focus race for this
  diagnostic read entirely rather than trying to win it. (The deferred `SelectWindow` re-assertion
  was kept alongside this, since it doesn't hurt and helps real interactive use even though it isn't
  the thing the test now depends on.)

`IlSpyAddInTests` 2/2 pass, confirmed twice in a row (the second run specifically to rule out the
flakiness the first fix attempt had).

**Status update**: all three of the smaller remaining gaps identified in the "what's left" survey
are now closed. Every item from that survey - reference hyperlink navigation, multi-select
decompilation, member/namespace single-node routing, the `InsertParenthesesVisitor` fidelity gap,
and cross-assembly reference navigation - has real, live-verified code behind it now.

## Pad-position test coverage, and a real "layout gets lost" bug found and fixed (2026-08-03)

User-flagged: existing pad tests check title/`IsVisible`/rendered content, but none of that catches
a pad docked in the *wrong place* - and the user had seen the ILSpy layout "get lost" a few times
during manual testing. Added real position introspection instead of guessing:

- `ILSpyAddIn`'s `od.ilspy.pane-position` (and `od.ilspy.status`'s `panes[].position`) walk the live
  `AvalonDock.DockingManager.Layout` (reached via reflection into the shell's `internal sealed
  DockWorkspace`, then ordinary typed AvalonDock API from there: `Descendents().OfType<
  LayoutAnchorable>()`, `.Parent as LayoutAnchorablePane`, `.GetSide()`) and report which named pane
  (`LeftPane`/`TopPane`/`BottomPane`, matching `Layouts/ILSpy.xml`'s `Name` attributes), which side,
  tab index, and floating/auto-hidden/hidden state a pad's anchorable actually has right now.
- Added matching assertions to `IlSpyAddInTests`: Assemblies alone in `LeftPane`/Left, Search alone
  in `TopPane`/Top, Analyze alone in `BottomPane`/Bottom, none floating/auto-hidden/hidden.

**Verifying this live immediately found the exact bug being tested for** - not hypothetically, a
real, 100%-reproducible one on this machine, including from a *freshly regenerated* (not just
stale) per-user layout file: opening an assembly right after a fresh launch left all three ILSpy
pads tabbed together in whatever pane already existed (`Properties`/`Projects`), on the `Right`
side, instead of their own `LeftPane`/`TopPane`/`BottomPane` groups.

Root cause, traced via `LoggingService` timestamps in the app log, not guessed: `dockingManager
_Loaded` (`AvalonDockLayout.cs` - the WPF `Loaded` routed event that flips `dockingManager.IsLoaded`
true for the *first* time) fired **after** `LayoutConfiguration.CurrentLayoutName = "ILSpy"`'s
setter had already run to completion in this session's timing (opening an assembly right after
startup, via DevFlow, races ahead of the window finishing its first layout pass). Sequence:

1. The setter's own `WorkbenchLayout.LoadConfiguration()` call is a no-op by design while
   `!dockingManager.IsLoaded` ("`LoadConfiguration` doesn't do anything until the docking manager is
   loaded" - existing comment on `dockingManager_Loaded`) - so the "ILSpy" layout template is never
   actually applied at this point, silently.
2. But `onActivating()` (`IlSpyWorkspaceHost.EnsureInitialized`, adding the three ILSpy panes via
   `DockWorkspaceExtensibility.AddToolPane`) has no such guard - it runs anyway, and AvalonDock's
   `AnchorablesSource` binding reactively docks the three new anchorables into whatever pane its
   default insertion strategy (`DockWorkspace.BeforeInsertAnchorable` always returns `false`, i.e.
   "AvalonDock decides") picks - landing them in the pre-existing `Properties`/`Projects` pane.
3. `LayoutConfiguration.OnLayoutChanged` fires at the end of the setter, and
   `ChooseLayoutComboBox.LayoutChanged` reacts by setting `comboBox.SelectedIndex`, which
   synchronously re-enters `OnSelectionChanged` → `StoreConfiguration()` (also has no `IsLoaded`
   guard) - persisting *that* ad-hoc, wrong arrangement to `ConfigLayoutPath/ILSpy.xml`.
4. Once `dockingManager_Loaded` finally does fire and calls the real `LoadConfiguration()`, it
   dutifully restores the file from step 3 - the now-corrupted one, not the AddIn's clean template
   (which never got a chance to load at all). From here on this is self-reinforcing: every later
   `StoreConfiguration()` (including a normal app exit) re-persists the same broken arrangement.

Confirmed by directly reading the saved file after deleting it and relaunching: even with a
guaranteed-fresh `ConfigLayoutPath/ILSpy.xml` (verified absent beforehand), it came back containing
all three ILSpy pads jammed into the shell's default `Properties`/`Projects`
`LayoutAnchorablePane`, and was byte-identical to `Default.xml`'s own freshly-saved content - i.e.
the "ILSpy" layout's own template had *never* been loaded even once.

**Fix**: added a guard to `AvalonDockLayout.StoreConfiguration()` symmetric with
`LoadConfiguration`'s existing one - `if (!dockingManager.IsLoaded) return;` - so nothing gets
persisted before the docking manager has loaded for the first time and had a chance to apply a
layout's real template. The reactive-insert-into-the-wrong-pane in step 2 above can still happen
transiently (that binding has no `IsLoaded` guard either, and doesn't need one - it's an in-memory,
not-yet-observed state), but it's never captured to disk anymore, and gets fully overwritten the
moment the real, guarded `LoadConfiguration()` eventually runs. Verified live: deleted the
still-corrupted saved files, rebuilt, and repeated the exact same fast (`open-assembly`
immediately after launch, no artificial delay) sequence twice from a clean state - both times
`od.ilspy.pane-position`/`od.ilspy.status` reported the correct `LeftPane`/`Left`,
`TopPane`/`Top`, `BottomPane`/`Bottom` placement, one anchorable each. `IlSpyAddInTests` (with the
new position assertions) passes 2/2.

## Real versioned layout DTO, step 1: the Capture/Apply converter (2026-08-03)

Picked up the plan's next concrete item (see "2026-08 architecture update" -> "Docking and layout
replacement" step 4, still flagged as-open there) right after the pad-position bug above made the
motivation concrete: today's "durable format" is still AvalonDock's own `XmlLayoutSerializer`
output (with a version-attribute stamp bolted on, see "Real versioned layout DTO (2026-08-02/03)"
above) - not an OpenDevelop-owned model independent of AvalonDock's object graph, as the
architecture section calls for.

Scoped this as three ordered steps, agreed with the user before starting, doing only step 1 in this
pass:

1. Define the DTO and a `Capture`/`Apply` converter against the *live* `LayoutRoot`, and prove the
   round-trip is correct - without touching `DockWorkspace.SaveLayout`/`RestoreLayout`'s actual
   file format yet.
2. Once step 1 is proven, switch the persisted file format itself from AvalonDock XML to this DTO
   (e.g. as JSON), with AvalonDock XML kept only as a template *import* format (the existing
   `data/layouts/*.xml`/`Layouts/ILSpy.xml` files) - not attempted yet.
3. Persist open document tabs (real identity, not content) - depends on step 2, not attempted yet.

**Step 1, done and verified live.** New `src/Main/SharpDevelop/Workbench/LayoutSnapshot.cs`:

- `LayoutSnapshot` (versioned root, `SchemaVersion` + a tree of `LayoutNodeSnapshot`), with three
  concrete node kinds: `LayoutSplitSnapshot` (mirrors a `LayoutPanel`'s `Orientation` + children),
  `LayoutAnchorablePaneSnapshot` (mirrors a `LayoutAnchorablePane`'s `Name`/`DockWidth`/`DockHeight`
  + an ordered `AnchorableSnapshot` list capturing `ContentId`/`IsSelected`/`IsVisible`), and
  `LayoutDocumentAreaSnapshot` - a deliberate placeholder, not a real model, for wherever the
  document pane sits in the tree (open-tab content/identity is explicitly out of scope for this
  step, same gap `DockWorkspace.LayoutSerializationCallback`'s existing comments already flag for
  the current XML-based path).
- `LayoutSnapshotConverter.Capture(LayoutRoot)` walks `root.RootPanel` recursively into this DTO -
  a pure read, no live-tree mutation.
- `LayoutSnapshotConverter.Apply(LayoutRoot, LayoutSnapshot)` rebuilds the panel tree from the DTO
  and assigns it to `root.RootPanel`, but **reuses** already-existing `LayoutAnchorable` instances
  (matched by `ContentId`, looked up once via `Descendents()`) rather than constructing new ones -
  a freshly-`new`'d `LayoutAnchorable` would have no bound `Content`, since that only happens
  through the `AnchorablesSource` binding `DockWorkspace` sets up once at startup. Any
  `ContentId` the snapshot mentions but that isn't currently registered is skipped, not fabricated.
  Wherever the snapshot has a `LayoutDocumentAreaSnapshot`, `Apply` reuses whichever document
  pane/group is already live in the current tree, preserving currently-open documents across an
  `Apply` even though their content was never part of the snapshot.
- `DockWorkspace` gained one small `internal LayoutRoot Layout => dockingManager.Layout;` seam
  (the `dockingManager` field itself stays `private`) so this converter and its test actions can
  reach the live layout without exposing AvalonDock any wider than that.

**Verified live, not just compile-clean** - added four small, reusable DevFlow actions
(`OpenDevelopDevFlowActions.cs`): `od.layout.pane-position` (the generic, non-ILSpy-specific
version of `od.ilspy.pane-position` above, for any `ContentId`), `od.layout.capture-snapshot`
(calls `Capture`, holds the result in memory for this session), `od.layout.scramble-into-pane`
(test-only: force a comma-separated list of anchorables into some other anchorable's pane, to
manufacture exactly the "everything got tabbed into the wrong pane" corruption the bug above
produced for real), and `od.layout.apply-stored-snapshot` (calls `Apply` with the captured
snapshot). Sequence run against a real app instance: opened an assembly (all three ILSpy pads
correctly in `LeftPane`/`TopPane`/`BottomPane`) -> `capture-snapshot` (`{"paneCount":3}`) ->
`scramble-into-pane` forcing `assemblyListPane`+`searchPane` into `analyzerPane`'s pane (confirmed
via `pane-position`: all three now in `BottomPane`, `siblingCount:3`) -> `apply-stored-snapshot` ->
`pane-position` again on all three: back to `LeftPane`/`Left`, `TopPane`/`Top`, `BottomPane`/
`Bottom`, one anchorable each - a full corruption-and-repair cycle, not just a no-op round-trip.
Also confirmed `Apply`'s `RootPanel` replacement doesn't disturb the document area: `od.active-view`
still reported the same active decompiled document with its full text intact immediately after.
`IlSpyAddInTests` still 2/2 after this change.

Not done in this pass (steps 2-3 above, and this converter's own known gaps): switching
`SaveLayout`/`RestoreLayout` to actually persist this DTO instead of AvalonDock XML; open-document
persistence; and anything beyond the `LayoutPanel`/`LayoutAnchorablePane`/document-area shape this
step's `Capture`/`Apply` models (e.g. `LayoutAnchorablePaneGroup`/floating windows fall through to
the same `LayoutDocumentAreaSnapshot` placeholder as document panes today - harmless for the
current shipped layouts, which don't use them, but not a general solution yet).

## Real versioned layout DTO, step 2: it's now the actual persisted format (2026-08-03)

Continuing directly from step 1 above. Wired `LayoutSnapshotConverter` into
`DockWorkspace.SaveLayout`/`RestoreLayout` themselves:

- `SaveLayout(fileName)` now always writes `JsonSerializer.Serialize(LayoutSnapshotConverter
  .Capture(dockingManager.Layout))` - no more `XmlLayoutSerializer` on the write side at all.
- `RestoreLayout(fileName)` sniffs the file's first non-whitespace character: `{` means the new
  JSON DTO (`RestoreLayoutFromSnapshot` -> `JsonSerializer.Deserialize<LayoutSnapshot>` ->
  `LayoutSnapshotConverter.Apply`), anything else falls through to the existing
  `XmlLayoutSerializer`/`LayoutSerializationCallback` path unchanged - now genuinely an *import*
  format only, exactly the framing the architecture section asks for: every shipped
  `data/layouts/*.xml`/`Layouts/ILSpy.xml` template still works as-is (never rewritten), and any
  legacy per-user save from before this change still loads once via that path, then gets
  naturally upgraded to JSON the next time anything calls `SaveLayout`.
- File names are unchanged (still `<LayoutName>.xml` per `LayoutConfiguration.CurrentLayoutFileName`
  - not renamed to `.json`) - format is detected by content, not extension, so nothing about
  `LayoutConfiguration`'s existing file-path logic needed to change.
- `LayoutNodeSnapshot` gained `[JsonPolymorphic]`/`[JsonDerivedType]` attributes (three concrete
  node kinds: `split`/`anchorablePane`/`documentArea`) so `System.Text.Json` can round-trip the
  DTO's small class hierarchy without a hand-written converter.
- `LayoutSnapshotConverter.Apply` fix made while wiring this in (not caught by step 1's testing,
  since that never exercised the `IsVisible: false` path): the earlier version called
  `anchorable.Show()`/`.Hide()` *while still building* each `LayoutAnchorablePane`, before the
  rebuilt pane was attached anywhere - `Hide()`/`Show()` reparent the anchorable based on its
  *current* parent chain, which at that point was either the old tree or nothing, not the new pane
  being built, so the visibility state landed on the wrong object graph. Fixed by deferring all
  `IsSelected`/`IsVisible`/`CanDockAsTabbedDocument` mutation to a second pass, run only after
  `root.RootPanel` is fully assigned to the rebuilt tree - each anchorable's parent chain is then
  the real, live one, and `Hide()` correctly relocates it into `LayoutRoot`'s own `Hidden`
  collection instead of fighting the rebuild.
- `AvalonDockLayout.ReadAnchorableContentIds` (used by `LoadLayout` to figure out which
  currently-registered `ToolPaneModel`s aren't part of the layout being switched to, so they can be
  excluded rather than left dangling) parsed `//@ContentId` via `XmlDocument` unconditionally -
  **would have silently broken every layout switch** once `SaveLayout` started writing JSON: caught
  via `LoggingService`'s own warning (`Could not read anchorable ContentIds from layout file`)
  during verification, not by inspection. An `XmlException` there was being caught and treated as
  "no content IDs" - so after switching to any layout whose file was now JSON, *every* registered
  pane (including all three ILSpy ones) would look like "not part of this layout" and get removed.
  Fixed with the same content-sniff approach as `RestoreLayout`: JSON walks the parsed
  `JsonDocument` tree collecting every `"ContentId"` property value (structure-agnostic, so it
  can't drift out of sync with `LayoutSnapshot`'s own shape), XML keeps the original XPath query.

**Verified live end to end, including a real process restart** (not just an in-memory
capture/apply cycle, which step 1 already covered) - this is the scenario that actually matters:
does a layout saved in the new format survive being read back by a *different* process instance,
the way a real app relaunch works.

1. Deleted both `ConfigLayoutPath/{Default,ILSpy}.xml`, launched fresh, opened an assembly -
   `od.layout.pane-position` confirmed `LeftPane`/`Left`, `TopPane`/`Top`, `BottomPane`/`Bottom` (the
   template import path, still XML, still works).
2. Confirmed on disk: `ConfigLayoutPath/ILSpy.xml`'s content now starts with
   `{"SchemaVersion":1,"Root":{"$type":"split",...` - `SaveLayout` really did write JSON this time,
   under the unchanged `.xml` filename.
3. Killed the process, relaunched (a genuinely new process, not a re-used one - confirmed no leftover
   listener on the DevFlow port beforehand), opened the assembly again - **no**
   `Could not read anchorable ContentIds` warning this time (the fix from above), all three ILSpy
   panes still registered, and `od.layout.pane-position` again reported the correct
   `LeftPane`/`TopPane`/`BottomPane` placement - loaded purely from the JSON file written by the
   *previous* process, proving the round-trip survives a real restart, not just staying correct
   because nothing ever unloaded.
4. Cycled `od.workbench.switch-layout` through `Debug` -> `Plain` -> `ILSpy` -> `Default` in one
   session (each switch both saves the outgoing layout and loads the incoming one) - zero
   warnings/exceptions in the log, and `od.pads` afterward still listed the full, unchanged set of
   25 registered pads (nothing silently dropped by the `ReadAnchorableContentIds` exclusion logic).
5. `IlSpyAddInTests` (with the pad-position assertions from the previous section) still 2/2 -
   confirmed on a clean `ConfigLayoutPath` so this run genuinely exercised the import-then-JSON
   path, not a cached prior state.

Not done in this pass (step 3, and this converter's own remaining shape gaps - unchanged from step
1, see that section): open-document persistence, and anything beyond the
`LayoutPanel`/`LayoutAnchorablePane`/document-area shape `Capture`/`Apply` model
(`LayoutAnchorablePaneGroup`/floating windows still fall through to the document-area placeholder -
harmless today since no shipped layout uses them, but not a general solution).

## Real versioned layout DTO, step 3 (first slice): capturing which real documents are open (2026-08-03)

Continuing the plan from steps 1-2 above. Per the research done before starting this slice: there
is no existing "reopen previously open files" feature anywhere in this codebase to build on
(`IRecentOpen`/`RecentOpen.cs` is just an MRU menu list, never auto-replayed) - this is genuinely
greenfield. Also confirmed a real blocker for the *reopen* half specifically: a document's
`PrimaryFileName` is only a real, reopenable disk path for ordinary file-backed `IViewContent`;
virtual documents (ILSpyAddIn's `ilspy://` decompiled views, the Start Page) have no such thing and
would need addin-specific "can this be reopened, and how" logic, not a generic file-path replay.

Scoped this the same way as steps 1/2 - prove the *capture* half is correct and low-risk before
touching anything that reopens documents on restore (the actually risky half):

- `LayoutDocumentSnapshot { FileName, IsActive }` and a `LayoutSnapshot.Documents` list.
- `LayoutSnapshotConverter.Capture(DockWorkspace)` (new overload alongside the existing
  `Capture(LayoutRoot)`) iterates `workspace.Documents`, and for each one whose
  `ActiveViewContent.PrimaryFile` is real and not `IsUntitled`, records its `FileName` and whether
  it's `workspace.ActiveDocument`. Virtual documents (`PrimaryFile == null` - ILSpy's
  `DecompiledViewContent`/`DecompiledSelectionViewContent`, the Start Page) are silently skipped,
  not recorded as broken/unreopenable entries - there's nothing wrong to report, they're simply
  outside this slice's model.
- Deliberately **not** wired into `SaveLayout`/`RestoreLayout` - `LayoutSnapshot.Documents` is
  populated by this new overload but the two places that actually persist/restore layouts still
  call the `LayoutRoot`-only overload, so nothing about real save/load behavior changed in this
  pass. No reopen-on-restore logic exists yet at all.

Verified live via the existing `od.layout.capture-snapshot` action (now reports `documents` too,
sourced from `LayoutSnapshotConverter.Capture(DockWorkspace.Current)` instead of the
`LayoutRoot`-only overload): with only the ILSpy virtual whole-module document open,
`capture-snapshot` correctly reported `"documents":[]` - the virtual document is excluded, not
misreported. Opened a real file (`od.open-file` on this technote itself) alongside it and
captured again: `"documents":[{"FileName":".../doc/technotes/ilspy.md","IsActive":true}]` - the
real file is recorded with the correct path and active-flag, the still-open virtual document still
excluded. `IlSpyAddInTests` 2/2 (one run hit the pre-existing, already-documented AvalonDock
focus-race flakiness on `od.ilspy.click-reference` - confirmed not a regression from this slice,
since nothing here is wired into any runtime path yet - and passed cleanly on immediate rerun).

**Status update**: the versioned layout DTO plan (steps 1-3) now has real Capture-side code for
every one of pane placement, persisted-format switch, and document identity. What's left,
genuinely not attempted: actually reopening documents from a snapshot on restore (needs the
addin-specific "reopenable?" hook noted above for virtual documents, and ordinary
`SD.FileService.OpenFile` for real ones), and wiring `LayoutDocumentSnapshot` capture into
`SaveLayout` itself (trivial once reopen exists - pointless before it, since nothing would ever
read the captured data).

## Real versioned layout DTO, step 3 completed: documents actually reopen on restore (2026-08-03)

Continuing directly from the first slice above. Widened `Capture(DockWorkspace)` from "real files
only" to every document's `IViewContent.PrimaryFileName` regardless of real vs. virtual, and added
`LayoutSnapshotConverter.ReopenDocuments`, wired into `DockWorkspace.RestoreLayoutFromSnapshot`
right after `Apply`. This was simpler than originally scoped: the "virtual documents need a
special addin-specific reopen hook" concern raised while planning step 3 turned out to already be
solved by existing infrastructure - `ILSpyDisplayBinding` is already registered
(`ILSpyAddIn.addin`, `fileNamePattern = "^ilspy://"`) to resolve exactly that scheme through the
ordinary `SD.FileService.OpenFile` pipeline (the same one `OpenLoadedModuleInILSpyCommand.cs`
already uses to open one), so `PrimaryFileName` is a general enough identity for both kinds with
no new extension point needed.

**Two real bugs found and fixed while verifying this live** - both pre-existing, both only
actually exercised end-to-end by this new reopen path:

1. **`DecompiledTypeReference.ToFileName()`/`FromFileName()`'s URI round-trip breaks on macOS/Linux
   absolute paths.** `"ilspy://" + AssemblyFile` produces three consecutive slashes when
   `AssemblyFile` is itself a Unix absolute path starting with `/` (e.g.
   `ilspy:///Users/.../DebugTestApp.dll/module.cs`) - and `FileUtility.NormalizePath` (which every
   `FileName` construction runs through, via `PathName`'s constructor) doesn't preserve three
   consecutive slashes faithfully, silently collapsing to two and corrupting the parse. Measured
   directly: reopening a persisted `ilspy://` document threw
   `DirectoryNotFoundException: Could not find a part of the path '.../ilspy:/Users/.../module.cs'`
   - the path had been treated as relative to the app's working directory instead of an absolute
   Unix path. This is genuinely latent, pre-existing infrastructure - the tree-click-driven
   decompile path never round-trips through the string form at all (it passes `AssemblyFile`/
   `Type` directly), and `OpenLoadedModuleInILSpyCommand.cs`'s existing `ToFileName()` call is
   exposed to exactly the same bug on this platform, just never exercised end-to-end before now.
   Fixed symmetrically in both directions: `ToFileName()` strips exactly one leading separator from
   `AssemblyFile` before concatenating (a no-op on Windows, which never starts with one),
   `FromFileName()` restores it. Verified live: `ilspy://Users/.../DebugTestApp.dll/module.cs`
   (two slashes, no leading-slash collision) now round-trips and decompiles correctly after a
   process restart.

2. **`ReopenDocuments`'s first version made an unrelated test flake at a ~75% rate** by reopening/
   reselecting every recorded document on *every* `RestoreLayout` call, not just the first one -
   `RestoreLayout` runs on every layout switch, not only at app startup, so switching back to a
   layout whose documents never actually closed would still force `SD.FileService.OpenFile`'s
   `switchToOpenedView` path to call `SelectWindow()` on whichever document the snapshot recorded
   as active, fighting whatever the caller had just navigated to immediately before. Measured: three
   of four consecutive `IlSpyAddInTests` runs failed on the reference-click-navigation step's
   `caretLine` assertion (landed on line 1, i.e. still on the whole-module document, not the type
   it should have jumped to) - a false positive traced back to exactly this. Fixed by skipping any
   document the snapshot recorded that `SD.FileService.GetOpenFile` already finds open - a
   same-session layout switch with nothing new to restore is now a true no-op, same as before this
   slice existed. Confirmed with four consecutive clean `dotnet test` runs after the fix (zero
   failures, versus 3/4 failing before it).

**Verified live end to end** (same-session layout switch, and a genuine process restart):
opened a real file (this technote) and the ILSpy whole-module document together, forced a save by
switching away and back (`Default` -> `ILSpy`), and `od.layout.capture-snapshot` reported both
still open with the correct active flag preserved; the ILSpy document's content was confirmed real
(`od.active-view`/`od.ilspy.status` showing the actual decompiled C#, not a placeholder). `dotnet
test` on `IlSpyAddInTests` passed cleanly four times in a row after the fixes above.

**Status update**: the versioned layout DTO plan (steps 1-3) is now fully implemented - pane
placement, the actual persisted format (JSON, AvalonDock XML as import-only), and document
identity/reopen all work and are live-verified. Remaining known gaps, all pre-existing and
explicitly out of scope for this pass (see each step's own section above for why): open-document
*content* isn't part of the DTO (identity/reopen only), and the panel-shape model doesn't cover
`LayoutAnchorablePaneGroup`/floating windows (harmless today - no shipped layout uses them).

## AvalonEdit line-number margin misaligned with mixed line heights (2026-08-03)

User-flagged (spotted while reviewing this technote's own `.md` rendering, which uses
`MarkDownWithFontSize-Mode.xshd` - H1-H6 headings rendered at 15-30pt vs. ~13pt body text, per
`od.active-view`'s `syntaxHighlighting: "MarkDownWithFontSize"`). Root cause:
`LineNumberMargin.OnRender` (`src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/Editing/
LineNumberMargin.cs`) drew every line's number aligned to `VisualYPosition.TextTop` of that visual
line's own text - correct only when every line has the same height. A heading's `VisualLine` row
is much taller than its neighbors (font-size-driven, not a fork-local change - confirmed via `git
log` that this file and the whole variable-height rendering path, including `HeightTree`, are
stock upstream AvalonEdit, not a regression introduced here), so top-aligning left the number
sitting at the very top of a tall heading row instead of level with its text - looking
disconnected from the line it labels whenever row heights are mixed, exactly the "行号显示对不上
...不同行高混合的情况" the user reported.

Fixed by centering the number within the visual line's full row height instead:
`line.VisualTop + (line.Height - text.Height) / 2`, replacing the `GetTextLineVisualYPosition(...,
TextTop)` call. `VisualLine.Height`/`VisualTop` are the same authoritative values the `HeightTree`
already provides for scrolling and text rendering - this isn't a new height-tracking mechanism,
just using the *centering* math instead of *top-alignment* math against data the margin already
had access to.

**Verified via DevFlow, not just build success** - since this environment runs the app off-screen
(no screenshot capability available here), added a small diagnostic action,
`od.file.visual-lines` (`OpenDevelopDevFlowActions.cs`), that reports each currently-rendered
`VisualLine`'s `LineNumber`/`VisualTop`/`Height` plus both the old and new Y-position formulas
side by side. Run against this technote's own `.md` file (H1 at line 1, H2 at line 3): line 1
(`Height: 35`) - old formula gives `16` (pinned near the top of its tall row), new formula gives
`9.5` (correctly centered - `(35-16)/2`); line 3 (`Height: 31`) - old `63`, new `57.5`, a 5.5px
shift. Ordinary body-text lines (`Height: 15`, matching the digit glyph's own height) barely move
(within 0.5px, sub-pixel rounding) - confirming the fix only changes anything for the
mixed-height case it targets, not the common uniform-height case. `IlSpyAddInTests` still 2/2
after this change (shared AvalonEdit infrastructure, worth the regression check even though this
addin doesn't touch Markdown itself).

## Legacy Pad migration, first slice - and the silent-drop bug it exposed (2026-08-03)

Started item 4 of "Docking and layout replacement" (migrate the 11 remaining legacy AddInTree
`<Pad>` tool panes to the modern `ToolPaneModel` pattern), plus the part of item 1 it naturally
drags in. Three things came out of it.

**1. `AvalonDockLayout`'s legacy→modern routing is no longer one hardcoded class name.**
`GetMefToolPaneContentId` was literally
`if (padDescriptor.Class == typeof(ProjectBrowserPad).FullName) return "ProjectBrowser";` - one
comparison per migrated pad, living in the shell. Replaced with a lookup over
`dockWorkspace.ToolPanes` on a new `ToolPaneModel.LegacyPadClass` property, so a migrated pad
declares its own legacy identity in its own constructor and the shell needs no change per pad.
`ProjectBrowserViewModel` now sets `LegacyPadClass = typeof(ProjectBrowserPad).FullName`; verified
live that Projects still routes through the MEF path and docks at `LeftPane`/`Left` exactly as
before.

**2. `Outline` migrated** as the first real pad through that generalized path:
`OutlineViewModel` (MEF-exported `ToolPaneModel`, same shape as `ProjectBrowserViewModel`) holds
the real behavior; `OutlinePad` stays as a thin shim so the AddInTree `<Pad>` entry's
title/icon/category/default-position metadata still resolves to a constructible type, and so
callers reaching `PadDescriptor.PadContent` directly still get real content. The shim delegates to
the same view model rather than duplicating it.

**3. The bug that made this look impossible for a long time: `DockWorkspace.ToolPanes` silently
dropped its entire pane set if any single part's constructor threw.** The getter did
`foreach (var pane in ExportProvider.GetExportedValues<ToolPaneModel>("ToolPane").OrderBy(p => p.Title))`.
`GetExportedValues` is lazy and `OrderBy` buffers it, so one throwing constructor aborted the whole
enumeration - and because `toolPanesView` was assigned only *after* the loop, the failure left it
null with `toolPanes` already partly filled, so the next access re-enumerated and re-added
duplicates. Worst of all it surfaced **no diagnostics whatsoever**. `OutlineViewModel`'s first
version touched `SD.Workbench` in its constructor, which is null that early (MEF composition runs
from `AvalonDockLayout.BindSources()`, before the workbench is registered) - so adding one pane made
*every* MEF pane vanish, with the only symptom being a wrong pane count and no error anywhere.
That's what produced hours of contradictory readings (`count:1`, `count:3` with only runtime-added
ILSpy panes, nondeterministic across runs - the same part constructs fine once the service exists,
so the outcome depended on which code path touched `ToolPanes` first).

Fixed by constructing parts one at a time via `GetExports<ToolPaneModel, IMetadata>(...)` and
guarding each `.Value` individually: one broken pane now costs exactly that pane, logged by type,
and materializing into a local list first means a failure can't leave the collection half-filled or
duplicated. `OutlineViewModel` also got the deferred-subscription treatment (`EnsureSubscribed`,
same shape as `CodeCoverageService.TryHookViewOpened`'s fix for the identical early-startup hazard),
and the shim calls it on construction so the legacy route - which never calls
`ToolPaneModel.Show()` - doesn't hand AvalonDock an empty `ContentPresenter`.

**Diagnosis method worth recording**: the breakthrough came from a temporary DevFlow action that
queried `MetadataReader.Read(assembly)` and `GetExports<ToolPaneModel, IMetadata>("ToolPane")`
directly, which returned **2** (both panes, correct metadata/contract names) while
`DockWorkspace.Current.ToolPanes` returned **1** - proving instantly that MEF registration was
never the problem and the loss was downstream, inside this getter. Decompiling
`BindExports`/`ExportProviderAdapter`/`MetadataReader` with this project's own embedded ILSpy (and
`ilspycmd` for >2000-char output) ruled out the composition library first. Before that, every
theory about attributes, `[Shared]`, contract names or assembly scanning was wrong; a standalone
2-class repro of the same registration shape returned 2 as well, which is what redirected the
search away from MEF.

**Also learned, and worth not re-discovering**: `count:1` for `ToolPanes` *after* startup is not
necessarily a bug at all - `AvalonDockLayout.LoadLayout` deliberately removes any registered pane
the restored layout file doesn't name (into `layoutExcludedPanes`, "a named layout shows exactly
the panes it contains"). Since `Outline` isn't in `Default.xml`, it's excluded post-population, and
`od.show-pad "Outline"` then legitimately falls through to the legacy `AvalonPadContent` route -
which works, and whose anchorable shows up under ContentId
`ICSharpCode.SharpDevelop.Gui.OutlinePad` (verified live) rather than `Outline`. Reading
`ToolPanes` after that exclusion and concluding "registration failed" is the trap; the
`[TOOLPANES] populated 2 ...` log line at population time is the reliable signal.

**Verified**: `populated 2 MEF tool pane(s): ProjectBrowser, Outline` at startup with zero part
failures; Projects still docks correctly; the Outline anchorable materializes via the shim route.
`IlSpyAddInTests` passed 7 of 8 consecutive runs, the single failure being the pre-existing,
already-documented `od.ilspy.click-reference` dispatcher-tick race (established earlier in this
session, before any of this work, and reproduced manually 5/5 successfully outside the test).

**Not done** (deliberately, and not attempted): the other 10 legacy pads. `Outline` was chosen as
the cheapest possible validation of the pattern; the remaining easy tier
(`DefinitionViewPad`, `BookmarkPad`) should be mechanical now that `LegacyPadClass` exists and
`ToolPanes` no longer hides constructor failures, while `ErrorListPad`/`ClassBrowserPad`/
`OutputPad`/`SideBar`/`FileScout` need per-call-site dependency mapping first (same care as the
`AssemblyTreeModel` migration). Making `Outline` render as a *docked, visible* pane in a layout
that doesn't name it is a separate question about layout templates, not about this migration.

## Legacy Pad migration, second slice: DefinitionViewPad (2026-08-03)

Same shape as `Outline`: `DefinitionViewViewModel` (MEF-exported `ToolPaneModel`, in the App
project since `ToolPaneModel` isn't reachable from Base) holds the real behavior (AvalonEdit
control showing the definition under the caret, refreshed via a `DispatcherTimer` and
`SD.ParserService.ParseInformationUpdated`); `DefinitionViewPad` is a thin shim so the AddInTree
`<Pad>` entry still resolves and any direct `PadDescriptor.PadContent` access still gets real
content. `LegacyPadClass` set to the shim's type, same as `Outline`/`ProjectBrowser`.

Applied the same deferred-subscription guard as `Outline` even though `IParserService` starts
before workbench initialization (so is very unlikely to be the timing hazard `SD.Workbench` was) -
cheap insurance against the exact failure mode ("Failed to create tool pane... - skipping it" now
at least gets logged instead of silently vanishing, but avoiding the failure in the first place is
still better than relying on the log).

Verified live: build clean, `[TOOLPANES]`-style pane count unaffected, `od.show-pad` finds it,
routes through the legacy shim (not in `Default.xml`, same as `Outline`), and the anchorable
materializes under `ICSharpCode.SharpDevelop.Gui.DefinitionViewPad`. `od.pads` still lists both
migrated pads with correct titles/categories/default positions - no regression to AddInTree
metadata resolution. `IlSpyAddInTests` 3/4 clean runs (the one failure being the same pre-existing
dispatcher-tick race noted throughout this session, unrelated to pad migration).

Two pads down (`Outline`, `DefinitionView`), nine to go
(`BookmarkPad`/`PropertyPad`/`TaskListPad`/`SearchResultsPad`/`ErrorListPad`/`ClassBrowserPad`/
`OutputPad`/`SideBar`/`FileScout`) - `BookmarkPad` is next in the easy tier, but it owns a XAML
`UserControl` (`BookmarkPadContent`) that also needs relocating out of the Base project, a wrinkle
`Outline`/`DefinitionView` didn't have (both were code-only controls).

## BookmarkPad reclassified: not actually easy tier (2026-08-03)

Looked at `BookmarkPad` next (the third "easy tier" item from the original survey) before
migrating it, and stopped: `BookmarkPadBase` (the shared abstract base `BookmarkPad` derives from)
is also the base class for `src/AddIns/Debugger/Debugger.AddIn/Pads/BreakPointsPad.cs` - a
different pad in a different AddIn assembly. Its toolbar commands
(`NextBookmarkPadCommand`/`PrevBookmarkPadCommand`/`DeleteMark`/`DeleteAllMarks`/`EnableDisableAll`
in `BookmarkPadToolbarCommands.cs`) all cast `this.Owner` to `BookmarkPadBase` directly - changing
that base type to a `ToolPaneModel` would mean updating the toolbar-owner contract for both pads
across two assemblies, not the same "one pad, one file pair" shape `Outline`/`DefinitionView` were.

Reclassifying this to medium/hard tier rather than rushing it - it needs the same kind of
call-site mapping the original `AssemblyTreeModel` migration got, this time across an AddIn
boundary, before attempting it. Not attempted in this pass.

**Status after this pass**: 2 of 11 legacy pads migrated (`Outline`, `DefinitionView`), the
`LegacyPadClass`/robust-`ToolPanes` foundation is proven across both, `ProjectBrowser` is
unaffected. Remaining 9: `BookmarkPad`+`BreakPointsPad` (now known to be linked, medium tier),
`PropertyPad`/`TaskListPad`/`SearchResultsPad` (medium, per the original survey), `ErrorListPad`/
`ClassBrowserPad`/`OutputPad`/`SideBar`/`FileScout` (hard tier, broad fan-out - unexamined in
detail yet).

## Legacy Pad migration, third slice: TaskListPad, and PropertyPad/SearchResultsPad reclassified (2026-08-03)

**`TaskListPad` migrated** - same shape as `Outline`/`DefinitionView`: `TaskListViewModel` (MEF
`ToolPaneModel` in the App project) holds the real behavior (comment-task list, scope filter,
per-token toolbar checkboxes), `TaskListPad` is a thin shim. This one had an extra wrinkle:
`TaskListPadCommands.cs`'s toolbar items (`SelectScopeComboBox`, `TaskListTokensToolbarCheckBox`)
referenced `TaskListPad.Instance` - a static singleton only ever set when the shim class is
actually constructed, which no longer happens on the common MEF-first path (the pad defaults
*visible*, so unlike `Outline`/`DefinitionView` it usually never falls through to the legacy
route at all). Rewrote those toolbar items to resolve `TaskListViewModel` directly via MEF instead
of through the shim - simpler than trying to keep an `Instance` singleton in sync with two
different construction paths, and `TaskListPadCommands.cs` had to move into the App project
alongside `TaskListPad` regardless (both are in the same namespace, referencing each other, and
that namespace's real content is now split across two assemblies otherwise).

Verified live: zero MEF construction failures, `Task List` docks at `Bottom` (matches its
non-hidden default), UI tree shows a real `ListView` under a `Task List`-titled tab. `dotnet test`
3/3 clean.

**`PropertyPad` and `SearchResultsPad` are blocked, not just harder** - looked at both before
migrating and stopped, for a different reason than `BookmarkPad`'s (shared base class): both
expose **static members that other AddIn assemblies call directly** -
`PropertyPad.ActiveContainer`/`PropertyPad.Grid` from `WpfDesign.AddIn`, and
`SearchResultsPad.Instance` from `SearchAndReplace`/`ResourceToolkit`/`AvalonEdit.AddIn`/
`TypeScript`/`CSharpBinding` (five different AddIns). Every AddIn project references only the Base
project (`ICSharpCode.SharpDevelop.csproj`), never the App project - confirmed by checking
`WpfDesign.AddIn.csproj`'s `<ProjectReference>` list, which has no reference to `SharpDevelop.csproj`/
`OpenDevelop.dll` at all. Since `ToolPaneModel` only exists in the App project, moving either pad
class there the way `Outline`/`DefinitionView`/`TaskListPad` were moved would break every one of
those callers at compile time - a real, structural blocker, not a matter of care/risk like
`BookmarkPad`.

This points at the actual prerequisite for unblocking the rest of the easy/medium tier at once:
relocate `PaneModel`/`ToolPaneModel` (and whatever `ObservableObjectBase` base they need) down
into a project every AddIn can already reference - Base itself, or a new small shared library -
exactly what the architecture doc's "Shared modern shell primitives" section already called for.
That's a separate, foundational piece of work, not attempted in this pass.

**Status after this pass**: 3 of 11 legacy pads migrated (`Outline`, `DefinitionView`,
`TaskListPad`). `BookmarkPad`+`BreakPointsPad` remain blocked on their shared base class;
`PropertyPad`/`SearchResultsPad` remain blocked on `ToolPaneModel`'s current location being
unreachable from other AddIns. `ErrorListPad`/`ClassBrowserPad`/`OutputPad`/`SideBar`/`FileScout`
(hard tier) still unexamined - likely to have the same or worse fan-out, cross-assembly issues.

## Foundational move: PaneModel/ToolPaneModel relocated to the Base project (2026-08-03)

This is the actual prerequisite the previous slice identified: `PropertyPad`/`SearchResultsPad`
(and any future pad) couldn't migrate to `ToolPaneModel` because that type lived in the App
project (`SharpDevelop.csproj`/`OpenDevelop.dll`), which almost no AddIn references - they only
reference the Base project (`ICSharpCode.SharpDevelop.csproj`). Moved
`ObservableObjectBase`/`PaneModel`/`ToolPaneModel`/`LegacyToolPaneModel` from
`src/Main/SharpDevelop/ViewModels/` into `src/Main/Base/Project/ViewModels/` (same namespace,
`ICSharpCode.SharpDevelop.ViewModels`, so no call site needed a `using` change) - exactly the
"Shared modern shell primitives" the architecture doc's target design already called for.

**The one real dependency that had to be broken first**: `PaneModel`'s `CloseCommand` called
`DockWorkspace.Current?.Remove(model)` directly - `DockWorkspace` is `internal sealed` and lives
in the App project, the one thing standing between `PaneModel` and being Base-portable. Introduced
`IPaneModelHost` (one method, `Remove(PaneModel model)`) in the same file/namespace as `PaneModel`;
`DockWorkspace` implements it and registers itself via `SD.Services.AddService(typeof(IPaneModelHost),
this)` in its constructor. `CloseCommandImpl.Execute` now resolves it through `SD.Services` instead
of a direct type reference - the same "shell owns the mechanism, resolved through the service
container" pattern already used for `IWorkbench`/`IStatusBarService` elsewhere in this codebase,
not a new one invented for this.

**A real, pre-existing regression surfaced immediately on rebuild** (from the `Outline`/
`DefinitionView`/`TaskListPad` migrations two slices ago, never caught because those slices only
rebuilt the App project and `ILSpyAddIn`, never the other AddIns): `WpfDesign.AddIn`'s
`Commands/Pads.cs` did `SD.Workbench.GetPad(typeof(OutlinePad))`/`typeof(PropertyPad)` - a
compile-time type reference that broke the moment those pad classes moved out of the Base project
`WpfDesign.AddIn.csproj` references. Fixed generally rather than patching around it: added
`IWorkbench.GetPad(string className)` (the string-keyed form `GetPad(Type)` was always just a
`pad.Class == type.FullName` comparison underneath, now the public shape too), updated
`WpfDesign.AddIn`'s two commands to look up by class-name string instead of `typeof(...)`, and
added the matching overload to `WixBinding`'s `MockWorkbench` test double. This is the durable fix
for the general problem, not just this one call site - any future AddIn wanting to reach a pad
whose real implementation lives in the App project now has a supported way to do it.

**Verified**: Base project builds clean with the four files now inside it;
`SharpDevelop.csproj`/`ILSpyAddIn.csproj`/`WpfDesign.AddIn.csproj` all build clean afterward (the
`WpfDesign.AddIn` failure above was caught and fixed in this same pass, not a leftover). Live:
fresh launch, zero exceptions, all three already-migrated pads (`ProjectBrowser`/`Outline`/
`Task List`) still dock and route correctly. Exercised the new `IPaneModelHost` indirection
end-to-end, not just by inspection - called `ProjectBrowser`'s real `CloseCommand` (the same
`ICommand` its close button binds to) via a temporary diagnostic action and confirmed
`IsVisible` flipped `true -> false`, proving the service-lookup chain (`PaneModel.CloseCommand` ->
`SD.Services.GetService(typeof(IPaneModelHost))` -> `DockWorkspace.Remove`) actually reaches the
real workspace, not just that it compiles. `IlSpyAddInTests` 2/3 (the known pre-existing
dispatcher-tick flake, unrelated).

**Status**: the structural blocker for `PropertyPad`/`SearchResultsPad` is gone -
`ToolPaneModel` is now reachable from every AddIn. Migrating those two pads themselves (updating
their own external callers - `WpfDesign.AddIn`'s static `PropertyPad.ActiveContainer`/`.Grid`
reads, and `SearchResultsPad.Instance`'s five call sites across as many AddIns - to resolve the new
view-model via MEF the same way `TaskListPad`'s toolbar items were updated) is still separate,
not-yet-done work, but no longer blocked on anything architectural.

## Legacy Pad migration, fourth slice: PropertyPad unblocked, and a real "default-visible pad never subscribes" bug (2026-08-03)

With `ToolPaneModel` now reachable from every AddIn (previous slice), migrated `PropertyPad` -
same shape as `Outline`/`DefinitionView`/`TaskListPad`: `PropertyPadViewModel` (MEF `ToolPaneModel`
in the App project) holds the real behavior (Xceed property grid, active-content tracking),
`PropertyPad` is a thin shim.

**The actual unblocking work was the external-caller migration**, not the pad itself - `PropertyPad`
had two kinds of caller `Outline`/`DefinitionView`/`TaskListPad` didn't:

- **Static member access from another AddIn**: `WpfDesign.AddIn`'s `WpfDesignDevFlowActions.cs`
  called `PropertyPad.Grid`/`PropertyPad.ActiveContainer` directly - a compile-time reference no
  amount of `LegacyPadClass`-style indirection fixes, since it's not going through
  `AvalonDockLayout`/`PadDescriptor` routing at all. Introduced `IPropertyPadHost` (`Grid`,
  `ActiveContainer`, `UpdateSelectedObjectIfActive`) in the Base project, next to
  `PropertyContainer`; `PropertyPadViewModel` implements it and registers via
  `SD.Services.AddService(typeof(IPropertyPadHost), this)` - the same pattern as `IPaneModelHost`
  from the previous slice, not a new one. `WpfDesignDevFlowActions.cs` and
  `PropertyContainer.cs`/`PropertyPadCommands.cs` (both already in Base, both referenced
  `PropertyPad` directly) now resolve through this service instead.
- **`typeof(PropertyPad)` from Base-project code that predates this whole migration effort**:
  `AbstractProjectBrowserTreeNode.ShowProperties()` and `FormsDesigner`'s
  `FormsCommands.cs`'s `ShowPropertiesWindow.Run()` both did
  `SD.Workbench.GetPad(typeof(PropertyPad))` - broken the moment `PropertyPad` moved to the App
  project, same as `WpfDesign.AddIn`'s `Commands/Pads.cs` was in the previous slice. Fixed with the
  same `IWorkbench.GetPad(string className)` overload already added for that.

**Also caught, this time actually a live-verified regression, not just a build failure**: the
"defer subscription to first real use instead of the constructor" pattern from
`Outline`/`DefinitionView`/`TaskListPad` deferred *only* to `Show()` - correct for those three,
which all default hidden and are only ever shown by a user/test explicitly activating them. But
`PropertyPad` defaults *visible* (`defaultPosition = "Right"`, no `Hidden`), and on the ordinary
MEF-composed path nothing ever calls `Show()` on an already-visible pane at all - `IsVisible=true`
is just set once in the constructor and AvalonDock renders it because of that, not because
anything invoked `Show()`. Running the real `WpfDesignerTests` suite (not just a manual probe)
caught this immediately: `SelectControlOnSamplePane_ShowsSelectionInPropertiesPad` and
`SelectControl_EditingContentInPropertiesPad_UpdatesAndSavesXaml` both failed with the Properties
pad reporting no selection at all, even though `PropertyPadGrid` itself resolved fine - the
subscription that keeps the grid's `SelectedObject` in sync with the WPF designer's selection had
simply never happened. Fixed by calling `EnsureSubscribed()` from every externally-reachable entry
point (`Grid`, `ActiveContainer`, `UpdateSelectedObjectIfActive`), not only `Show()` - the first
real touch from any direction now triggers it. `Outline`/`DefinitionView`/`TaskListPad` don't need
this same fix since `Show()` is the only way anything ever reaches them (they default hidden).

**Verified**: Base/App/`ILSpyAddIn`/`WpfDesign.AddIn` all build clean. `FormsDesigner`/`WixBinding`
edits (both also fixed the same `typeof(PropertyPad)` pattern) could **not** be build-verified in
this environment - both target `.NETFramework,Version=v4.5`, whose reference assemblies aren't
installed here (pre-existing environment gap, unrelated to this change) - correctness there rests
on inspection only (the same mechanical `GetPad(string)` substitution already proven elsewhere).
Live: fresh launch, zero exceptions, Properties pad docks at `Right` correctly. `WpfDesignerTests`
5/5 clean (twice in a row) after the subscription fix, versus 2 failures before it.
`IlSpyAddInTests` clean too (shared infrastructure, worth the regression check).

**Status**: 4 of 11 legacy pads migrated (`Outline`, `DefinitionView`, `TaskListPad`,
`PropertyPad`). `SearchResultsPad` is next - structurally unblocked the same way now, but has five
external call sites across five different AddIns (`SearchAndReplace`, `ResourceToolkit`,
`AvalonEdit.AddIn`, `TypeScript`, `CSharpBinding`) via its `Instance` static property, more than any
pad migrated so far. `BookmarkPad`+`BreakPointsPad` remain blocked on their shared base class
(a different, harder problem than the reachability one this and the previous slice solved).

## Legacy Pad migration, fifth slice: SearchResultsPad, and a virtualization false alarm (2026-08-03)

`SearchResultsPad` had the most external callers of any pad so far (8+ call sites across
`SearchAndReplace`, `ResourceToolkit`, `AvalonEdit.AddIn`'s `OpenLensRenderer`, `TypeScript`,
`CSharpBinding`), all through the same `SearchResultsPad.Instance` static singleton, plus a set of
genuinely stateless factory methods (`CreateSearchResult`, `CreateInlineBuilder`) that never
touched pad state at all.

**Split into two independent pieces**, mirroring the reason each half's callers exist:

- `ISearchResultsHost` + `SearchResultsHost.Current` (Base project,
  `Editor/Search/ISearchResultsHost.cs`) - same `SD.Services.AddService`/static-resolver pattern
  as `IPropertyPadHost`, replacing every `.Instance` call site. Registered *eagerly* in
  `SearchResultsPadViewModel`'s constructor, unlike the deferred-subscription pattern used
  everywhere else this migration - `SD.Services.AddService` itself never touches `SD.Workbench` or
  anything else not ready yet, and external callers need to resolve the host correctly on their
  very first touch, not only after some later `Show()`.
- `SearchResultFactory` (Base project, `Editor/Search/SearchResultFactory.cs`) - the stateless
  `CreateSearchResult`/`CreateInlineBuilder`/`DummySearchResult` static helpers, extracted
  unchanged since they never depended on the pad instance, just on AddInTree-registered
  `ISearchResultFactory` extensions.

**A real routing bug, same shape as the layout-exclusion issue from earlier slices**: initial
`BringToFront()` was `=> Show()`, which does nothing when this default-hidden pad has been excluded
from `DockWorkspace.ToolPanes` by the current layout file (see `Outline`'s original bug) - `Show()`
only flips `IsVisible`/`IsActive` on a model with no live anchorable in that state. Fixed to
`SD.Workbench.GetPad(typeof(SearchResultsPad))?.BringPadToFront()`, going through the same
`PadDescriptor` routing `od.show-pad` already uses, which correctly falls back to the legacy
`AvalonPadContent` path. Verified live: `od.layout.pane-position` for the pad's `ContentId` went
from `found:false` to `found:true` after the fix, with real result text rendered in the UI tree.

**A false alarm, not a regression**: `SearchAndReplaceTests.ShowResults_PopulatesSearchResultsPadUiTree`
initially failed (only 1 of an expected 2+ `SearchResultNode`s found in the automation tree after
searching "Widget" across the fixture solution, which has 5 raw matches in 2 files). Traced through
`DefaultSearchResultFactory`/`SearchRootNode` (`SearchAndReplace/Project/Gui/SearchRootNode.cs`):
node construction is a strict 1:1 map over matches with no dedup by line/column, and the default
`Flat` grouping mode attaches `resultNodes` directly as `SearchRootNode`'s children (no
intermediate per-file wrapper) - so the data model always holds the correct count. The actual cause
is `ResultsTreeView.xaml`'s `VirtualizingStackPanel.IsVirtualizing="True"`: a UI-automation scan
taken immediately after `ShowSearchResults`, without scrolling every row into view, only sees
whichever `TreeViewItem` containers happened to already be realized - typically just the first.
This is pre-existing WPF virtualization behavior, unrelated to this migration, and was never caught
before because no earlier pad migration this session drove test assertions through automation-tree
node *counts*. Confirmed harmless by direct visual check of a live-launched instance: the Search
Results pad renders every match correctly once actually looked at (scrolled/rendered), matching
what the data model always said. Not fixed (there is nothing in this migration's scope to fix);
flagging the test's virtualization-blind assertion style as a known limitation for whoever next
touches search UI tests.

**Status**: 5 of 11 legacy pads migrated (`Outline`, `DefinitionView`, `TaskListPad`,
`PropertyPad`, `SearchResultsPad`). `BookmarkPad`+`BreakPointsPad` remain blocked on their shared
base class. `ErrorListPad`, `ClassBrowserPad`, `OutputPad`, `SideBar`, `FileScout` not yet examined
for external-caller complexity.

## Legacy Pad migration, sixth slice: ErrorListPad, and a real first-show layout race (2026-08-03)

`ErrorListPad` had the same shape as `TaskListPad` (no shared base class, no cross-AddIn MEF
reachability problem now that `ToolPaneModel` lives in Base) but the widest spread of external
`typeof(ErrorListPad)`/`GetPad(typeof(ErrorListPad))` callers of any pad so far - production code in
7 different AddIns/assemblies (`BuildCommands.cs`, `AspNet.Mvc`, `WixBinding` (x2), `XmlEditor`,
`WpfDesign.AddIn`, `Profiler.AddIn`, `UnitTesting`'s `TestExecutionManager.cs`), none of which
reference the App project. All fixed with the same `IWorkbench.GetPad(string className)` overload
already added for `PropertyPad`'s slice - `XmlEditor` additionally needed
`ErrorListPad.ShowAfterBuild` (a plain static forwarding property, never pad-instance-dependent)
replaced with the direct `ICSharpCode.SharpDevelop.Project.BuildOptions.ShowErrorListAfterBuild` it
always forwarded to, since `XmlEditor` has no reason to reference the App-project type at all for a
property that never touched pad state. Also deleted a dead `[Obsolete]` `SDTask.
DefaultContextMenuAddInTreeEntry` constant in Base's `Task.cs` that referenced
`Gui.ErrorListPad.DefaultContextMenuAddInTreeEntry` directly - unused anywhere, and would have
required its own indirection otherwise.

`ErrorListToolbarCommands.cs`'s three toggle buttons moved into the App project alongside
`ErrorListViewModel` (same treatment `TaskListPadCommands.cs` got) - they resolve the `[Shared]`
view model straight via `OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>()`
rather than through a legacy `ErrorListPad.Instance` singleton, since these commands only ever run
from inside the AddIn tree the App project already owns (no cross-assembly boundary to cross, unlike
`IPropertyPadHost`/`ISearchResultsHost`).

**A newly-caught, real bug** (not a false alarm this time): `ErrorListTests.
ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors` failed consistently (not flaky - reran
clean, failed every time) on its Description-column assertion, even though `od.error-list` (raw
`TaskService` data) showed all 6 expected tasks correctly. Root cause, confirmed by a manual
step-by-step repro: the legacy `ErrorListPad` used to be constructed **eagerly at workbench
startup** - `AvalonDockLayout`'s startup loop explicitly does `if (!IsMefToolPane(pd)) ShowPad(pd);`
for every registered `PadDescriptor`, i.e. it deliberately *skips* pads that already have a migrated
`ToolPaneModel` (this is what lets AvalonDock realize the modern pane through the ordinary
`AnchorablesSource` binding instead of double-showing it). Before this slice, `ErrorListPad` was
*not yet* a MEF tool pane, so it got that automatic early `ShowPad`, meaning its `ListView`/`GridView`
visual tree already existed, laid out, by the time any build ever failed - a later build failure was
just an `ItemsSource` update on an already-realized control. After migrating it, the pad's entire
control tree is now built for the first time whenever it's first actually shown - which, in this
test's sequence (build fails, *then* `od.show-pad` is called), is the same tick a caller might
immediately inspect the rendered UI automation tree, racing ahead of WPF's layout pass for a
freshly-constructed `GridView`. Manually reproducing the same steps with an extra beat of latency
between `show-pad` and reading the tree always rendered correctly, confirming this was a genuine
timing gap introduced by no longer eagerly constructing default-visible migrated pads, not a data or
grouping bug (unlike the SearchResultsPad virtualization false alarm, which turned out to need no
fix at all).

Fixed at the shared `od.show-pad` DevFlow action level (`OpenDevelopDevFlowActions.cs`), not per-pad:
after `SD.Workbench.ActivatePad(pad)`, flush the dispatcher up to `DispatcherPriority.Loaded` via a
throwaway `Application.Current.Dispatcher.Invoke(() => {}, DispatcherPriority.Loaded)` before
returning. This matches what the action's own description already promised ("so AvalonDock actually
creates and renders its content") and benefits every migrated pad's first-show, not only
`ErrorListPad` - the race is inherent to lazy MEF pad construction in general, this was just the
first pad+test combination to actually expose it (a build-then-immediately-inspect sequence, which
none of the earlier five pads' tests happened to do).

**Verified**: Base/App/`WpfDesign.AddIn`/`XmlEditor`/`UnitTesting` all build clean. `WixBinding`/
`AspNet.Mvc`/`Profiler.AddIn` could not be build-verified (pre-existing net45 reference-assembly gap,
same as previous slices, confirmed unrelated). Live: fresh launch, zero exceptions, Errors pad shows/
docks at `Bottom` correctly, a real 6-error build populates and renders every row (File + Description
columns both confirmed via the UI automation tree). `ErrorListTests` 3/3 clean after the
`od.show-pad` fix (was 2/3 before, failing consistently, not flaky).

**Status**: 6 of 11 legacy pads migrated (`Outline`, `DefinitionView`, `TaskListPad`, `PropertyPad`,
`SearchResultsPad`, `ErrorListPad`). `BookmarkPad`+`BreakPointsPad` remain blocked on their shared
base class. `ClassBrowserPad`, `OutputPad`, `SideBar`, `FileScout` not yet examined for
external-caller complexity - worth checking each for the same "eager-startup vs first-show timing"
risk this slice found, in addition to the usual reachability checks.

## Legacy Pad migration, seventh slice: SideBar (ToolsPad), and FileScout ruled out (2026-08-04)

**`FileScout` looked at first and ruled out, not migrated.** It's a pure `System.Windows.Forms`
`UserControl` (`ListView`/`TreeView`/`Splitter`/`ShellTree`) hosted via `WindowsFormsHost`, but
`WorkbenchStartup.InitializeWorkbench` has `WindowsFormsHost.EnableWindowsFormsInterop()` commented
out with `"removed - no WinForms interop in this MVP build"`. So `FileScout` is very likely
non-functional today (can't render), not merely un-migrated - wrapping it in a `ToolPaneModel` shim
would just be polishing dead code. Migrating it for real means a native WPF rewrite of the file
browser, not the mechanical "shim + ViewModel" shape every other pad in this list got. Left for a
separate, deliberate decision (rewrite vs. delete the AddInTree `<Pad id="FileScout">` entry
entirely) rather than attempted here.

**`SideBar` migrated** (AddInTree pad id `"SideBar"`, class `ICSharpCode.SharpDevelop.Gui.ToolsPad`,
the id and class name diverge, worth remembering when grepping for it). Turned out to be the
easiest slice yet: already pure WPF (a single `ContentPresenter`), no shared base class, and only
one production external caller of `typeof(ToolsPad)`
(`WpfDesign.AddIn/Src/Commands/Pads.cs`'s `Tools` menu command), fixed the same way `PropertyPad`'s
callers were, with `SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.ToolsPad")` instead of
`typeof(ToolsPad)`, since that AddIn only references the Base project. `IToolsHost` (the interface
several AddIns, WpfDesign, FormsDesigner, AvalonEdit.AddIn, Reporting, WorkflowDesigner,
Data.EDMDesigner, implement to feed this pad) stays in the Base project on its own, since the new
shim lives in the App project which those AddIns don't reference.

`ToolsPadViewModel` (App project, MEF-exported `ToolPaneModel`) reproduces the original behavior
exactly: subscribes to `SD.Workbench.ActiveViewContentChanged` (deferred to first real use, same
early-startup hazard guarded against in every previous slice) and sets its `ContentPresenter.Content`
from `SD.GetActiveViewContentService<IToolsHost>().ToolsContent`, falling back to the
"no tools available" string. `ToolsPad` itself is now a two-line shim resolving the ViewModel from
MEF, same shape as `ErrorListPad`.

**Verified**: `SharpDevelop.csproj`, `WpfDesign.AddIn`, and the full `OpenDevelop.Mvp.slnx` all
build clean (0 errors). Live: fresh launch, zero exceptions attributable to this change (only
pre-existing unrelated errors - `BrowserDisplayBinding` class-not-found, stale recent-file paths),
`od.show-pad "ToolsPad"` finds it and reports `success:true`,
`className:"ICSharpCode.SharpDevelop.Gui.ToolsPad"` - confirming `LegacyPadClass` routing works for
this pad same as the previous six.

**Status**: 7 of 11 migrated. `FileScout` ruled out (see above, needs its own rewrite-or-delete
decision, not a migration). Remaining 3 to examine: `BookmarkPad`+`BreakPointsPad` (known blocked,
shared base class across two assemblies), `ClassBrowserPad`, `OutputPad`.

## Legacy Pad migration, eighth slice: OutputPad, and `ClassBrowserPad` ruled out (2026-08-04)

**`ClassBrowserPad` looked at and ruled out, not migrated.** Its AddInTree `<Pad id="ClassBrowser">`
entry, and the `IClassBrowser`/`ClassBrowserServiceImpl` service it depends on, are both already
wrapped in `<!-- MVP: removed ... -->` in `ICSharpCode.SharpDevelop.addin` - unlike `OutputPad`/
`Bookmarks`, which are still live registrations. So this pad isn't reachable in the running app at
all today; it doesn't need MVVM migration, it's simply out of this MVP build's scope already
(same category the user asked to skip explicitly for this pass).

**`OutputPad` migrated** (AddInTree pad id `"OutputPad"`, class `ICSharpCode.SharpDevelop.Gui.
CompilerMessageView`) - the biggest slice yet, both in size (~570 lines) and in the number of
external touch points, because it combines every hazard the previous seven slices found
individually:

- **Static `Instance` singleton with 12+ external call sites** across Base, three AddIns
  (PackageManagement x2, and Base's own `ServiceReference`/`TypeResolutionService`/
  `CompilerMessageViewToolbarCommands`), same shape as `PropertyPad`/`SearchResultsPad`'s
  blocker. Two fixes, matched to what each caller actually needed:
  - Callers that only needed `BringToFront()` (`RestorePackagesCommand.cs`,
    `AddServiceReferenceViewModel.cs`, and seven separate `GetPad(typeof(CompilerMessageView)).
    BringPadToFront()` call sites across `XmlView.cs`, `WixBindingService.cs`,
    `TypeResolutionService.cs`, `ProfilerRunner.cs`, and the NAnt sample) now call the
    already-registered `SD.OutputPad.BringToFront()` (`Workbench.IOutputPad`, pre-existing,
    unrelated to this migration) directly - no new interface needed for that one method.
  - Callers needing `MessageViewCategory`-typed access or the toolbar-facing surface
    (`GetCategory`, `AddCategory`, `SelectedCategoryIndex`, `MessageCategories`, `WordWrap`, the two
    change events, `Content`) got a new `IOutputPadHost` in the Base project, same shape as
    `IPropertyPadHost`/`ISearchResultsHost`, registered via
    `SD.Services.AddService(typeof(IOutputPadHost), this)` in the ViewModel's constructor.
    `MessageViewCategory.Create`, `PackageManagementCompilerMessageView.cs`, and
    `CompilerMessageViewToolbarCommands.cs` (moved into the App project, same treatment as
    `ErrorListToolbarCommands.cs`/`TaskListPadCommands.cs`) all resolve through it now.
- **A genuine "must stay eager" constraint, not just an early-startup timing hazard.** Every other
  migrated pad's real work got deferred to a lazy `EnsureSubscribed()` on first touch. `OutputPad`
  can't use that pattern: `Workbench.IOutputPad` is explicitly documented thread-safe and routinely
  driven by background build/restore/coverage threads, and `WorkbenchStartup.cs` had a pre-existing
  `// HACK: eagerly load output pad because pad services cannot be instantiated from background
  threads` comment confirming this was already a known constraint before this migration. So
  `CompilerMessageViewViewModel` builds its whole control tree and subscribes to
  `SD.ProjectService.CurrentSolutionChanged` directly in the constructor, exactly like the original
  class did - the only thing that changed is *what* constructs it (MEF's `[Shared]` "ToolPane"
  export instead of the AddInTree), not *when* relative to workbench startup.
- **A real, live-reproduced crash from a wrong assumption about MEF `[Shared]` scoping across two
  export contracts of the same part.** `CompilerMessageViewViewModel` has two `[Export]` attributes
  (`typeof(CompilerMessageViewViewModel)` and `"ToolPane"`/`typeof(ToolPaneModel)`, same pattern
  every migrated pad uses) - assumed, wrongly, that `[Shared]` meant one singleton instance served
  both contracts. It does not, under this codebase's TomsToolbox-over-Microsoft.Extensions.
  DependencyInjection bridge: resolving via the plain-type contract after `DockWorkspace.ToolPanes`
  had already constructed the instance via the "ToolPane" contract built a **second, distinct**
  instance, whose constructor then crashed on its own `SD.Services.AddService(typeof(IOutputPad),
  this)` call with `ArgumentException: An item with the same key has already been added`. Hit twice,
  live: once from an explicit `GetExportedValue<CompilerMessageViewViewModel>()` this slice
  initially added to `WorkbenchStartup.cs` (to replace the eager-load hack above - removed again,
  unnecessary, since `workbench.WorkbenchLayout = layout` immediately above it already touches
  `DockWorkspace.ToolPanes` and constructs the real instance right there), and once from
  `CompilerMessageViewToolbarCommands.cs`'s `ShowOutputFromComboBox`, which is constructed *inside*
  the ViewModel's own constructor (via `ToolBarService.CreateToolBar`) and so re-enters MEF
  resolution mid-construction. Fixed by making every external touch point (toolbar commands, the
  two `OpenDevelopDevFlowActions.cs` DevFlow actions, `MessageViewCategory`, the PackageManagement
  AddIn) resolve via the already-registered `IOutputPadHost`/`IOutputPad` **services**
  (`SD.Services.GetService`), never via a second `GetExportedValue<CompilerMessageViewViewModel>()`
  call. The shim (`CompilerMessageView.cs`, App project) keeps its own `GetExportedValue` call same
  as `ErrorListPad`/`ToolsPad`/`PropertyPad`'s shims - dormant in practice, like theirs, since
  `CreatePad()` is only reachable through direct `PadDescriptor.PadContent`/`BringPadToFront()`
  access and every remaining caller of those was fixed to go through `SD.OutputPad`/`IOutputPadHost`
  instead. Worth flagging for whoever migrates the next pad with a constructor-time
  `SD.Services.AddService` call (only `PropertyPad`/`OutputPad` do this so far): audit every
  resolution of that ViewModel type, not just the shim, for this exact hazard.

**Verified**: full `OpenDevelop.Mvp.slnx` builds clean (0 errors). Live: fresh launch after the
fix, zero exceptions, `od.show-pad "Output"` finds it (`className:
"ICSharpCode.SharpDevelop.Gui.CompilerMessageView"`), a real build's "Build finished successfully."
text renders in the pad's UI automation tree. `WorkbenchTests` 33/33 clean (includes
`BuildSolution_OutputPadCapturesRealBuildLog` and all three `ErrorList_*` tests, which build and
read output/error-list state together).

**Status**: 8 of 11 migrated. `ClassBrowserPad` ruled out (already excluded from the MVP AddInTree,
not reachable). `FileScout` ruled out separately (needs a rewrite-or-delete decision). Only
`BookmarkPad`+`BreakPointsPad` remain - known blocked on their shared base class across two
assemblies (Base's `BookmarkPadBase` is also `Debugger.AddIn`'s `BreakPointsPad`'s base), not yet
attempted.

## Legacy Pad migration, eighth slice: BookmarkPad + BreakPointsPad, the last blocked pair (2026-08-04)

**Root cause, confirmed by reading the code (not just repeating the earlier "blocked" note):**
`OpenDevelopMefHost.BindExports` only scans `Assembly.GetExecutingAssembly()` - the App project's
own assembly. `Debugger.AddIn` isn't scanned, and correctly doesn't reference the App project
(only Base/Core/Core.Presentation) - so `BreakPointsPad`'s real implementation can never become a
MEF `[Export("ToolPane", ...)]` part the way the other 9 migrated pads did. That's the actual
blocker, not merely "two pads share a base class" - the shared base class is what made the
blocker *visible* (migrating `BookmarkPad` alone, the way `Outline`/`DefinitionView` were migrated
one at a time, would silently break `BreakPointsPad`'s compile), not the blocker itself.

**The unblock**: `IPaneModelHost` (Base project, previously only had `Remove(PaneModel)`) got a new
`Add(ToolPaneModel model)` method, implemented by `DockWorkspace` as a one-line forward to its
existing internal `AddToolPane` (itself already used by ILSpyAddIn's runtime-constructed panes,
but only reachable there because ILSpyAddIn is a special case that directly references the App
project - the one thing every other AddIn in this migration correctly doesn't do). This gives any
AddIn a way to register a runtime-constructed `ToolPaneModel` with the one real docking host,
through the same service-indirection pattern as `IPropertyPadHost`/`IOutputPadHost` - no compile-time
reference to the App project needed, and no change to `OpenDevelopMefHost`'s scanning.

**Shape of the fix**:
- `BookmarkPadBase : AbstractPadContent` (Base project) replaced by
  `BookmarkPadViewModelBase : ToolPaneModel` (`Editor/Bookmarks/BookmarkPadViewModelBase.cs`) -
  same members (`ListView`/`Items`/`SelectedItem`/`SelectedItems`, `BookmarkManager` subscription
  deferred to `Show()` since both pads default hidden - `defaultPosition = "Bottom, Hidden"` for
  both, so the simple `Outline`/`DefinitionView`-style deferred pattern applies, none of
  `OutputPad`'s "must stay eager" complication), plus a new `protected abstract void
  CreateToolBarContent()` hook (each subclass's toolbar/column setup differs) called at the end of
  `EnsureSubscribed()` - same reasoning as `TaskListViewModel`'s deferred toolbar construction.
  `SDBookmark.ShowInPad`/`CurrentLineBookmark.ShowInPad`'s parameter type updated to match.
- `BookmarkPadViewModel : BookmarkPadViewModelBase` (App project, `[Export]`+`[Shared]`, same shape
  as the other 9) - `BookmarkPad` is a thin shim, same file-location reasoning as
  `CompilerMessageView`'s (needs `OpenDevelopMefHost.ExportProvider`, internal to the App
  assembly) but keeps its **original namespace** (`ICSharpCode.SharpDevelop.Editor.Bookmarks`, not
  `Gui`) so `PadDescriptor.Class`/`LegacyPadClass` keep resolving to the same fully-qualified name
  regardless of which project the file physically lives in.
- `BreakPointsPadViewModel : BookmarkPadViewModelBase` (Debugger.AddIn, **not** a MEF part -
  constructed with a plain `new`) lives entirely in that AddIn's own assembly, referencing only the
  Base project as before. `BreakPointsPad` (the AddInTree shim, unchanged namespace/location)
  constructs it once (cached in a static field - this shim plays the role `[Shared]` MEF
  composition plays for the App-project-hosted pads) and registers it via
  `(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel)` on first
  touch.
- `BookmarkPadToolbarCommands.cs`'s 5 commands (shared by both pads' toolbars via `this.Owner`)
  needed only their cast target updated, `BookmarkPadBase` → `BookmarkPadViewModelBase` - no
  duplication, they stay in Base, reachable from both assemblies exactly as before.

**Same `CreatePad()`-must-stay-real lesson as `OutputPad`'s slice, caught before it shipped this
time**: an early draft made both `CompilerMessageView` and `BookmarkPad`'s shims bare marker classes
(no `IPadContent`), reasoning that `LegacyPadClass` routing means the real pane is never
constructed through the legacy path. Wrong - `PadDescriptor.BringPadToFront()` unconditionally
calls `CreatePad()` *first*, regardless of whether a MEF `ToolPaneModel` already exists for that
class (the `IsMefToolPane` skip only applies to `AvalonDockLayout`'s own startup-time `ShowPad`
loop, a different code path) - and several external callers this slice didn't touch
(`GetPad(typeof(CompilerMessageView)).BringPadToFront()` in samples/Profiler.AddIn/XmlEditor/
WixBinding/`TypeResolutionService`) still reach it. A bare marker class would have made
`CreatePad()`'s `(IPadContent)Activator.CreateInstance(...)` cast throw, caught internally and
shown as an error dialog - not a crash, but a real regression. Both shims stay real, constructible
`AbstractPadContent`s, same as every other migrated pad's.

**Verified**: `SharpDevelop.csproj`, `Debugger.AddIn.csproj`, and the full `OpenDevelop.Mvp.slnx`
all build clean (0 errors). Live: fresh launch, zero exceptions attributable to this change,
`od.show-pad "Bookmarks"` and `od.show-pad "BreakPointsPad"` both find their panes
(`className`s: `ICSharpCode.SharpDevelop.Editor.Bookmarks.BookmarkPad` and
`ICSharpCode.SharpDevelop.Gui.Pads.BreakPointsPad`), `od.debug.pad-snapshot "BreakPointsPad"` still
works (empty breakpoint list, no error) confirming the DevFlow reflection-based `GetSnapshotAsync`
lookup survived the shim rewrite, and `OutputPad`/`ToolsPad` (previous slices) still resolve
correctly too (regression check).

**Status: 11 of 11 legacy pads in the original "Docking and layout replacement" item 4 list are
now either migrated (10: `Outline`, `DefinitionView`, `TaskListPad`, `PropertyPad`,
`SearchResultsPad`, `ErrorListPad`, `SideBar`/`ToolsPad`, `OutputPad`, `BookmarkPad`,
`BreakPointsPad`) or deliberately ruled out with a documented reason** (`ClassBrowserPad`: already
excluded from the MVP AddInTree; `FileScout`: WinForms interop is disabled in this MVP build,
needs a rewrite-or-delete decision, not a mechanical migration). No further pads in this list
remain unexamined.

## Legacy pad migration, fourth slice: UnitTestsPad and the AddIn-side routing seam (2026-08-09)

The item-4 list was shell-internal pads only. The Unit Tests pad (`UnitTesting.addin`'s
`<Pad id="UnitTestingPad" class="ICSharpCode.UnitTesting.UnitTestsPad">`) is an **AddIn** pad and
is the last unexamined legacy one in the running product; it was also the subject of a user bug
report: starting a test run in the debugger made the pad vanish, and it never came back even after
debugging stopped.

**Root cause 1 - the vanish**: legacy pads aren't part of persisted layouts
(`LayoutSerializationCallback` cancels any anchorable whose ContentId isn't a registered
`ToolPaneModel`), and the user's saved layout (`layouts/Default.xml`) still carried the legacy
`ContentId="ICSharpCode.UnitTesting.UnitTestsPad"`. Combined with `LoadLayout`'s reconciliation
(panes not named in the restored layout file are evicted from `ToolPanes`), a legacy pad was
detached on every layout restore. The debugger makes this acute: `BaseDebuggerService`'s
`OnDebugStarting`/`OnDebugStopped` switch to the `Debug` layout (which lists only
`ProjectBrowser`+`OutputPad`), so **starting a debug session immediately evicted the pad** from the
workbench.

**Root cause 2 - it never came back**: `WindowsDebugger.Stop()` called `DapSession.Stop()`, which
tears down via `CleanupSession()` - and `CleanupSession` never raises `Exited`. The only `Exited`
triggers are the DAP `terminated`/`exited` events and `AdapterProcessExited`, so an **explicit
user stop** skipped `SessionExited` entirely, which is the only path that calls
`BaseDebuggerService.OnDebugStopped` (the thing that switches back to `"Default"`). Result: the
workbench stayed on the `Debug` layout forever after any explicit stop. This is a pre-existing
bug unrelated to pad migration (normal session *termination* still fired `Exited`); fixed in
`WindowsDebugger.Stop()` by reusing `SessionExited()` after `CurrentSession.Stop()` so the explicit
stop path gets the same main-thread cleanup (layout switch back to `Default`, line-marker
removal, pad refresh, session null-out).

**The migration**: `UnitTestsPad` is AddIn-side, so the App's MEF catalog
(`OpenDevelopMefHost.BindExports(Assembly.GetExecutingAssembly())`) can never see a
`[Export("ToolPane", ...)]` from the UnitTesting assembly. Added a Base-side seam instead:
`PadToolPaneProvider` (`src/Main/Base/Project/ViewModels/PadToolPaneProvider.cs`) - a small
`Register(legacyPadClass, Func<ToolPaneModel>)` / `Resolve(legacyPadClass)` registry whose factory
is invoked lazily on first resolution. `AvalonDockLayout.GetMefToolPaneContentId` now falls back to
it on a miss and registers the resolved pane via `DockWorkspaceExtensibility.AddToolPane` - the
first `ShowPad` runs inside `Attach`, before `InitializeLayout`/`BindSources`, so the pane is in
`ToolPanes` when the `AnchorablesSource` binding attaches, exactly like a built-in pane. The
registration itself comes from a new `/SharpDevelop/Autostart` command
(`RegisterUnitTestsPadToolPaneCommand`), which runs before the workbench is up - hence the lazy
factory.

`UnitTestsPadToolPaneModel` (`Pad/UnitTestsPadToolPaneModel.cs`) is the modern model:
`ContentId="UnitTestingPad"`, `LegacyPadClass = typeof(UnitTestsPad).FullName`,
`PreferredDockSide=Left`, `PreferredDockSize=250` (matching the legacy `defaultPosition="Left"`
and `EnsureDefaultPositionSize`), `Content` = the shared `UnitTestsPad` instance. The pad keeps a
static `SharedInstance` (first constructed instance wins) because `PadDescriptor.BringPadToFront()`
unconditionally `CreatePad()`s - the AddInTree route can still mint a second, never-shown instance;
`TestExecutionManager.ShowUnitTestsPad` now uses `SharedInstance` instead.

**Layout data migration**: the saved `layouts/Default.xml` carried the legacy ContentId - rewritten
to `UnitTestingPad` (the compatibility layer that would have matched
`LegacyPadClass == anchorable.ContentId` in `LayoutSerializationCallback` was deliberately
**not** added; the data, not the code, was migrated). The `data/layouts/Default.xml` template got
the pad too, preserving the "always visible by default" semantics the legacy
`defaultPosition="Left"` gave it (a pane not named in the restored layout is evicted, so without
the template entry new users would never see the pad).

**Verified end-to-end**: fresh launch shows `UnitTestingPad` in `od.layout.tool-panes` (4 panes,
visible) and `UnitTestsPadView` in the UI tree; opening the Obfuscar solution, setting a
breakpoint, and `od.unit-test.debug-one` reproduces the vanish *during* debugging (Debug layout,
2 panes - the designed debug-layout behavior); `od.debug.stop` now switches back (`Saving
Debug.xml → Loading Default.xml` in the log) and restores `ErrorList`+`UnitTestingPad`, visible in
the UI tree again. The remaining AddIn legacy pads (the Debugger.AddIn pads, XPathQueryPad,
PackageManagementConsolePad, etc.) can use the same `PadToolPaneProvider` seam.

**Follow-up (same day): layout switches are incremental, not evicting**. A user review of the
verification above rejected the "Debug layout shows exactly 2 panes" behavior: switching layouts
must *open and surface the panes the target layout names* but must **not close pads the user had
open** (the debugger's `Debug`-layout switch was *closing* ErrorList/UnitTestsPad/TaskList/...).
`LoadLayout` therefore no longer removes non-layout panes from `ToolPanes` (the `layoutExcludedPanes`
bookkeeping and `ReadAnchorableContentIds` were deleted with it); instead the `AnchorablesSource`
import after `RestoreLayout` re-docks them via `DockWorkspace.BeforeInsertAnchorable` to their
`ToolPaneModel.PreferredDockSide` (now declared on every migrated pane, matching the legacy
`defaultPosition`; panes with `IsVisible=false` are sent to the Hidden area instead of selected).
Verified live: during a paused debug session `od.layout.tool-panes` still lists ErrorList,
PropertyPad, TaskList, ToolsPad, UnitTestingPad as visible, and the Debug→Default switch on stop
keeps them all. The user's two other reports were fixed in the same pass: the Unit Tests pad
status bar read `Total: 0` while its tree was populated (it only ever counted *runs*;
`LoadOpenSolution` now counts the loaded test tree's leaves, so `Total: 524` shows the discovered
set, and a run's `StartRunStatus`/`TestCountDiscovered` still override it), and opening a
solution now brings the Projects pad to front (`WpfWorkbench` subscribes
`SD.ProjectService.SolutionOpened` → `GetPad(typeof(ProjectBrowserPad)).BringPadToFront()`, which
routes to the migrated `ProjectBrowserViewModel` via its `LegacyPadClass`).

A later, opposite-direction fix to the same `CountLeafTests` (2026-08-25): a fresh, empty
OpenDevelop (no solution, no project) showed `Total: 1` on the Unit Tests pad. Root cause: the
`OpenSolution` getter lazily creates an empty `TestSolution` ("All Tests" root), and
`CountLeafTests` returned `1` for any node with no loaded children — counting that empty root as a
test. `CountLeafTests` now returns `0` for a container node (`TestSolution`/`TestNamespace`/
`TestProjectBase`/`MtpTestClass`) with no children, and only `1` for a real test-method leaf.
Verified live via DevFlow (`od.unit-test.pad-tree`/`od.solution.status` confirm no solution/project,
and the UI status bar reads `Total: 0`).

## Legacy Pad migration, fifth slice: the Debugger.AddIn pads (2026-08-09)

The seven Debugger.AddIn pads were the last unexamined legacy cluster: `BreakPointsPad` had been
migrated on 2026-08-04 (its `BookmarkPadViewModelBase` subclass + shim), the other six
(`CallStackPad`, `ThreadsPad`, `LoadedModulesPad`, `LocalVarPad`, `WatchPad`, `ConsolePad`) were
still plain `AbstractPadContent` classes - WPF already (ListView/SharpTreeView/console), so no
control port was needed, only the same shim+model split the 08-04 slice established.

**The pattern applied per pad** (mirroring `BreakPointsPad`): the legacy class stays as a thin
shim - static field holding one `XxxPadViewModel`, ctor constructs it with a plain `new` (the
AddIn's assembly is never scanned by `OpenDevelopMefHost`) and registers it via
`IPaneModelHost.Add` (the Base-side seam added for the 08-04 slice), `Control` delegates to
`viewModel.Content`. The shim keeps the members external code still casts to (`WatchPad`'s
`AddWatch`/`Tree`/`Items` - `WatchRootNode.Drop` and `AddWatchExpressionCommand` still route
through `GetPad(typeof(WatchPad)).PadContent as WatchPad`; the `GetSnapshotAsync` methods the
DevFlow `od.debug.pad-snapshot` action reflects on). Each `XxxPadViewModel : ToolPaneModel` sets
`Title`/`ContentId`/`IsVisible=false`/`IsCloseable=true`/`LegacyPadClass`/`PreferredDockSide=Bottom`
(matching the addin file's `defaultPosition = "Bottom, Hidden"`) and owns the control + the
`WindowsDebugger.RefreshingPads` subscription, so a paused session populates all four list/tree
pads.

**WatchPad's toolbar commands** (`AddWatchCommand`/`RemoveWatchCommand`/`ClearWatchesCommand`)
received the model as their `Owner` once the toolbar was built with `this` = the model (the same
shift every migrated toolbar went through), so they now cast to `WatchPadViewModel` instead of
`WatchPad`. The `Debugger.AddIn.addin` registrations are untouched - the shim classes keep their
exact class names, so `PadDescriptor.BringPadToFront()/CreatePad()` routing is unchanged.

**ConsolePad needed a shared-console extraction, not just a model split.** `ConsolePad` was a
subclass of Base's `AbstractConsolePad` (itself still the base of the unmigrated
`FSharpInteractive`), and the common-console toolbar commands (`ClearConsoleCommand`,
`DeleteHistoryCommand`, `ToggleConsoleWordWrapCommand`) cast `Owner` to `AbstractConsolePad` - a
model can't be one. Extracted the console body (panel + `ConsoleControl` + toolbar + prompt/
history/readonly-region handling + `IEditable`/`IPositionable`/`IToolsHost`) into a new
`ConsolePadCore` (Base, `Gui/Pads/ConsolePadCore.cs`), parameterized by delegates for the
per-console pieces (prompt, command acceptance, text-entered hook, toolbar construction);
`AbstractConsolePad` now delegates to a core built from its subclass overrides (its public/
protected surface is unchanged, so `FSharpInteractive` compiled untouched), and
`ConsolePadViewModel` hosts its own core with the debugger behaviors (DAP `EvaluateAsync` REPL,
`DebuggerDotCompletion`, the `ConsolePad`-specific toolbar path). The three common-console
commands now operate on a new Base-side `IConsolePadHost` interface (`ClearConsole`/
`DeleteHistory`/`WordWrap`) that both `AbstractConsolePad` and the model implement - the same
host-neutral seam `IPaneModelHost`/`IPropertyPadHost`/`ISearchResultsHost` established in the
earlier slices.

**Verified end-to-end** (fresh build of Base/Debugger.AddIn/FSharpBinding/SharpDevelop, live app):
`od.debug.pad-snapshot` for the four list/tree pads during a paused `DebugTestApp` session returns
real content (CallStack 1 frame, Locals 3 variables, Threads 3, Modules 4); `od.show-pad` for
`WatchPad`/`ConsolePad` creates them and their `ToolPaneModel` anchorables without exceptions; a
full debug run still switches `Default` → `Debug` → `Default` and `DebuggerIntegrationTests` 9/9
pass. The pads stay on-demand (the startup `ShowPad` loop's `AvalonPadContent` is content-lazy and
the addin's pads are `Bottom, Hidden`), exactly as before the migration - only the created pad is
now a layout-persisted `ToolPaneModel` anchorable instead of a legacy one.

## Legacy Pad migration, sixth slice: the remaining AddIn pads (2026-08-09)

With the Debugger.AddIn cluster done, the last six AddIn-side legacy pads in the running product
were migrated with the same shim+model+`IPaneModelHost.Add` shape:

- `XPathQueryPad` (XmlEditor) - model owns `XPathQueryControl` + the `ActiveViewContentChanged`
  subscription and the `XPathQueryControl.Options` memento save/load; the shim keeps `Instance`
  and forwards `Dispose` (which is when the memento is persisted) to the model.
- `CodeCoveragePad` (CodeCoverage) - model owns `CodeCoverageControl` + the
  `SolutionOpened`/`SolutionClosed` subscriptions; the shim keeps the full surface
  `CodeCoverageService`/`ShowSourceCodeCommand`/`ShowVisitCountCommand` reach it through
  (`Instance`, `UpdateToolbar`, `ShowResults`, `ClearCodeCoverageResults`,
  `ShowSourceCodePanel`, `ShowVisitCountPanel`), all delegating to the model.
- `FSharpInteractive` (FSharpBinding) - migrated onto the `ConsolePadCore` extracted in the
  previous slice (the console body is no longer reachable only via `AbstractConsolePad`); the
  model owns the fsi.exe process plumbing, `ReadAll`/`InsertBeforePrompt`, prompt "> " and the
  `;;`-terminated command acceptance; the shim keeps `fsiProcess`/`foundCompiler` internals that
  `SentToFSharpInteractive` still reads through `PadContent as FSharpInteractive`.
- `DatabasesTreeViewPad` (Data) - model owns `DatabasesTreeViewUserControl`/`DatabasesTreeView`;
  the shim keeps `Instance` + `Databases` (`DatabaseTreeViewCommands` adds through it).
- `ThumbnailViewPad` (WpfDesign) - model owns the `ContentPresenter`/`ThumbnailView` swap on
  `ActiveViewContentChanged`; note its legacy `defaultPosition = "Right, Hidden"` (unlike every
  other pad in this cluster) so the model declares `PreferredDockSide = Right`.
- `PackageManagementConsolePad` (PackageManagement) - model owns `PackageManagementConsoleView`
  + the PowerShell `ShutdownConsole` teardown loop; `PackageManagementWorkbench` still reaches it
  via `GetPad(typeof(...)).PadContent.Control`.

Each shim keeps its exact class name and AddInTree registration (no `.addin` changes), and every
pad's `ContentId`/`LegacyPadClass`/`IsVisible=false`/`PreferredDockSide` follows the established
conventions. `FileScout` remains the only un-migrated legacy pad, still deliberately excluded
(WinForms interop is disabled in this MVP build - see the seventh slice's ruling).

**Verified end-to-end**: all six create their `ToolPaneModel` anchorables via `od.show-pad` and
dock at the declared side (five `Bottom`, `ThumbnailViewPad` `Right`); `WorkbenchTests` 37/37 and
`DebuggerIntegrationTests` 9/9. One live-verification gotcha worth remembering: an addin project
whose `OutputPath` points into `AddIns/` silently keeps serving its *previously deployed* dll if
its incremental build is skipped - after touching an addin's source, check the deployed dll's
timestamp (the CodeCoverage pad initially "worked" against an 08:25 build until the project was
force-rebuilt at 18:28).

**Two real bugs the verification flushed out (fixed in the same pass)**:

1. *A hidden pad could never be shown again.* `pane.Show()` only flips the model's `IsVisible`;
   the anchorable follows it only through the `LayoutItem`'s OneWay Visibility sync, and since the
   incremental-layout change (earlier today) sent `IsVisible=false` panes to the Hidden area at
   insertion, nothing ever unhid them - `ShowToolPane`/`od.show-pad` on a default-hidden pad (e.g.
   Search Results, or any of these seven) left the anchorable in the Hidden collection forever
   (measured: `pane-position` stayed `isHidden:true` after `od.show-pad`). Two-part fix: `ShowToolPane`
   now calls `LayoutAnchorable.Show()` on a hidden anchorable, and `DockWorkspace.BeforeInsertAnchorable`
   opts out (`return false`) for hidden anchorables - `AddToLayout`'s guard throws on an anchorable
   still marked hidden, and `HideAnchorable` recorded the previous container/index anyway, so
   `Show()`'s default re-insertion restores the pad where it was. Caught by the previously-flaky
   `ShowResults_PopulatesSearchResultsPadUiTree` (which had been green before today's layout change);
   `WorkbenchTests` 37/37 and `DebuggerIntegrationTests` 9/9 after the fix.

2. *`od.show-pad` never materialized a shim-backed pad.* A pad whose model is only constructed by
   `PadDescriptor.CreatePad()` (all seven Debugger.AddIn pads) routed through the legacy
   `AvalonPadContent` path on `od.show-pad` - the content is lazy, so the shim (and with it the
   `ToolPaneModel` registration) never ran. The action now calls `pad.CreatePad()` before
   `ActivatePad`, so `TryShowMefToolPane` finds the model and the pad becomes a real anchorable.
   Also caught here: `ConsolePadViewModel`'s toolbar builder read `core.Console` from the model's
   not-yet-assigned field during the `ConsolePadCore` constructor (NRE) - the core now passes the
   `ConsoleControl` into the build delegate instead.

## Follow-on infrastructure: a shell-wide notification banner (2026-08-07)

Trigger for this section: `doc/technotes/auto-update.md` plans a visible "Check for Updates"
feature (backend already implemented, see that file) modeled on ILSpy's own
`Commands/CheckForUpdatesCommand.cs` + `ViewModels/UpdatePanelViewModel.cs` — a dismissible banner
docked above the document area with a message and a "Download"/"Check again" button. OpenDevelop
has no equivalent surface today, and auto-update is not the only feature that will eventually want
one (extension-install prompts, "solution reload needed", crash-recovery notices are the same
shape). Rather than building an update-specific banner, this section plans the generic shell
primitive once, using the conventions the pad-migration work above already established, so
auto-update becomes the *first consumer*, not a one-off.

### What already exists and is directly reusable

- `ObservableObjectBase`/`PaneModel`/`ToolPaneModel` now live in the Base project
  (`src/Main/Base/Project/ViewModels/`, see "Foundational move" above) — reachable from every
  AddIn, not just the App project. A banner view model does not need `ToolPaneModel`'s
  dock-side/size semantics (it isn't a dockable pane), but it can and should extend the same
  `ObservableObjectBase` for property-changed plumbing, for consistency with every other view model
  in the shell.
- The established idiom for "an AddIn/service needs to reach a shell-owned mechanism without a
  compile-time reference to the App project" is a small single-purpose interface registered via
  `SD.Services.AddService(typeof(IFoo), this)` and resolved via `SD.Services.GetService(...)` —
  `IPaneModelHost`, `IPropertyPadHost`, `ISearchResultsHost`, `IOutputPadHost` all follow this
  shape (see the four "Legacy Pad migration" slices above). A notification host follows the same
  pattern; it does not need a new mechanism invented for it.
- `IStatusBarService`/`SD.StatusBar.SetMessage(...)` already exists for cheap, transient,
  no-action-required text — still the right choice for the *silent* weekly startup check
  (`auto-update.md`'s plan already assumes this) and should stay separate from the banner, not be
  replaced by it.

### What's actually missing

There is no dockable-or-fixed **banner region** anywhere in the shell chrome — nothing between "a
line in the status bar" and "a modal dialog". ILSpy's `UpdatePanelViewModel` works because ILSpy's
`MainWindow.xaml` has a dedicated `ContentControl`/row above its document area that the panel binds
`Visibility` into (`Docking/DockLayoutSettings.cs` docks it there); OpenDevelop's `WpfWorkbench.xaml`
has no analogous slot. That slot, plus one generic view model, is the actual gap.

### Plan

1. **`NotificationBannerViewModel : ObservableObjectBase`** (Base project,
   `src/Main/Base/Project/ViewModels/NotificationBannerViewModel.cs`, next to `PaneModel`) — generic,
   not update-specific:
   - `IsVisible`, `Message`, `ActionText` (null hides the action button), `ActionCommand`,
     `DismissCommand` (sets `IsVisible = false`).
   - Mirrors ILSpy's `IsPanelVisible`/`Message`/`ButtonText`/`DownloadOrCheckUpdateCommand` shape,
     generalized to any (message, single action) pair rather than update-specific
     `UpdateAvailableDownloadUrl` state — that state stays in the *caller* (e.g. a future
     `UpdateCheckCoordinator`), which sets `Message`/`ActionText`/`ActionCommand` on the shared
     banner instead of the banner owning update semantics itself.
2. **`INotificationHost`** (Base project, one interface: `void Show(string message, string
   actionText, Action action); void Dismiss();`) — registered by whatever owns the single live
   `NotificationBannerViewModel` instance and control (App project, see next point), resolved via
   `SD.Services.GetService(typeof(INotificationHost))` by any caller (any AddIn, no App-project
   reference needed) — same shape as `IOutputPadHost`.
3. **Shell wiring** (App project): one `NotificationBannerViewModel` instance (constructed
   alongside `DockWorkspace`, registering itself via `SD.Services.AddService` in its constructor,
   same pattern as `PropertyPadViewModel`/`CompilerMessageViewViewModel`), one small
   `NotificationBanner.xaml` control bound to it, hosted in a new fixed row in
   `WpfWorkbench.xaml`/`AvalonDockLayout`'s chrome — above the `DockingManager`'s document area, not
   inside it (so it survives layout switches and isn't itself a pane/document), collapsed
   (`Visibility` bound to `IsVisible`) when idle so it costs nothing when unused.
4. **Auto-update becomes the first consumer**: `auto-update.md`'s manual "Check for Updates"
   command calls `SD.Services.GetService(typeof(INotificationHost))`'s `Show(...)` with the
   download-available message/action, instead of (or in addition to) the dialog fallback that
   technote sketches. The silent startup check keeps using the status bar, per that document's own
   reasoning (never surface an unprompted banner from a background check) — the banner is for the
   user-initiated path only, unless/until that policy is deliberately revisited.

### Sequencing

This is a small, one-time addition (one view model, one interface, one XAML control, one chrome
slot) — not a phase of its own in the "Phased implementation plan" above, since it doesn't touch
docking/layout/theming/composition. It can land independently of and before the Help-menu command +
options-panel work in `auto-update.md`; those two pieces are unblocked by this section landing
first, not the reverse.

**Status (2026-08-07): done, and consumed.** `NotificationBannerViewModel`/`INotificationHost`
(`src/Main/Base/Project/ViewModels/NotificationBannerViewModel.cs`), the `notificationBar` slot in
`WpfWorkbench.xaml`/`.cs`, and `InfoBarBackground`/`InfoBarBorder` theme keys in
`Theme.{Light,Dark}.xaml` are all in place exactly as planned above. Both the Base and App projects
build clean (0 errors/warnings) with these changes. `auto-update.md`'s Help-menu command and
options panel (see its own 2026-08-07 status update) are now the first real consumer, calling
`SD.Services.GetService(typeof(INotificationHost))` exactly as sketched. Not yet live-UI-verified
via DevFlow - no existing action drives a menu command or opens the Options dialog by id, so
correctness here rests on the build passing plus following the same service-registration pattern
already live-verified for `IPropertyPadHost`/`IOutputPadHost`/etc. above.
