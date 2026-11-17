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
  `Resources/VS2026/Browser/Browser_16x.xaml` lookup that doesn't exist. Added an alias to
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

**Shared implementation (2026-07-28 status, trimmed):** node model, item resolution, CPS tree
provider, the command/business-logic layer, CPS-flag kind resolution and git status each have one
implementation; only native dialog/clipboard calls are host-specific. The plan below (rungs R6c-R6d)
reads as if none of this had started; in practice OpenDevelop already had a complete, running
`ProjectBrowser*`-named implementation (pad, WPF `TreeView`, icon/overlay services,
`SharpDevelopProjectTreeProvider`).

- **Node model** (`ProjectBrowserNodeContext`/`NodeProperties`/`NodeModel`). `GitFileStatus` is a
  field on `ProjectBrowserNodeContext`; the enum itself lives in the Base layer alongside
  `GitStatusService` (see "Git status" below) since `GitAddIn` needs it too and only references
  Base. WPF-only rendering (`Icon`/overlay `ImageSource` properties) is split into
  `ProjectBrowserNodeModel.Wpf.cs`.
- **Project item resolution** (`ProjectDisplayItems.GetProjectDisplayItems`/
  `GetEvaluatedDependencyItems`) - host-agnostic (operates on `IProject`/`MSBuildBasedProject`),
  including bin/obj/.git/.vs path exclusion for MSBuild items that legitimately have no
  `Visible="false"` metadata (e.g. Uno.Resizetizer-generated sources). Addins extend this without shell changes through
  `IProjectFileNestingProvider`: they declare the extra file types they own and return a
  display-relative parent path for each child. Registration through
  `ProjectFileNestingProviderRegistry` raises a change event so an already-open Projects pad
  refreshes. The GTK designer is the first consumer (`.ui`, `.ui.cs`, `.css` under its behavior
  class); a provider can scope itself to selected project files and is removed when its AddIn unloads.
  Explicit `DependentUpon` remains authoritative. When two providers claim a fallback, their
  `Priority` is consulted first (highest wins) and AddIn registration order is the stable tie-breaker;
  addins should normally scope their rules rather than compete for a generic extension.
- **CPS tree provider** (`SharpDevelopProjectTreeProvider`) - the SDK-style-project dependency
  branch reads evaluated MSBuild items via `MSBuildBasedProject.GetEvaluatedProjectItems()`, a live
  code path (it relies on the same `OpenConfiguration`/`OpenCurrentConfiguration` machinery as
  `GetEvaluatedProperty`, which `MtpTestProject.ResolveAssemblyDll` already exercises - see
  `unit-testing.md`).
- **Command/business-logic layer** (`ProjectBrowserController`) - create/rename/delete/import file
  or folder, include/exclude, remove reference, remove project, open-with, open-folder,
  set-startup-project, new-item/new-project orchestration around a template. It is split into
  `ProjectBrowserControllerBase` (shared, abstract) plus a small concrete subclass supplying the
  genuinely native touchpoints (`ShowNewItemDialogAsync`/`ShowNewProjectDialogAsync`/
  `CopyTextToClipboard`) via host-neutral `NewItemDialogOutcome`/`NewProjectDialogOutcome` records.
  The T4-template `CustomTool="TextTemplatingFileGenerator"` auto-set lives in the shared base.
- **CPS-flags → `ProjectBrowserNodeKind` resolution** - `ProjectBrowserTreeKindResolver.ResolveKind`,
  called by the tree builder. It distinguishes ghost/ready-to-include and missing files via CPS's own
  `FileSystemEntity`/`IncludeInProjectCandidate` flags, rather than an extra `File.Exists` disk check
  per node (a ready-to-include file exists on disk, so a `File.Exists`-based check would always see
  it as a plain `File`, not `GhostFile`).
