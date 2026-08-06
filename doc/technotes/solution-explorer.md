# Solution Explorer (WPF, CPS-backed)

**Status update (2026-08-05): legacy-addin cleanup + Cut/Copy/Paste/View-in-Browser parity pass.**
Opening a solution was logging `Cannot find class` for `ViewInBrowserConditionEvaluator`,
`CutProjectBrowserNode`, `CopyProjectBrowserNode`, `PasteProjectBrowserNode`, `DeleteProjectBrowserNode`,
plus a missing `Icons.22x22.Browser` XAML icon resource. Root cause: the legacy WinForms/ExtTreeView
ProjectBrowser's `.csproj` sources were excluded from compilation when WinForms was removed (its Pad
registration was already marked "MVP: removed" in `ICSharpCode.SharpDevelop.addin`), but the
`ToolBar`/`ContextMenu` `<Path>` entries that referenced those now-uncompiled classes were never
updated alongside it.

**First attempt was wrong and got reverted**: commenting out the entire
`/SharpDevelop/Pads/ProjectBrowser/ToolBar/*` + `ContextMenu/*` block in `ICSharpCode.SharpDevelop.addin`
looked safe (the Pad that used to own this tree is commented out, and several of its `<Path>` names -
`ContextMenu/SolutionNode`, `ContextMenu/ProjectNode`, `ContextMenu/FileNode` - are identical to path
names the new WPF pad's `ICSharpCode.SharpDevelop.ProjectBrowser.addin` also declares, so it looked like
pure duplication). It broke startup with `TreePathNotFoundException` on
`/SharpDevelop/Pads/ProjectBrowser/ContextMenu/ProjectActions`: that path, and several sibling ones
under this same tree, are `Include`d from genuinely live places that have nothing to do with the old
WinForms pad - the main window's "Project" top menu, `GitAddIn.addin`, `WixBinding.addin`, and
`PackageManagement.addin` all extend or include into these exact path names. So this "legacy" tree is
actually shared, load-bearing AddInTree infrastructure that several unrelated live features depend on,
not an orphaned duplicate - deleting/disabling it wholesale is not a safe move.

Fixed instead, surgically, leaving every `<Path>`/`<Include>` structure untouched:

- The 18 `MenuItem` occurrences across this file referencing `Project.Commands.CutProjectBrowserNode` /
  `CopyProjectBrowserNode` / `PasteProjectBrowserNode` / `DeleteProjectBrowserNode` had their `class`
  attribute redirected to the new `ICSharpCode.SharpDevelop.Commands.CutProjectBrowserItemCommand` /
  `CopyProjectBrowserItemCommand` / `PasteProjectBrowserItemCommand` / `DeleteProjectBrowserItemCommand`
  (below) - same menu ids, same tree position, now pointing at classes that actually compile and that
  bind against the new pad's `ProjectBrowserNodeContext`/`IProjectBrowserController`, instead of being
  deleted.
- The single `ConditionEvaluator name="ViewInBrowser"` registration (line 34) and the `ComplexCondition`
  block wrapping the old `MenuItem id="ViewInBrowser"` (in `ContextMenu/FileNode`, which really is the
  same path the new pad also owns) were commented out `MVP: removed`-style - both depended on
  `Project.Commands.ViewInBrowser`/`ViewInBrowserConditionEvaluator`, whose sources are excluded from
  compilation, and are now superseded by the equivalent `MenuItem` added to `ContextMenu/FileNode` in
  `ICSharpCode.SharpDevelop.ProjectBrowser.addin`.
- `Icons.22x22.Browser` had no matching resource file and no entry in
  `PresentationResourceService`'s alias dictionary, so it always fell through to a literal
  `Resources/VS2017/Browser/Browser_16x.xaml` lookup that doesn't exist. Added an alias to
  `Application` (same icon already used for the analogous `Icons.16x16.BrowserWindow`), rather than
  authoring a new icon asset for MVP.

