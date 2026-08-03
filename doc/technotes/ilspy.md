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
