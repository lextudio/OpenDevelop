# Workflow Designer (Windows Workflow Foundation)

Tracking note for [orosandrei/Rehosted-Workflow-Designer#21](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/21)
("Integration with OpenDevelop and LibreWPF"), opened by this repo's maintainer against their own
WF rehosting sample project. That issue also asks to fold in
[#19](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/19) (.NET 8 migration) and
[#18](https://github.com/orosandrei/Rehosted-Workflow-Designer/issues/18) (step/step-in/step-over
debugging) where possible.

## Current state in this repo

`src/AddIns/DisplayBindings/WorkflowDesigner/` exists but is **not part of the solution** (not
referenced by `SharpDevelop.sln`/`SharpDevelop.Tests.sln`) and hasn't been touched since the
SharpDevelop-era commit `137bb94ea0` ("Replace WorkflowDesigner with simple designer for Workflow
Foundation 4.0"). It's a single in-process `.addin`:

- `WorkflowDisplayBinding.cs` — `ISecondaryDisplayBinding` that sniffs the root `xmlns` of any
  opened `.xaml` file for `http://schemas.microsoft.com/netfx/2009/xaml/activities` and, if it
  matches, attaches a secondary view.
- `WorkflowDesignerViewContent.cs` — wraps `System.Activities.Design.WorkflowDesigner` directly,
  hosts its `.View` as the WPF content and `.PropertyInspectorView` in the Properties pad, and
  round-trips `.Load()`/`.Flush()`/`.Text` against the `OpenedFile` stream.
- Targets `net4.0`, references `System.Activities.Design`, `System.Activities.Design.Base`,
  `System.Activities.Core.Design` — .NET Framework reference assemblies that ship only inside the
  Windows `%windir%\Microsoft.NET\Framework\...` GAC, not as NuGet packages for `net5.0`+.

This is the opposite shape from every other designer in the repo: WinForms/WPF/WinUI/MewUI/GTK4
all converged on the out-of-process DDP host pattern in
[`designer-common.md`](designer-common.md) precisely because their design surfaces had to run
somewhere other than "in-process, full .NET Framework, Windows-only." The workflow addin never
needed to make that jump because it was written for, and abandoned on, classic SharpDevelop/.NET
Framework — before OpenDevelop's LibreWPF/net10.0/cross-platform baseline existed at all.

## The actual blocker: there is no ported design surface to reuse

Every other OpenDevelop designer had *something* to converge on: WPF's engine is open-sourced
(`dotnet/wpf`, reused via `externals/vscode-wpf`), WinForms' designer surface ships as source in
`dotnet/winforms`, and the WinUI/Uno path reuses XAML Studio's renderer. Windows Workflow
Foundation's **design-time** surface never got that treatment:

- `System.Activities` (the runtime) was ported to modern .NET by UiPath as
  [CoreWF](https://github.com/UiPath/CoreWF) and is genuinely cross-platform (`net6.0`+,
  `net6.0-windows`, works on macOS/Linux for executing/tracking workflows).
- `System.Activities.Design` / `System.Activities.Core.Design` / `System.Activities.Presentation`
  (the rehosted **designer** — the actual drag/drop canvas, activity designers, expression
  editor) were **never open-sourced and never ported**. UiPath tracked this explicitly in
  [CoreWF#58 "Porting Workflow Designer to .NET Core"](https://github.com/UiPath/corewf/issues/58)
  and it was never completed; their stated direction was a browser-based designer instead of a
  ported WPF one. These assemblies remain .NET Framework 4.x-only, Windows-only, closed-source.
- Andrei Oros's own [2017 write-up](https://andreioros.com/blog/windows-workflow-foundation-2017/)
  (linked from the Rehosted-Workflow-Designer README) reaches the same conclusion: the designer
  is stuck on .NET Framework even as the runtime moves forward.

So unlike WPF/WinForms/WinUI, there is no upstream engine to link, port, or reuse as source —
`System.Activities.Design`'s actual designer-surface code isn't available to port at all. Any
"rehost the WF designer on LibreWPF" plan has to treat the classic designer as a sealed,
Windows-only, .NET Framework-only artifact and design around it, not through it.

## Decision: no existing designer to reuse — build a new one on CoreWF

The instinct to check whether `orosandrei/Rehosted-Workflow-Designer` itself could just become
"the .NET 10 designer" doesn't pan out: its `RehostedDesigner.csproj` is an old-style,
`packages.config`-based project pinned to `TargetFrameworkVersion v4.5.2` (last feature commit in
2021; everything since is dependency-bump PRs), and it references the same closed-source
`System.Activities.Design`/`.Presentation` assemblies as this repo's dead addin. There is no
version of it, or of anything else found so far, that runs on .NET 6+ — that matches CoreWF's own
unresolved [issue #58](https://github.com/UiPath/corewf/issues/58). A Windows-only `net48`
out-of-process host (HWND-embedding the real Microsoft designer, as sketched in an earlier draft
of this note) would work, but was rejected: the project-wide direction is .NET 10 across the
board with CoreWF as the workflow foundation, and a `net48`-only child would be a permanent,
Windows-only exception to that, not a stepping stone off of it.

So the only option that reaches "cross-platform, .NET 10, CoreWF-based" is to build a genuinely
new design surface — in the same sense that OpenDevelop's WPF/WinForms/WinUI designers are real,
from-scratch (or adapted-source) design surfaces, not thin wrappers around a Microsoft-owned
control. CoreWF supplies the activity/expression *model* (the `System.Activities.Statements.*`
types, `DynamicActivity`, XAML round-trip via `ActivityXamlServices`) but nothing to render a
canvas from it — that half has to be written.

```text
OpenDevelop (net10.0, LibreWPF shell, any OS)
        │ DDP over the shared pooled-host RPC transport (designer-common.md)
        ▼
WorkflowDesigner.SurfaceHost (net10.0, out-of-process child)
        - loads the .xaml activity tree via CoreWF's ActivityXamlServices (real runtime types,
          not a hand-rolled XAML parser — mirrors designer-common.md's "run the real runtime
          object, don't reimplement it" rule)
        - owns the live activity object graph, selection/edit model, and undo history for the
          document lease, same as WpfDesign.SurfaceHost owns the WPF DOM today
        - a WPF canvas in the *host* renders that activity tree as boxes/connectors (Sequence,
          Flowchart, If, custom activities, ...); the parent process gets a rendered
          frame/hit-test surface over DDP, not a live control reference
        - Toolbox catalog derived by reflecting CoreWF/project activity assemblies inside the
          host, sent to the parent as data (same shape as the other four backends' Toolbox RPC)
        - Properties pad driven by ordinary `TypeDescriptor`/reflection over the selected
          activity, marshalled through the shared Properties adapter contract, not
          System.Activities.Design's PropertyInspectorView
WorkflowDesigner.AddIn (in the main process)
        - ISecondaryDisplayBinding + view content, thin RPC client over the host, same shape as
          WpfDesign.AddIn/FormsDesigner's parent-side pieces
```

This follows the same shared-host/DDP shape every other backend in
[`designer-common.md`](designer-common.md) already converged on — WinForms, WPF, WinUI/Uno,
MewUI and GTK4 all run the real runtime object in a child process and speak the wire protocol to
it, and there's no reason for the workflow designer to be the exception. Loading arbitrary
project-authored activity assemblies (custom activities are just CLR types the target project
references) into the child is exactly the kind of untrusted-code boundary the OOP model exists
for in the first place — it must not run in OpenDevelop's own process.

Scope carried over from the earlier draft, still true:

- **Debugging (issue #18)** is a `WorkflowApplication`/`TrackingParticipant` integration against
  CoreWF, wired into the existing DAP plumbing ([`debugging.md`](debugging.md)) — independent of
  the canvas work and cross-platform on its own.
- **Issue #19** (.NET 8/CoreWF migration) is subsumed: there's no migration path for the old
  `System.Activities.Design`-based code, so the new addin starts on .NET 10/CoreWF directly
  rather than migrating the dead code forward.

## Phased plan

1. **Delete `src/AddIns/DisplayBindings/WorkflowDesigner/`** (the current dead, unbuilt `net4.0`
   addin) rather than evolving it — its `WorkflowDisplayBinding`/`WorkflowDesignerViewContent`
   are wrappers around exactly the assembly (`System.Activities.Design`) this plan avoids
   depending on, so nothing in them survives the rewrite.
2. **Stand up CoreWF as a dependency** — done as a spike: `UiPath.Workflow` 6.0.3 (the CoreWF
   NuGet package; resolves fine through the existing `nuget.org` source) loads a `Sequence`/
   `WriteLine` activity from a `.xaml` file via `ActivityXamlServices.Load` and runs it through
   `WorkflowInvoker.Invoke` on `net10.0` on macOS — confirmed working, not just assumed. Real
   dependency wiring (adding the package to this repo's actual addin project, not a scratch
   probe) still needs doing in step 3.
3. **Minimal host + addin pair — done.** `WorkflowDesigner.Host` (child) and `WorkflowDesigner`
   (the addin, `ICSharpCode.WorkflowDesigner`) exist, build, and are registered in
   `OpenDevelop.Mvp.slnx` (the solution `rebuild-all.sh`/`launch.sh` actually build — confirmed
   neither this nor the old `SharpDevelop.sln`/`.Tests.sln` ever listed the dead addin either).
   `WorkflowDocument` in the host loads/saves through `ActivityXamlServices.CreateBuilderReader`/
   `CreateBuilderWriter` (the same mechanism the classic in-process designer used internally) —
   confirmed end to end in a scratch probe: load → walk via `WorkflowInspectionServices` → read/
   write an `InArgument<T>` literal via reflection → round-trip save. On the addin side,
   `WorkflowDesignerHostClient`/`WorkflowPropertyAdapter` are adapted directly from
   `MewUIDesignerHostClient`/`WpfSurfaceElementPropertyAdapter` rather than written fresh, and
   `WorkflowDesignerViewContent` renders nested boxes with a real toolbox add/delete round-trip
   (collection containers plus single-child CoreWF slots such as `If.Then`/`Else` and
   `While.Body`). It also already adopted the shared-shell contracts
   from designer-common.md's 2026-08-24 "push for more code reuse" pass —
   `DesignerSelectionController`'s ordered multi-selection, `DesignerPadController` for the
   Outline/Properties bridge, `DesignerMultiPropertyAdapter` for multi-select property editing,
   and `DesignerCommandController` for the Delete command — instead of the hand-rolled
   event-wiring an earlier draft of this file used, so it doesn't start life as a sixth
   almost-identical copy of that plumbing. Versioned CoreWF XAML history powers standard
   Undo/Redo, and toolbox insertion supports both double-click and drag/drop. The workflow-level
   Arguments panel is also live: it projects the `ActivityBuilder` properties, creates, deletes,
   renames, retargets argument types and edits literal defaults through the host, and every mutation participates in the
   same history. A root-scope Variables panel provides the same create/delete, versioned and
   undoable baseline for activities that expose a CoreWF `Variables` collection. Nested scope,
   default values, custom-activity discovery and expression editing remain step 4 work.
4. **Toolbox + additional activity shapes**: `If`/`Flowchart`/custom activities, drag-drop from a
   `ToolboxControl` populated from CoreWF + referenced activity-library assemblies (paralleling
   `ActivityLibraries/` in the upstream sample), and an expression editor (CoreWF supports C#
   expressions via Roslyn; VB expression support is optional/lower priority). This step is also
   where the UX-parity items below get built in, not a separate pass — they're the same "toolbox
   and canvas interaction" surface, just shaped to match Visual Studio's conventions instead of an
   arbitrary new one.

### UX parity target: Visual Studio's Workflow Designer

The instruction from here on is explicit: match Microsoft's own rehosted designer's user
experience, not invent a new one. The full reference is
[`MicrosoftDocs/visualstudio-docs/docs/workflow-designer`](https://github.com/MicrosoftDocs/visualstudio-docs/tree/main/docs/workflow-designer)
(mirrored at [learn.microsoft.com/visualstudio/workflow-designer](https://learn.microsoft.com/en-us/visualstudio/workflow-designer/developing-applications-with-the-workflow-designer)) —
per-activity-designer pages (Sequence, If, Flowchart, ...), dialog-box references, and the shell
docs below. Concrete conventions to replicate, most-to-least load-bearing:

- **Three-part shell** ([`workflow-designer-shell-features.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/workflow-designer-shell-features.md)):
  a breadcrumb bar above the canvas, the canvas itself, and a shell bar below it with zoom in/out,
  fit-to-screen, and an overview map (a viewport rectangle over a thumbnail of the whole tree —
  the designer is virtualized, so undrawn regions show blank until scrolled into view once).
  `WorkflowDesignerViewContent` now supplies breadcrumb drill-in, in-place and global tree
  expansion, shared zoom/fit chrome and a clickable activity-tree overview. The overview's
  scroll viewport rectangle remains future work.
- **Breadcrumb drill-in — implemented for activity trees** ([`how-to-use-breadcrumb-navigation.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-breadcrumb-navigation.md)):
  double-click an activity to make it the new root (fully expanded, ancestors listed as breadcrumb
  buttons); click an ancestor to go back up; chevrons expand/collapse an activity in place, with
  global Expand All/Collapse All/Restore. `Flowchart`/`Switch`/`TryCatch` opt out of in-place
  expand — worth remembering once those shapes exist (step 4's `Flowchart` item).
- **Arguments/Variables designers** ([`how-to-use-the-argument-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-argument-designer.md),
  [`how-to-use-the-variable-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-variable-designer.md)):
  buttons in the canvas's lower-left corner open a tabular grid with a `Create Argument`/
  `Create Variable` blank row (name/direction/type/default, or name/type/scope/default); Delete
  key removes the selected row. The addin now exposes an **Arguments** panel from the breadcrumb
  bar (and `Ctrl+E`, then `A`): it reads the workflow `ActivityBuilder` properties and supports
  create, delete, rename, type changes and literal default-value changes, with each operation versioned and undoable. It currently
  supports the common `String`, `Int32`, `Boolean`, `Double`, and `Decimal` types plus fully
  qualified CLR type names. The adjacent **Variables** panel (and `Ctrl+E`, then `V`) provides
  create/delete for the root activity's `Variables` collection and displays its scope; nested
  scopes, CoreWF direction editing and expression-bound defaults remain step 4 work.
- **Expression editor** ([`how-to-use-the-expression-editor.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-use-the-expression-editor.md)):
  renders as a plain `TextBlock` until focused, then becomes a real (VB-syntax) expression editor
  with IntelliSense-in-VS-only, and is also reachable via an ellipsis button from the property
  grid as a dialog. Our current property pad already edits `InArgument<string>` literals as plain
  strings (`WorkflowDocument.ConvertToPropertyType`); a real expression editor is out of scope
  until step 4 adds non-literal expression support, but the click-to-edit TextBlock/TextBox
  behavior itself is a cheap, worthwhile match once that lands.
- **Toolbox interaction details — done** ([`how-to-add-activities-to-the-toolbox.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/how-to-add-activities-to-the-toolbox.md)):
  empty containers now show **"Drop activity here"** hint text instead of rendering blank, and an
  activity's `DisplayName` is directly editable on its header via double-click (a `TextBlock` that
  swaps for a `TextBox`, committing on Enter/lost-focus, reverting on Escape) — not only through
  the Properties pad — matching `sequence-activity-designer.md`'s callout for `Sequence`.
- **Zoom/Fit shell chrome — done**: `WorkflowDesignerViewContent` now hosts its canvas inside the
  shared `ICSharpCode.SharpDevelop.Widgets.DesignerCanvas` control every other backend (WinForms,
  WPF, WinUI/Uno, GTK4, MewUI) already uses for its zoom/fit/gridlines/theme toolbar
  (designer-common.md) — the closest in-repo equivalent to VS's shell-bar docs
  (`workflow-designer-shell-features.md`), reused as-is rather than building bespoke chrome.
  Gridlines/Theme/ShowNames/DesignSize are left off (`Capabilities = Zoom | Fit`) since none map
  to an activity tree. Breadcrumb drill-in and a clickable overview are implemented; coupling
  the overview to a precise ScrollViewer viewport rectangle remains deferred.
- **Keyboard shortcuts** ([`keyboard-shortcuts-in-the-workflow-designer.md`](https://github.com/MicrosoftDocs/visualstudio-docs/blob/main/docs/workflow-designer/keyboard-shortcuts-in-the-workflow-designer.md)):
  a full `Ctrl+E, <letter>` shortcut family (Arguments, Variables, expand/collapse, flowchart
  connect, next-item focus, ...). `Ctrl+E`, then `A` opens Arguments and `Ctrl+E`, then `V` opens
  root-scope Variables; the remaining members should be bound when their matching panels/features
  arrive.

None of the per-activity-designer pages (`if-activity-designer.md`, `flowchart-activity-designer.md`,
`assign-activity-designer.md`, etc.) are reproduced here; read the relevant one directly from the
docs tree above when implementing that activity's box in step 4 — each documents its exact
property surface and designer-specific behavior (e.g. `Flowchart`'s connector gestures).
5. **CoreWF execution/tracking integration** for #18, independent of 2–4 and shippable on all
   platforms once the runtime dependency from step 2 is in place.
6. **Non-Windows verification**: since the new addin targets net10.0 cross-platform rather than
   being Windows-gated, explicitly test load/render/edit/save on macOS (matching this repo's
   LibreWPF baseline) as part of accepting each phase above, not as an afterthought.

The CoreWF host/addin baseline and the interaction slices recorded above are implemented; this
note remains the architecture and phased roadmap for the unfinished workflow-specific features.