**Follow-up (same day): `Cannot find class: ICSharpCode.SharpDevelop.BrowserDisplayBinding.BrowserDisplayBinding`
on every file open.** Same shape of bug, different subsystem: `Src\Gui\BrowserDisplayBinding\**\*.cs`
(the legacy WinForms embedded-browser view, used for "View in Browser"/`.htm` preview) is excluded
from compilation, but `ICSharpCode.SharpDevelop.addin` still registered its `DisplayBinding`,
a `BrowserLocation` `ConditionEvaluator`, and a `/SharpDevelop/ViewContent/Browser/Toolbar` path full
of its toolbar commands (`GoBack`/`GoForward`/`Stop`/`Refresh`/`GoHome`/`GoSearch`/
`UrlComboBoxBuilder`/`NewWindow`). `DisplayBinding`s are probed against *every* file the workbench
opens (to pick the right editor/viewer), which is why this error fired for a plain `.cs` file, not
just HTML - unlike the `ProjectBrowser` case, this whole area checked out as genuinely unreferenced
by any other addin (`grep -rn "ViewContent/Browser"` across the repo turns up nothing outside this
one file), so it was safe to comment out wholesale rather than needing the surgical per-`MenuItem`
treatment above. Commented out, `MVP: removed`-style: the `DisplayBinding id="Browser"` entry, the
`BrowserLocation` `ConditionEvaluator`, and the entire `ViewContent/Browser/Toolbar` path. No WPF
embedded-browser replacement exists yet - flag if "preview HTML in an editor tab" turns out to matter
for MVP; for now `.htm`/`.html` files just fall back to `AutoDetect`/`ShellExecute` like any other
file type. (Left untouched: the `BrowserSchemeExtension` `Doozer`, whose backing class is *also*
excluded from compilation and is consumed by `HelpViewer.addin`'s `<BrowserSchemeExtension>` element -
that's a pre-existing break in a different, unrelated add-in, out of scope for this pass.)

**Follow-up 2 (same day): `insertbefore`/`insertafter` codon-not-found warnings, and a course
correction.** A batch of `TopologicalSort` warnings ("Codon (X) specified in the insertbefore/
insertafter of ... does not exist") surfaced across `ResourceToolkit`, `Debugger.AddIn`,
`SearchAndReplace`, and `CodeQuality`. First pass fixed all of them by just deleting the dangling
attribute - user pushed back: don't default to deleting, actually migrate what was really lost. Redid
the investigation per-reference using `git log --all -S "<name>"` across the whole repo (not just this
port's history) before deciding what "fixing" means for each one - see [[feedback-addin-warning-cleanup]]
for the resulting standing rule. Outcome, categorized:

- **Real rename, redirected**: `insertbefore="CSharp"`/`"VBNet"` in
  `Hornung.ResourceToolkit.addin` -> `"C#-Roslyn"`/`"VB-Roslyn"` (the ids the Roslyn-backed
  `CSharpBinding`/`VBBinding` ports actually register now).
- **Reorganized, not lost - no redirect target exists**: `insertbefore="Refactoring"`
  (ResourceToolkit, `Debugger.AddIn`) - confirmed Rename/FindReferences/ExtractInterface are fully
  live today, wired under `/SharpDevelop/EntityContextMenu` (`ICSharpCode.SharpDevelop.addin:1925-1936`).
  The old single `"Refactoring"` submenu grouping id itself doesn't exist anymore (dissolved into flat
  items under a different id, `EntityContextMenu`), so there's nothing wrong with the *feature* - just
  dropped the now-meaningless ordering hint.
- **Genuinely predates this port by 10+ years - confirmed via git history, not assumed**:
  - `insertafter="FindNextSelected"` (`SearchAndReplace.addin`) - upstream SharpDevelop commit
    `3875e607ff` "remove FindNextSelected" (2011-10-26) deleted the command class and its `MenuItem`
    but missed cleaning up this attribute on the neighboring `Replace` item. Reviving it would mean
    re-implementing a feature removed 15 years ago, not restoring a port casualty.
  - `insertafter="AddExpressionBreakpoint"` (`Debugger.AddIn.addin`) - `git log --all -S
    "AddExpressionBreakpointCommand"` returns **nothing** across the entire repo history: the
    `MenuItem` (Shift+F7) and its class reference were added together in commit `cb0f290477` (2013-09-15),
    but the class was never actually written, and the `MenuItem` itself got commented out 8 days later
    in `b959bf5bdf`. Not a removed feature - a stub that never got implemented. See "New feature" below
    for what replaced it.
- **Real, but out of pass scope - a whole add-in, not a small fix**: `insertafter="CheckWithStyleCop"`
  (`CodeQuality.addin`) - `SourceAnalysis.csproj` (StyleCop integration) isn't in `OpenDevelop.Mvp.slnx`
  at all; the whole add-in is out of MVP scope, not just this one ordering hint. User chose to record
  this here and defer, not migrate it this session.

**New feature (not a warning fix): right-click a breakpoint marker to edit its condition.** The user's
ask, once `AddExpressionBreakpointCommand` turned out to be vaporware: replace the old
(never-working) Shift+F7 concept with "set a normal breakpoint, then right-click its marker to
configure a condition," backed by SharpDbg. Investigation found this was *already fully built end to
end* except for one missing wire:

- Backend: `BreakpointBookmark.Condition`/`HitCondition`
  (`src/AddIns/Debugger/Debugger.AddIn/Breakpoints/BreakpointBookmark.cs`) already flow through
  `WindowsDebugger.cs:472` -> `DapSession.SetBreakpointsAsync` -> DAP `setBreakpoints` request
  `condition`/`hitCondition` fields, gated on `Capabilities.SupportsConditionalBreakpoints`.
- UI: `BreakpointEditorPopup` (condition radio buttons, hit-count checkbox, enabled checkbox) already
  exists and is already shown - but only via `CreateTooltipContent()` on **mouse hover**
  (`IconBarMargin.MouseHover` in `AvalonEdit.AddIn/Src/IconBarMargin.cs`), not on click.
- Missing piece: `IBookmark.MouseDown`/`BookmarkBase.MouseDown` is a no-op by default, and
  `BreakpointBookmark` never overrode it. Added an override that opens the same
  `BreakpointEditorPopup` on `MouseButton.Right` (`Placement = PlacementMode.MousePoint`,
  `StaysOpen = false` so it dismisses like a normal flyout on outside click). Left-click keeps its
  existing toggle/remove behavior from the base class unchanged. Builds clean
  (`dotnet build src/AddIns/Debugger/Debugger.AddIn/Debugger.AddIn.csproj`).

New functionality (was a genuine feature gap the new WPF pad had relative to the legacy one, not just
a class-name mismatch - `ProjectBrowserAddInCommands.cs`'s existing command surface had no Cut/Copy/
Paste-node or View-in-Browser equivalents to redirect the old MenuItems to):

- `IProjectBrowserController`/`ProjectBrowserControllerBase`
  (`src/Main/SharpDevelop/Services/ProjectBrowserControllerBase.cs`) gained `CanCutOrCopy`/
  `CanPaste`/`Cut`/`Copy`/`Paste`. Clipboard state is an in-memory `(Path, IsDirectory, IsCut)` tuple
  on the controller (not the OS clipboard - the legacy version used Windows clipboard formats for
  cross-app paste, which has no cross-platform equivalent here and wasn't needed for in-tree
  cut/copy/paste). `Paste` reuses the existing `ImportExistingFiles`/`ImportExistingFolder`
  service primitives (already used by "Add Existing File/Folder") to copy into the target directory,
  then `DeleteItem`s the source when the pending op was a Cut - no new host-service surface needed.
- `ProjectBrowserAddInCommands.cs` gained `CutProjectBrowserItemCommand`/
  `CopyProjectBrowserItemCommand`/`PasteProjectBrowserItemCommand`/`ViewInBrowserProjectBrowserCommand`,
  wired into `ICSharpCode.SharpDevelop.ProjectBrowser.addin`'s `Common/Edit` group (Cut/Copy/Paste, all
  node kinds) and `ContextMenu/FileNode` (View in Browser). Unlike the legacy version's
  `ViewInBrowserConditionEvaluator` (an addin-tree `<Condition>` reading an `extensions` attribute),
  the new command's `IsEnabled` just checks the file extension directly (`.htm`/`.html`) - simpler,
  and avoids re-registering a `ConditionEvaluator` name that already had one legacy registration
  removed above.

- No new command surface needed for these classes to disappear from the log; they're the same
  `ProjectBrowserControllerBase`/`ProjectBrowserAddInCommands.cs` files already covering every other
  Project Browser action, just extended.

Not ported (left as legacy-only, no equivalent added): the old clipboard used real OS clipboard
formats so cut/copy/paste could cross process boundaries (e.g. into Explorer); the new implementation
is in-tree only. Flag this if cross-app paste turns out to matter for MVP.

**Status update (2026-07-28): one shared implementation for node model, item resolution, CPS tree
provider, the command/business-logic layer, CPS-flag kind resolution, AND git status - only native
dialog/clipboard calls stay per-host.** The plan below (rungs R6a-R6d) reads as if none of this had
started; in practice OpenDevelop already had a complete, running `ProjectBrowser*`-named
implementation (pad, WPF `TreeView`, icon/overlay services, `SharpDevelopProjectTreeProvider`) that
had diverged from UnoDevelop's `SolutionExplorer*`/`Uno*`-named one under the same "copy, then
adapt call sites, rename types" instruction this doc itself gave in R6b - i.e. a real fork, not a
mechanical rename, by the time anyone went back to check. Unified in this pass:

- **Node model** (`ProjectBrowserNodeContext`/`NodeProperties`/`NodeModel`) - canonically named
  after OpenDevelop's already-running pad rather than UnoDevelop's `SolutionExplorer*`/`Uno*`
  names, since that's the side with the fuller feature set (WPF Properties-pad integration,
  overlay/icon services) actually wired up. `GitFileStatus` (previously UnoDevelop-only) is a field
  on `ProjectBrowserNodeContext` here; the enum itself now lives in the Base layer alongside
  `GitStatusService` (see "Git status" below) since `GitAddIn` needs it too and only references
  Base. WPF-only rendering (`Icon`/overlay `ImageSource` properties, which don't exist under
  Uno.Sdk) split into `ProjectBrowserNodeModel.Wpf.cs`, compiled only into OpenDevelop.
