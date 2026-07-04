# Solution Explorer (WPF, CPS-backed)

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
