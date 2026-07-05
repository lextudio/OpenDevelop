# C# Type System: Replace NRefactory *and* the SD mock abstraction with Roslyn

## Status update (this revision)

The previous version of this note scoped "Roslyn migration" narrowly to
`CSharpBinding` (parsing/completion/refactoring for that one add-in). That
undersold the actual problem: `src/Libraries/ICSharpCode.TypeSystem.Abstractions`
is itself a **mock** replacement for NRefactory's type system
(`ICSharpCode.NRefactory.TypeSystem`) — its own file headers say so explicitly
(`Mocks/MiscMocks.cs`, `Mocks/ResolveResults.cs`, `ClassBrowserIconServiceStub.cs`).
Nothing in the codebase is backed by a real compiler today: `SD.ParserService`
has zero registered `IParser` for `.cs` (CSharpBinding isn't in the MVP build),
so `IUnresolvedFile`/`ICompilation`/`ResolveResult` are always empty/null in
practice.

Decision: **do not build out `ICSharpCode.TypeSystem.Abstractions` further.**
Any code that needs real semantic information (base types, derived types,
XML doc comments, go-to-definition, etc.) should talk to
`Microsoft.CodeAnalysis` (Roslyn) directly — `ISymbol`, `INamedTypeSymbol`,
`Compilation`, `SemanticModel`, `SymbolFinder` — not through `ICSharpCode.TypeSystem`
interfaces. Where old SD code already routes through `IEntity`/`IType`/`ResolveResult`,
prefer rewriting the call site against Roslyn types over extending the mock.
`ICSharpCode.TypeSystem.Abstractions` and the old `ICSharpCode.SharpDevelop.Refactoring`
NRefactory-era engine (`FindReferenceService`, `InheritanceHelper`-shaped call sites,
`TypeGraphNode`) are to be phased out, file by file, as their consumers are migrated.

## Phase 0: Foundation (DONE this session)

- Add `Microsoft.CodeAnalysis.CSharp.Workspaces` (pulls in `Microsoft.CodeAnalysis.CSharp`,
  `Microsoft.CodeAnalysis.Common`, `Microsoft.CodeAnalysis.Workspaces.Common`) to
  `AvalonEdit.AddIn.csproj`. Packages are already present in the local NuGet cache
  (`~/.nuget/packages/microsoft.codeanalysis.csharp.workspaces/5.3.0`).
- New `RoslynWorkspaceHelper` (`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/Roslyn/`):
  an `AdhocWorkspace` fed directly from `SD.ProjectService`'s already-loaded `IProject`s
  (source files via `IProject.GetItemsOfType(ItemType.Compile)`, metadata references via
  `IProject.ResolveAssemblyReferences(...)`). Exposes `Solution`, and
  `GetSymbolAtCaret(ITextEditor)` (token → declared symbol or bound symbol via `SemanticModel`).
  Rebuilt/refreshed on demand (lazy, not incrementally diffed yet — acceptable for MVP;
  a real implementation would hook `OpenedFile` change events).
- Rewrite the 3 broken AvalonEdit.AddIn consumers to use `Microsoft.CodeAnalysis` symbols
  end-to-end, no `ICSharpCode.TypeSystem` involved:
  - `GoToEntityAction.cs` — takes `ISymbol`, navigates via `symbol.Locations[0].GetLineSpan()`.
  - `FindBaseClasses.cs` (`FindBaseClassesOrMembers`) — `INamedTypeSymbol.BaseType`/`.AllInterfaces`
    chain; override/explicit-interface-impl walk via `IMethodSymbol.OverriddenMethod` /
    `.ExplicitInterfaceImplementations` (and the property/event equivalents), plus an
    implicit-interface-implementation reverse lookup via `containingType.FindImplementationForInterfaceMember`.
  - `FindDerivedClassesOrOverrides.cs` — `SymbolFinder.FindDerivedClassesAsync` /
    `FindDerivedInterfacesAsync` / `FindOverridesAsync` against `RoslynWorkspaceHelper.Solution`.
  - `XmlDocTooltipProvider.cs` — `ISymbol.GetDocumentationCommentXml()` parsed into the
    (still SD-shaped, but now un-excluded) `ICSharpCode.SharpDevelop.Editor.DocumentationUIBuilder`,
    via a new `ICSharpCode.TypeSystem.XmlDocumentationElement` — a plain `System.Xml.Linq`-backed
    adapter (`src/Libraries/ICSharpCode.TypeSystem.Abstractions/XmlDocumentationElement.cs`) with
    no compiler-specific dependency of its own; its `ReferencedEntity` is always `null` (no
    clickable `cref` links yet), everything else (summary/param/returns/remarks/lists/code
    blocks) renders through the existing, more complete renderer instead of a hand-rolled one.
    `XmlDocFormatter.cs` itself (the old `IType`/`IEntity`-shaped entry point) stays excluded.
  - Both commands stop deriving from `ResolveResultMenuCommand` (which only knows how to
    produce SD `ResolveResult`s, and can't today since no `IParser` exists) — they become
    plain commands that resolve their own Roslyn symbol at the caret.
- These 4 files become the first real (non-mock) semantic-analysis code in the repo.
- Along the way, restored `ReadOnlyDocument` (`src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/Document/`)
  from git history (`git show a5776d437b~1:.../NRefactory/Editor/ReadOnlyDocument.cs`, ported to
  `ICSharpCode.AvalonEdit.Document`) plus a `CreateDocumentSnapshot()` extension — this was never
  an NRefactory-*type-system* dependency (no `IType`/`IEntity` involved), just a read-only
  `IDocument` wrapper that `AvalonEdit.AddIn`'s change-margin/diff code needs and that never got
  carried over when NRefactory was removed (nothing referenced it until `AvalonEdit.AddIn` was
  wired into the build this session). `DocumentHighlighter` only accepts a live `TextDocument`
  (it registers a `WeakLineTracker`), so the 2 call sites that feed it a read-only buffer
  (`ChangeMarkerMargin`'s diff popup, `DocumentationUIBuilder.AddCodeBlock`/`AddSignatureBlock`)
  use `TextDocument` instead, not `ReadOnlyDocument`.
- Found and fixed two runtime bugs surfaced once `AvalonEdit.AddIn` actually loaded for the
  first time (previously it wasn't in the build, so nothing exercised these paths):
  1. `AutoDetectDisplayBinding.CreateContentForFile` threw an unhandled `InvalidOperationException`
     when no display binding claimed a file — now logs and falls back to a blank `SimpleViewContent`
     (`src/Main/SharpDevelop/Workbench/DisplayBinding/AutoDetectDisplayBinding.cs`).
  2. The MVP stub `CSharpLanguageBinding.FormattingStrategy` (`src/Main/Base/Project/Stubs/`)
     returned `null`, which crashed `CodeEditorAdapter.FileNameChanged`'s
     `OptionControlledIndentationStrategy` constructor (`ArgumentNullException`) the first time a
     `.cs` file's `CodeEditor` actually initialized. Now returns `DefaultFormattingStrategy.DefaultInstance`,
     matching what `DefaultLanguageBinding` already does.

## Phase 1: Make ParserService itself Roslyn-backed (DONE this session, option (a))

Registered a real C# `IParser` — `RoslynParser` (`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/Roslyn/`)
at `/SharpDevelop/Parser` (`supportedfilenamepattern="\.cs$"` in `AvalonEdit.AddIn.addin`) — so
`SD.ParserService.Resolve(...)`/`GetCompilationForFile(...)`/`Parse(...)` now return real data for
`.cs` files everywhere, not just Phase 0's 4 files.

Went with **option (a)** from the previous revision of this note (thin `ResolveResult`s backed by
Roslyn, not a wholesale rewrite of `ResolveResultMenuCommand` and its ~10 in-tree-MVP consumers —
`GoToDefinition.cs`, `DefinitionViewPad.cs`, `DeclaringTypeSubMenuBuilder.cs`,
`SymbolTypeAtCaretConditionEvaluator.cs`, `EditorRefactoringContext.cs`, `CodeEditorView.cs`,
`ClipboardRing.cs`; the RefactoringService/Dom.ClassBrowser files that also touch `ResolveResult`
are already `Compile Remove`d from the MVP build, so out of scope). Reasoning: those 10 files are
sound UI/command glue that was always correctly shaped around `ResolveResult`/`IEntity`/`IType` —
they just never got real data because nothing backed the interfaces. Rewriting all of them to talk
to Roslyn directly for no functional gain (they'd do the exact same thing, just via different
types) was judged disproportionate; **new** code (Phase 0's 4 files) still talks to
`Microsoft.CodeAnalysis` directly and should keep doing so.

New adapters (`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/Roslyn/RoslynEntityAdapters.cs`,
`RoslynCompilation.cs`, `RoslynUnresolvedFile.cs`): `RoslynTypeDefinitionAdapter` (`ITypeDefinition`
and `IType`), `RoslynUnresolvedTypeDefinitionAdapter` (`IUnresolvedTypeDefinition` — Roslyn has no
separate unresolved phase, so `.Resolve()` just returns the resolved adapter for the same symbol),
`RoslynMemberAdapter` (`IMethod`/`IField`/`IProperty`/`IEvent` in one class, since Roslyn's own
`ISymbol` hierarchy doesn't split them either), `RoslynAssemblyAdapter` (`IAssembly`),
`RoslynCompilation` (`ICompilation`) — all backed by real `Microsoft.CodeAnalysis` symbols/
`Compilation`. Per the "MVP mock" policy already established elsewhere in this codebase (see
`Mocks/MiscMocks.cs`'s `DefaultUnresolvedMethod` comment), **only members actually exercised by the
~10 in-tree consumers have real implementations** — the long tail (`GetSubstitution`,
`AcceptVisitor`, cross-compilation `ToReference`, etc.) throws `NotImplementedException`. Don't
grow this adapter for new features; extend it only when an existing consumer's code path needs a
member it doesn't have yet.

Known gaps: no `INamespace`/`IVariable` adapters yet, so `ResolveResult`s for namespaces and
locals/parameters degrade to `ErrorResolveResult` (`RoslynParser.ToResolveResult`) instead of
`NamespaceResolveResult`/`LocalResolveResult` — go-to-definition on a namespace or local variable
won't work until those are added, following the same pattern as the type/member adapters.

Verified: full `OpenDevelop.Mvp.slnx` builds with 0 errors; app boots and its DevFlow HTTP agent
responds (UI tree renders) with no new exceptions in the log.

## Phase 2: Code completion (DONE this session)

There was no MVP code completion for `.cs` at all before this (no add-in registered anything at
`/SharpDevelop/ViewContent/TextEditor/CodeCompletion` for `.cs` — `CSharpBinding`, the only one
that did, isn't in the MVP build). Added `RoslynCodeCompletionBinding`
(`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/Roslyn/RoslynCodeCompletionBinding.cs`),
registered in `AvalonEdit.AddIn.addin` (`CodeCompletionBinding id="C#-Roslyn" extensions=".cs"`).
Triggers on typing a letter/`_`/`.` or Ctrl+Space; uses
`Microsoft.CodeAnalysis.Completion.CompletionService.GetCompletionsAsync` against the *same*
`RoslynWorkspaceHelper` workspace Phase 1's parser uses (no second, disconnected workspace).
`RoslynCompletionItem.Complete` applies the change via `CompletionService.GetChangeAsync`, so
things like using-directive insertion on completing a type from another namespace work for free.

This surfaced a real correctness gap in `RoslynWorkspaceHelper` from Phase 0/1: `GetSolution()`
only read source files from disk, so completion while actively typing unsaved changes would've
seen stale content. Fixed with a `liveOverrides` dictionary (file path → live buffer text) that
`SyncProject` now prefers over `File.ReadAllText` when present; `FindDocument(path, liveText)`
sets/clears the override by diffing against on-disk content, and `RoslynParser.Parse`/
`RoslynWorkspaceHelper.GetSymbolAt(ITextEditor, ...)` were updated to pass the editor's live text
through. Still not incremental/event-driven (a full solution re-scan still happens per call), but
at least reflects what's actually in the buffer now.

Icon mapping for completion items uses `Microsoft.CodeAnalysis.Tags.WellKnownTags` → the same
`Icons.16x16.*` resource names `RoslynSymbolIcons` (Phase 0) already uses for context-action
popups.

## Phase 3: Delete the mock type system (partially done this session; mostly still blocked)

Correction to the previous revision of this note: **`src/Libraries/NRefactory/*` cannot be
physically removed** — it was flagged as "already empty/unused" but that was wrong. The 4
sub-projects there are legacy-format stub `.csproj` files (no real source, per the Phase 0
investigation), but ~40 *other*, not-yet-MVP-ported add-ins (`CSharpBinding`, `Debugger.Core`,
`XamlBinding`, `PackageManagement`, `UnitTesting`, `ResourceToolkit`, etc.) still hold real
`<ProjectReference>`s to these `.csproj` paths. Deleting them would break those add-ins' ability
to ever build again, for no MVP benefit — verified via `grep -rl NRefactory **/*.csproj` before
touching anything. Leave them alone; this is a wider-repo concern than "the MVP build," out of
scope here.

Done this session: **physically deleted** `src/Main/Base/Project/Src/Services/RefactoringService/`
(9 files: `FindReferenceService.cs`, `RefactoringService.cs`, `TypeGraphNode.cs`, etc.) — verified
via the same grep technique that nothing else references that path, unlike NRefactory. Removed the
now-pointless `<Compile Remove="Src\Services\RefactoringService\**\*.cs" />` line from
`ICSharpCode.SharpDevelop.csproj` along with it. Full `OpenDevelop.Mvp.slnx` still builds with 0
errors.

## Phase 1 "option (b)" (DONE this session): rewrote the deferred consumers against Roslyn directly

Went back and did the rewrite Phase 1 deferred. `Roslyn/*.cs` (the adapters, `RoslynParser`,
`RoslynWorkspaceHelper`, etc.) **moved from `AvalonEdit.AddIn` to `Base/Project`**
(`src/Main/Base/Project/Roslyn/`, namespace `ICSharpCode.SharpDevelop.Roslyn`) — the command
classes needing them (`GoToDefinition`, `DeclaringTypeSubMenuBuilder`,
`SymbolTypeAtCaretConditionEvaluator`, `DefinitionViewPad`) live in `Base/Project`, which
`AvalonEdit.AddIn` depends on, not the other way around. `Base/Project.csproj` now carries the
`Microsoft.CodeAnalysis.CSharp.Workspaces`/`.Features` package references (removed the now-redundant
copies from `AvalonEdit.AddIn.csproj`, which gets them transitively).

Rewritten to talk to `Microsoft.CodeAnalysis` (`ISymbol`/`INamedTypeSymbol`/`Location`) directly,
no more `ResolveResult`/`IEntity`/`IType`:
- `GoToDefinition.cs` — plain `AbstractMenuCommand` now (was `ResolveResultMenuCommand`); exposes
  `RunOn(ISymbol)` for `CodeEditorView`'s Ctrl+Click handler to call directly.
- `DeclaringTypeSubMenuBuilder.cs` — the live-editor-caret path is Roslyn-native; the
  `IMemberModel` path (bookmarks) is untouched, split into its own method.
- `SymbolTypeAtCaretConditionEvaluator.cs` — same split: Roslyn `ISymbol` for the live-caret/menu
  path, `IEntityModel`/bare `IEntity` still supported for the bookmark/`GotoDialog` path (that's a
  separate background-model subsystem, not part of this rewrite's scope).
- `EditorRefactoringContext.GetCurrentSymbolAsync()` — now returns `Task<Microsoft.CodeAnalysis.ISymbol>`
  (had zero callers in the MVP build, so this was a safe signature change).
- `DefinitionViewPad.cs` — resolves via `RoslynWorkspaceHelper.GetSymbolAtCaret`, navigates via
  `ISymbol.Locations`/`FileLinePositionSpan` instead of `ResolveResult.GetDefinitionRegion()`/`DomRegion`.
- `CodeEditorView.cs` — `ShowHelp()` and Ctrl+Click now resolve a Roslyn symbol directly instead of
  `SD.ParserService.Resolve(...)`; `HelpProvider.ShowHelp(string)` (already existed) replaces the
  `IEntity`-typed overload.
- `ClipboardRing.cs` — never actually used its resolved symbol; now a plain `AbstractMenuCommand`.
  `ClipboardRingAction.cs` had a dead unused `IEntity Entity` property, deleted.
- `QuickClassBrowser.cs` — the class/member combo-box browser, previously walking
  `IUnresolvedFile.TopLevelTypeDefinitions`/`IUnresolvedTypeDefinition.Resolve(...)`, now walks
  `INamedTypeSymbol`/`ISymbol` directly via the same `RoslynWorkspaceHelper`-backed `Document`;
  `Update(IUnresolvedFile)` → `Update(FileName)` (updated its one caller, `CodeEditor.cs`).
- `CodeSnippet.GetCurrentClass()` (used for the `${ClassName}` snippet variable) — now returns
  `INamedTypeSymbol` via `RoslynWorkspaceHelper.GetSymbolAtCaret`.
- `IconBarManager.cs`, `ParserFoldingStrategy.cs` — turned out to be fine as-is (they consume
  `IUnresolvedFile`/`IUnresolvedTypeDefinition`, which is Roslyn-backed since Phase 1 regardless of
  caller-side type; only removed a stray dead `using` / fixed a misleading doc comment).

**Deliberately not touched** (real, separate subsystems that happen to reference
`ICSharpCode.TypeSystem` types, not NRefactory-descended glue): `EntityBookmark`/`GotoDialog`/the
whole `Dom/*Model.cs` background project-content model, `AmbienceService`/`DefaultAmbience`,
`ClassBrowserIconServiceStub.cs`, `CompletionImage.cs`, `IProject`/CPS, `ParserService.cs` itself.
Rewriting those against Roslyn would be a different, much larger project than "finish the C#
semantic-analysis migration" — they're legitimate shell architecture, not something inherited from
NRefactory that needs replacing.

**Still blocked from full Phase 3 (deleting `ICSharpCode.TypeSystem.Abstractions` itself):** the
"deliberately not touched" list above still depends on its interfaces. That's a materially
different, much larger task (rearchitecting the bookmark/background-model/ambience/class-browser
subsystems), out of scope for this pass.

## Non-goals, revisited

Session update: two of the three items below turned out to be tractable within the existing
`RoslynWorkspaceHelper` scope and are now done; the third is a genuine architectural limitation
of OpenDevelop's project model, not something the Roslyn bridge itself can fix. Looked at
UnoDevelop's vendored MonoDevelop `MonoDevelopWorkspace` for how a full IDE does this - its
incremental-update and P2P-reference handling are the same shape as what's below, just built on
richer project-system events (`SolutionItemModifiedEventArgs`) than SD's `IProject` exposes.

- **Incremental workspace updates — DONE (bounded).** `RoslynWorkspaceHelper` now tracks a
  `dirtyProjects` set, marked via `project.Items.CollectionChanged`. `GetSolution()` only does the
  full Compile-item rescan (`SyncDocumentList`/`SyncReferences`) for a project whose item list
  actually changed since the last call; everything else takes the cheap `SyncOpenDocumentText`
  path, which only pushes already-known live (unsaved) buffer text into already-known documents.
  This isn't MonoDevelop's fully event-driven `Workspace` subclass (no per-document
  `OnDocumentTextChanged` push from the editor; the buffer diff still happens on each
  `GetSolution()` call) - but it does remove the "re-read and re-diff every file in every project
  on every keystroke-triggered completion/tooltip request" cost that made this a real non-goal.
- **Transitively-referenced project outputs — DONE.** `SyncReferences` now wires
  `ProjectReferenceProjectItem`s to real Roslyn `ProjectReference`s (compilation-to-compilation)
  instead of a flat DLL `MetadataReference` of the referenced project's build output, whenever the
  referenced project is itself a loaded `.csproj` with a live Roslyn `ProjectId`. Roslyn then
  resolves the transitive closure (P → Q → R) through Q's own `ProjectReferences` automatically,
  matching MonoDevelop's `AddReferences`/`ProjectReference` behavior. `GetMetadataReferences` skips
  any resolved-assembly-reference path that's covered by a `ProjectReference` instead, so a
  project doesn't get both a live compilation reference and a stale prebuilt-DLL reference to the
  same dependency. NuGet package assemblies and non-project references are unaffected - those
  still flow through `IProject.ResolveAssemblyReferences` as file-backed `MetadataReference`s, same
  as before.
- **Multi-target-framework / multi-TFM project support — still a non-goal, and not really fixable
  here.** MonoDevelop models this by minting one Roslyn `ProjectId` per (project, TFM) pair
  (`DotNetProject.TargetFrameworkMonikers` × `ProjectDataMap.GetOrCreateId`). OpenDevelop's
  `IProject`/`MSBuildBasedProject` has no equivalent concept - a loaded project is evaluated once,
  for a single (implicit) TargetFramework, full stop; `IProject.GetItemsOfType`/
  `ResolveAssemblyReferences` don't carry a TFM axis to iterate over. Making
  `RoslynWorkspaceHelper` multi-TFM-aware would first require `MSBuildBasedProject` itself to
  evaluate and expose multiple per-TFM item/reference sets, which is a project-system change, not
  a workspace-bridge change - out of scope for this doc.