- **Project item resolution** (`ProjectDisplayItems.GetProjectDisplayItems`/
  `GetEvaluatedDependencyItems`) - this was already factored out as host-agnostic (operates on
  `IProject`/`MSBuildBasedProject`, no OpenDevelop-specific dependency) but not linked back into
  UnoDevelop; `UnoProjectService`'s own duplicate `IProject`-based overload is deleted, with its one
  real improvement (bin/obj/.git/.vs path exclusion, for MSBuild items that legitimately have no
  `Visible="false"` metadata - e.g. Uno.Resizetizer-generated sources) ported into the shared
  version rather than lost. `UnoProjectService.GetProjectDisplayItems(string projectPath)` (a raw
  MSBuild-XML scan for projects with no live `IProject` yet) has no OpenDevelop equivalent and
  stays UnoDevelop-only.
- **CPS tree provider** (`SharpDevelopProjectTreeProvider`/`UnoDevelopProjectTreeProvider`) - the
  SDK-style-project dependency branch OpenDevelop had added (reading evaluated MSBuild items via
  `MSBuildBasedProject.GetEvaluatedProjectItems()`) is now in both, and it's a **live** branch on
  both sides, not source parity for dead code: UnoDevelop's own `IProject` implementation,
  `UnoProjectModel`, does derive from `MSBuildBasedProject`. `GetEvaluatedProjectItems()` had been
  guarded `#if !HAS_UNO` in `MSBuildBasedProject.cs` with no actual platform reason - the
  `OpenConfiguration`/`OpenCurrentConfiguration` machinery it depends on already worked under
  Uno.Sdk elsewhere in the very same class (`GetEvaluatedProperty`, which
  `MtpTestProject.ResolveAssemblyDll` already exercises successfully - see
  `unit-testing.md`) - the guard is removed, completing the capability rather than papering over
  the gap with a compile-time exclusion.