- **Git status** - `GitStatusService`/`GitFileStatus` provide proper porcelain-v1 X/Y parsing,
  cross-platform `git` discovery and Untracked/Ignored/Renamed/Conflicted states. They live in
  `Main/Base/Project/Src/Services/ProjectBrowser/` (Base layer, not the App-layer
  `SharpDevelop.csproj` - `GitAddIn` only references Base, so a type it needs can't live in App).
  `GitAddIn`'s `OverlayIconManager` computes its `ImageSource` badges from the shared
  `GitFileStatus`; the older `GitStatusCache` engine (`git ls-files` +
  `status --porcelain --untracked-files=no`) is gone. One deliberate behavior change: the old engine
  also badged every clean tracked file with a green checkmark; the shared engine doesn't distinguish
  "clean and tracked" from "not in a git repo at all" (both report `GitFileStatus.None`), so only
  non-clean files get badged, matching VS/VS Code convention.

The three `ProjectBrowserControllerBase` overrides and the WPF-only `FileDialogService`
(`Microsoft.Win32.OpenFileDialog`/`OpenFolderDialog`) remain native UI code with no shared business
logic to extract.

## Goal

Replace the legacy WinForms/SharpTreeView-based Solution Explorer (excluded
from MVP per `docs/opendevelop.md` MVP policy 3) with a new WPF Solution
Explorer backed by a .NET Project System (CPS) shim, instead of reviving
the legacy mixed tree pipeline (MVP policy 4).

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
  related — only the CPS shim's own types keep
  the real `Microsoft.VisualStudio.ProjectSystem` namespace (needed to link
  unmodified upstream MIT source), and only the WPF view layer gets new
  OpenDevelop-specific code.

## Rungs

### R6c — WPF tree view (new work)
- [ ] Write a WPF `SolutionExplorerPad` (AvalonDock pad) hosting a `TreeView`/
  `HierarchicalDataTemplate` bound to the CPS-derived node model.
- [ ] Write the CPS `IProjectTree` → WPF-bindable node converter, reusing
  `SolutionExplorerNodeContext`'s `Kind`/`IconUri`/`State`/`ContextMenuPath` fields.
- [ ] Wire context menus through the existing `ContextMenuPath` →
  `MenuService`/`ICSharpCode.Core.Presentation` (already ported, R4 done) —
  reuse the WPF menu-building code already in the app rather than anything new.
- [ ] In-place rename via `ShowInputBox` (no native inline-edit dependency needed).
- [ ] Icons: use `SD.ResourceService`/`PresentationResourceService` (already
  fixed for real icon resolution this session) keyed by `IconUri`/`Kind`.

### R6d — Feature parity pass
- [ ] Multi-project solutions, nested folders, References/Packages/
  Dependencies grouping nodes.
- [ ] Include/Exclude, Add New Item/Project, Remove Reference, Set Startup
  Project, Copy Path, Open With — via the node model's command surface
  (R6c), just needs a WPF host implementing the controller's host interface.
- [ ] Drag-drop (project reorder, file move) — new work if wanted for MVP.

### Legacy menu codons that still point at excluded classes (GitHub issue 10)

`MenuService.CreateContextMenu` builds **every** codon under a node's path, from both
`ICSharpCode.SharpDevelop.addin` (legacy) and `ICSharpCode.SharpDevelop.ProjectBrowser.addin`
(new), and same-id submenus share one child node (`AddInTreeNode.BuildChildItems`). So a legacy
`MenuItem` whose class sits in the excluded `Src\Gui\Pads\ProjectBrowser\**` is not dormant - it is
shown, and clicking it raises `Cannot find class`. Issue 10 was the folder node's legacy
"Add > Existing Folder" (`AddExistingFolderToProject`).

The paths the WPF pad actually opens are the six in `ProjectBrowserNodeContext.ContextMenuPath`
(SolutionNode, ProjectNode, FolderNode, FileNode, ReferenceNode, PackageReferenceNode) plus
whatever they `Include` (ProjectActions, CutCopyPasteDeleteRename, CutPasteRemoveRename,
FolderNode/Add, SolutionNode/CollapseAll) and the main menu. Every other legacy node path
(WebReference*, ServiceReference*, SolutionItemNode, ...) is only reached by the excluded WinForms
tree nodes. Rule applied to each broken codon on a reachable path:

- the new addin already offers it on that node -> commented out `MVP: removed` (keeping it would be
  a duplicate even once fixed);
- a new command exists but that node lacks it -> `class` redirected (Rename, Set Startup Project,
  Collapse All, File > New > Project, Run, Add Reference on the main Project menu);
- no replacement -> commented out.

Legacy entries that *work* still duplicate new ones (two "Add", "Cut", "Paste", "Rename" on a
project node); removing those is a separate dedup pass. To re-check after a change, compare each
`.addin` `class=` against the TypeDefinitions of the built assemblies (the `#US`-heap caveat does
not apply - type names are in `#Strings`) and keep only hits on the reachable paths above.

Replacements written for the WPF pad (`ProjectBrowserAddInCommands.cs` ->
`ProjectBrowserControllerBase`): `CollapseAll`, `OpenTerminal` ("Open in Terminal" in
Common/Open and SolutionNode), `RunProject`/`RunProjectWithoutDebugger`, and `AddReference`
with a WPF `AddReferenceWindow` (solution projects + browsed assembly files only; NuGet stays in
Manage Packages, GAC/COM/Service/Web references are not offered). `ShowAddReferenceDialogAsync` is
`virtual`, not abstract, so the other host sharing the base class is not forced to implement it.
A `ProjectReference` is written with its `Project`/`Name` metadata removed - SDK-style projects
resolve it by path.

### Solution folders and solution items

`ProjectBrowserTreeBuilder.BuildSolutionTree` walks `ISolution.Items` recursively (it used to list
`solution.Projects` flat, so solution folders and loose files never appeared). Two node kinds were
added: `SolutionFolder` (virtual - `FullPath` is empty, `BoundItem` is the `ISolutionFolder`) and
`SolutionItem` (a file in a solution folder; `IsFileNode` but deliberately not `IsFileLike`, so the
project-item commands - rename/delete on disk, include/exclude, custom tool - never apply to it).
Their menus are `ContextMenu/SolutionFolderNode` (Add > Existing Project / New Solution Folder /
Existing Item, Rename, Remove) and `ContextMenu/SolutionItemNode` in the new addin. Removing a
folder takes its projects out of the solution (after a confirmation naming the count); no file is
deleted. Items added on the solution node itself go to a "Solution Items" folder, because `.slnx`
has no top-level `<File>`.

Two `.slnx` bugs surfaced on the way, both pre-existing:

- `Solution.Save()` always wrote the classic `.sln` text format, so any save of a `.slnx` solution
  replaced the XML with `.sln` text. `SlnxSolutionWriter` now handles `.slnx`: it loads the existing
  XML and only synchronises `Folder`/`Project`/`File` elements with the model, keeping every
  surviving element's attributes and children (per-project BuildType/Platform rules etc., which the
  model does not carry).
- `SlnxSolutionLoader` turned `<Folder Name="/src/tests/">` into a top-level folder called
  "src/tests". `.slnx` lists nested folders flat by full path; the loader now nests them by path.

Also: `TryRemoveProject` only edited the in-memory model, so removing a project from a solution was
never written to disk; `RemoveFromProject` now saves the solution afterwards.

## Non-goals for this pass

- Full VS MEF composition fidelity (per-project-scoped composition is intentionally simplified via
  `Dataflow/ManualComposition.cs` — don't attempt to build real VS-style
  scoped composition from scratch).
- SharpTreeView/legacy tree path — stays excluded per MVP policy 3, not a
  fallback if CPS integration hits friction.

## References

- [[open-source-cps-shim]] — why this is a clean-room MIT reimplementation,
  not decompiled from the closed CPS SDK.
- `docs/opendevelop.md` MVP policy 3/4 — hard constraints this plan must stay
  inside (no SharpTreeView, no legacy WinForms Solution Explorer, WPF+CPS only).
