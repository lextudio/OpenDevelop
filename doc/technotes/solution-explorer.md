# Solution Explorer (WPF, CPS-backed)

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