- **Command/business-logic layer** (`ProjectBrowserController`) - ~90% of this ~700-line class
  (create/rename/delete/import file or folder, include/exclude, remove reference, remove project,
  open-with, open-folder, set-startup-project, new-item/new-project orchestration around a
  template) was byte-for-byte identical between the two hosts once names were normalized, with
  exactly three genuinely native touchpoints: the new-item/new-project dialog invocation, and
  copy-to-clipboard. Split into `ProjectBrowserControllerBase` (shared, abstract - everything else)
  plus a ~30-40 line concrete subclass per host supplying just those three overrides
  (`ShowNewItemDialogAsync`/`ShowNewProjectDialogAsync`/`CopyTextToClipboard`) via host-neutral
  `NewItemDialogOutcome`/`NewProjectDialogOutcome` records. OpenDevelop's T4-template
  `CustomTool="TextTemplatingFileGenerator"` auto-set (a real feature UnoDevelop's copy lacked)
  moved into the shared base, so UnoDevelop gains it for free. `IUnoSolutionExplorerHost`/
  `IUnoSolutionExplorerController`/`IUnoSolutionExplorerService` (mechanically-identical renamed
  copies of `IProjectBrowserHost`/`IProjectBrowserController`/`IProjectBrowserService`) are gone;
  `UnoProjectService` now implements the shared `IProjectBrowserService` directly.

- **CPS-flags → `ProjectBrowserNodeKind` resolution** - `CpsTreeConverter.ResolveKind`
  (UnoDevelop) and `ProjectBrowserTreeBuilder.GetNodeKind` (OpenDevelop) both used to have their own
  copy of this mapping. UnoDevelop's was the more refined of the two (it distinguishes
  ghost/ready-to-include and missing files via CPS's own `FileSystemEntity`/
  `IncludeInProjectCandidate` flags, rather than an extra `File.Exists` disk check per node), so it
  became the canonical one: extracted into `ProjectBrowserTreeKindResolver.ResolveKind`, called by
  both converters. OpenDevelop gains ghost-file recognition it never had (a ready-to-include file
  exists on disk, so its old `File.Exists`-based check always saw it as a plain `File`, not
  `GhostFile`). An earlier revision of this note suspected the CPS-flag version was the source of
  UnoDevelop's intermittent Solution Explorer content issues seen in integration testing; that
  turned out not to hold up (the issue cleared on its own after the item-resolution/CPS-tree-provider
  fixes above, not from touching kind resolution) - noted here so the suspicion isn't silently
  forgotten if something like it resurfaces.
- **Git status** - `GitStatusService`/`GitFileStatus` were UnoDevelop-only (proper porcelain-v1 X/Y
  parsing, cross-platform `git` discovery, Untracked/Ignored/Renamed/Conflicted states), while
  OpenDevelop's `GitAddIn` had its own older, narrower engine (`GitStatusCache` - `git ls-files` +
  `status --porcelain --untracked-files=no`, only Added/Modified/Deleted/OK/None, and a WPF-typed
  `ImageSource`-returning overlay provider). Unified onto UnoDevelop's engine: `GitStatusService`/
  `GitFileStatus` moved to `Main/Base/Project/Src/Services/ProjectBrowser/` (Base layer, not the
  App-layer `SharpDevelop.csproj` they started in during the node-model merge above - `GitAddIn`
  only references Base, so a type it needs can't live in App). `GitAddIn`'s `OverlayIconManager`
  now computes its `ImageSource` badges from the shared `GitFileStatus` instead of its own
  `GitStatusCache`-backed `GitStatus` enum (deleted); `GitStatusCache.cs` is gone. One deliberate
  behavior change: the old engine also badged every clean tracked file with a green checkmark (via
  a separate `git ls-files` pass); the shared engine doesn't distinguish "clean and tracked" from
  "not in a git repo at all" (both report `GitFileStatus.None`), so that checkmark-on-every-file
  behavior was dropped rather than reintroduced just for parity - matches VS/VS Code convention
  (only non-clean files get badged), not a regression.

**Left as two separate implementations** (native UI code with no shared business logic to extract -
unifying it means building a cross-framework dialog/clipboard abstraction, a qualitatively
different and much larger effort than deduplicating logic): the three `ProjectBrowserControllerBase`
overrides themselves (WinUI `DataPackage`/`Clipboard` vs WPF `Clipboard`;
`NewItemDialog`/`NewProjectDialog` WinUI content dialogs vs `NewItemWindow`/`NewProjectWindow` WPF
windows with an owner handle) and the WPF-only `FileDialogService`
(`Microsoft.Win32.OpenFileDialog`/`OpenFolderDialog`) vs UnoDevelop's own.

## Goal

Replace the legacy WinForms/SharpTreeView-based Solution Explorer (excluded
from MVP per `docs/opendevelop.md` MVP policy 3) with a new WPF Solution
Explorer backed by a .NET Project System (CPS) shim, mirroring what
`UnoDevelop` already built for its Uno/WinUI port — per MVP policy 4, follow
UnoDevelop's direction (`ProjectTree` model + converter + node context)
instead of reviving the legacy mixed tree pipeline.

- Reference implementation: `/Users/lextm/uno-tools/UnoDevelop` (same original
  SharpDevelop codebase, already did this port for a different UI framework).
- Solution Explorer backend: .NET Project System + a clean-room CPS shim
  (see [[open-source-cps-shim]]) — MIT-licensed, not decompiled from the
  closed CPS SDK.
- Solution Explorer UI: WPF, 1:1 feature parity with the original WinForms
  version (multi-project solutions, nested folders, References/Packages/
  Dependencies grouping, linked files, missing files, Show All Files,
  Include/Exclude, rename/delete/add, startup project).
- Code namespace stays SharpDevelop-style (`ICSharpCode.SharpDevelop.Project.*`,
  `ICSharpCode.Core.IOwnerState`, addin-tree paths under
  `/SharpDevelop/Pads/ProjectBrowser/...`) for everything semantic/command-
  related, matching how UnoDevelop did it — only the CPS shim's own types keep
  the real `Microsoft.VisualStudio.ProjectSystem` namespace (needed to link
  unmodified upstream MIT source), and only the WPF view layer gets new
  OpenDevelop-specific code.

## What's reusable from UnoDevelop vs what's genuinely new

UnoDevelop's Solution Explorer has three layers. Two of them are UI-framework-
agnostic and should be **copied directly into OpenDevelop**, not hand-ported or
referenced cross-repo (matching how `ICSharpCode.TypeSystem.Abstractions` was
handled: this code isn't a published package, so copy the source wholesale
into OpenDevelop's own tree and adapt in place, rather than trying to reference
UnoDevelop or its submodules from OpenDevelop):

1. **CPS shim (copy as-is, no UI dependency at all)** —
   `UnoDevelop/src/Main/ProjectSystem/` (hand-written shim of the
   `Microsoft.VisualStudio.ProjectSystem.*` surface: `Tree/IProjectTree.cs`,
   `Tree/MutableProjectTree.cs`, `Tree/ProjectTreeFlags.cs`,
   `Tree/ProjectTreeExtensions.cs`, `IProjectTreeProvider.cs`,
   `ProjectTreeProviderBase.cs`, plus `Composition`/`Contracts`/`Imaging`/
   `Properties`/`References`/`Rules` support types) and
   `UnoDevelop/src/Main/ProjectSystemManaged/` (real MIT dotnet/project-system
   code linked from the `externals/project-system` git submodule — dependency-
   tree factories, `DependenciesSnapshot`, `MSBuildDependencyCollection`, the
   VS MEF hosting bridge in `Bridge/RealMefHost.cs`, and the hand-rolled
   per-project composition injector in `Dataflow/ManualComposition.cs`).
   None of this touches Uno/WinUI. Bring in the `externals/project-system`
   submodule the same way UnoDevelop does (or copy the specific linked files
   directly if the submodule setup is more friction than it's worth for MVP).
2. **Node data model (copy, then adapt call sites)** —
   `Services/SolutionExplorerNodeModel.cs`, `Services/SolutionExplorerNodeContext.cs`,
   `Services/SolutionExplorerNodeProperties.cs`, and the command layer
   `Services/UnoSolutionExplorerController.cs` (`IUnoSolutionExplorerController`/
   `IUnoSolutionExplorerHost`) are plain C# classes/records over SharpDevelop's
   own `IProject`/`ISolutionItem` types — no Uno/WinUI types appear in them
   except at the very edge (a `TreeViewNode` reference or two). Copy these in,
   rename the `Uno*`-prefixed types to something host-neutral (or just
   `SharpDevelop*`), and swap the few Uno-typed edges for WPF equivalents.
   `Services/UnoDevelopProjectTreeProvider.cs` (SharpDevelop `IProject` → CPS
   `MutableProjectTree` bridge) is also framework-agnostic and copies over
   directly.
3. **Tree UI (genuinely new — do not port)** —
   `Services/CpsTreeConverter.cs` (CPS tree → Uno `TreeViewNode`) and
   `Workbench/SolutionExplorerPad.cs` (WinUI `TreeView` + `UserControl`,
   `DataTemplate` built in code with converters) are Uno/WinUI-specific and
   have no WPF equivalent to copy. This is real new work: a WPF `TreeView`
   (or `HierarchicalDataTemplate`-bound `ItemsControl` if virtualization or
   custom chrome needs outgrow plain `TreeView`) hosted as an AvalonDock pad,
   with its own CPS-node → WPF-node converter mirroring `CpsTreeConverter.cs`'s
   shape but binding to WPF `TreeViewItem`/`HierarchicalDataTemplate` instead
   of WinUI's `TreeView.ItemTemplate`. Icons, context menus (still routed
   through the same SharpDevelop addin-tree `ContextMenuPath` per node kind —
   reuse that routing, just wire it to WPF's `ContextMenu` instead of WinUI's),
   in-place rename (UnoDevelop drives this through `_host.ShowInputBox` rather
   than native inline edit — same approach works in WPF), and drag-drop (not
   confirmed present in UnoDevelop; if needed, this is new work either way)
   all need fresh WPF-side implementation.

## Rungs

### R6a — CPS shim import
- [ ] Add/vendor the `externals/project-system` submodule (or copy the
  specific linked files) into OpenDevelop.
- [ ] Copy `UnoDevelop/src/Main/ProjectSystem/` into
  `OpenDevelop/src/Main/ProjectSystem/` verbatim; convert its csproj to
  SDK-style (`Microsoft.NET.Sdk`, no `UseWPF` needed — it's a plain library)
  following the same conversion rules already used for
  `ICSharpCode.TypeSystem.Abstractions`.
- [ ] Copy `UnoDevelop/src/Main/ProjectSystemManaged/` similarly; wire its
  `externals/project-system` file links to OpenDevelop's own submodule path.
- [ ] Build standalone (own MVP-style mini-solution or as a `ProjectReference`
  probe from a throwaway console project) before wiring into the real
  workbench — same "convert one project at a time" discipline as the rest of
  this port.

### R6b — Node model + provider
- [ ] Copy `SolutionExplorerNodeModel.cs`, `SolutionExplorerNodeContext.cs`,
  `SolutionExplorerNodeProperties.cs`, `UnoSolutionExplorerController.cs`,
  `UnoDevelopProjectTreeProvider.cs` into
  `OpenDevelop/src/Main/SharpDevelop/Services/`.
- [ ] Rename `Uno*` types (`UnoSolutionExplorerController` →
  `SolutionExplorerController`, etc.) and strip the couple of WinUI-typed
  edges (they'll be replaced by WPF types in R6c).
- [ ] Wire `Commands/SolutionExplorerAddInCommands.cs` and
  `Conditions/SolutionExplorerConditionEvaluators.cs` the same way — these are
  already SharpDevelop addin-tree pattern, no UI-framework coupling.

### R6c — WPF tree view (new work)
- [ ] Write a WPF `SolutionExplorerPad` (AvalonDock pad) hosting a `TreeView`/
  `HierarchicalDataTemplate` bound to the CPS-derived node model.
- [ ] Write the WPF equivalent of `CpsTreeConverter.cs` (CPS `IProjectTree` →
  WPF-bindable node), reusing `SolutionExplorerNodeContext`'s `Kind`/`IconUri`/
  `State`/`ContextMenuPath` fields UnoDevelop's converter already computes.
- [ ] Wire context menus through the existing `ContextMenuPath` →
  `MenuService`/`ICSharpCode.Core.Presentation` (already ported, R4 done) —
  reuse the WPF menu-building code already in the app rather than anything new.
- [ ] In-place rename via `ShowInputBox` (matches UnoDevelop's approach, no
  native inline-edit dependency needed).
- [ ] Icons: use `SD.ResourceService`/`PresentationResourceService` (already
  fixed for real icon resolution this session) keyed by `IconUri`/`Kind`.

### R6d — Feature parity pass
- [ ] Multi-project solutions, nested folders, References/Packages/
  Dependencies grouping nodes — confirm behavior against UnoDevelop's already-
  implemented set (see UnoDevelop's own `doc/solution-explorer.md` milestone
  list for what's done vs still narrow, e.g. Show All Files physical-file
  enumeration policy).
- [ ] Include/Exclude, Add New Item/Project, Remove Reference, Set Startup
  Project, Copy Path, Open With — via `UnoSolutionExplorerController`'s
  already-defined command surface (R6b), just needs a WPF host implementing
  `IUnoSolutionExplorerHost`/its renamed equivalent.
- [ ] Drag-drop (project reorder, file move) — new work if wanted for MVP;
  UnoDevelop doesn't confirm having this either, so no reference implementation
  to lean on.

## Non-goals for this pass

- Full VS MEF composition fidelity beyond what UnoDevelop's shim already does
  (per-project-scoped composition is intentionally simplified there via
  `Dataflow/ManualComposition.cs` — don't attempt to build real VS-style
  scoped composition from scratch, reuse UnoDevelop's simplification).
- SharpTreeView/legacy tree path — stays excluded per MVP policy 3, not a
  fallback if CPS integration hits friction.

## References

- `UnoDevelop/doc/project-system.md` — the 50-slice incremental build log for
  the CPS shim itself (Slice 1 `ProjectTreeFlags` through Slice 50 external-
  edit reload). Read before re-deriving any of this from scratch.
- `UnoDevelop/doc/solution-explorer.md` — UnoDevelop's own 4-milestone
  Solution Explorer plan and current gap list.
- [[open-source-cps-shim]] — why this is a clean-room MIT reimplementation,
  not decompiled from the closed CPS SDK.
- `docs/opendevelop.md` MVP policy 3/4 — hard constraints this plan must stay
  inside (no SharpTreeView, no legacy WinForms Solution Explorer, WPF+CPS only).
