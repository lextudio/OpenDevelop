# Out-of-Process Roslyn Host — Design

Status: **partially implemented; not ready for default IDE use.** A real host accepts project
snapshots, serves document requests, and supports parent-owned replay after process death.
Phase 1 has a batch API, but consumers still have per-anchor paths. Remote mode now registers an
`ILanguageService` adapter, shared by C# and VB, rather than a second local Roslyn workspace.
The protocol includes semantic tokens, symbol classification/name, document status and rename
options; the application project prepares the host runtime. Explicit TFM queries now select their
own slices without changing the active framework. Project references are reconciled when later
projects arrive, and readiness includes transitive dependencies. Completion and snippet insertion
no longer synchronously wait for Roslyn. Full IDE interaction and lifecycle verification remain
outstanding. Code actions are recomputed on apply and have a real cross-process
replacement test. Passing isolated host tests is not the IDE migration
acceptance gate. See §8 for the required end-to-end gate.

## 1. The question

Should the Roslyn backend (and OpenLens with it) run in its own process, so that per-project state
can be serialized to disk and startup/shutdown get faster?

**Short answer.** Yes for Roslyn, and the boundary is far cheaper than it looks. No for OpenLens —
it is a WPF renderer bound to the editor's visual tree and cannot leave the UI process; only the
*data* its providers consume moves, and that move is where most of the win is.

But the honest framing of the payoff is different from the one in the question:

- **Out-of-process does not give persistence.** Persisting project state is a separate feature. It
  is possible today, in-process (§7).
- **What out-of-process gives is fault isolation and an enforceable architectural boundary.**
  Under the lifetime decision in §7b, the host exits with the IDE. Faster startup requires
  separately validated caching/persistence, not merely a process split.
- The dominant cost is not serialization. It is the ~15 call sites that block the UI thread on a
  language-service call today (§5.1). Those are a latent freeze in-process; across a process
  boundary they become a certain one.

The rest of this document treats the user's own framing as the design brief: **a Roslyn-specific
enhanced LSP**. That is the right shape, and §4 explains where "enhanced" is load-bearing.

## 2. What makes this cheap: the boundary already exists

`ILanguageService` (`src/Main/Base/Project/Src/LanguageServices/LanguageServiceContracts.cs:9`) has
**24 members and not one Roslyn type on it**. The contract file does not even `using
Microsoft.CodeAnalysis`. `DocumentId`, `TextSpan`, `CompletionItem`, `DiagnosticSeverity` are
OpenDevelop DTOs that deliberately shadow the Roslyn names.

This is the enabling fact. The layering rule in `language-services.md` ("the UI host should not
directly depend on Roslyn or LSP objects") has already been paid for. Every consumer listed in §5
already speaks DTOs.

Payload shapes, for a wire:

| Class | Members | Wire risk |
| --- | --- | --- |
| Scalars / small results | `GetSymbolNameAsync`, `IsValidIdentifierAsync`, `GetHelpKeywordAsync`, `GetContainingTypeNameAsync`, `GetQuickInfoAsync`, `GetSymbolKindAsync`, `UpsertDocumentAsync`, `RefreshProjectAsync`, `OnTextChanged` | none |
| Bounded flat lists | `GetCompletionsAsync`, `GetDiagnosticsAsync`, `GoToDefinitionAsync`, `FormatAsync`, `FindMemberAsync`, `GetCodeActionsAsync`, `GetSemanticTokensAsync` | size only |
| Recursive but all-DTO trees | `GetDocumentOutlineAsync`, `GetBaseSymbolsAsync`, `GetDerivedSymbolsAsync` | depth-aware serializer |
| Solution-wide maps | `RenameSymbolAsync`, `ExtractInterfaceAsync`, `ApplyCodeActionAsync` — `IReadOnlyDictionary<path, TextEdit[]>` | unbounded but plain |

Nothing here needs a new data model. That is unusual for a process-split proposal and it is the
main reason this is worth doing.

## 3. Precedent: the designers already do this

`src/Main/Designer/` is a working out-of-process host, and the new host should reuse it rather than
invent a second pattern. From `Designer.Remote/DesignerHostProcessClient.cs`:

- **Transport**: TCP on `IPAddress.Loopback` with an ephemeral port; the *child* dials back
  (`:92-96`). StreamJsonRpc with `HeaderDelimitedMessageHandler` + `SystemTextJsonFormatter`
  (`:133-135`).
- **Launch**: the dotnet muxer resolved from the current process (`:207`), `exec "<childDll>"
  --port <port> --token <token>` (`:60`). stdout/stderr pumps start *before* the TCP accept, because
  a child that banners before connecting otherwise deadlocks on a full pipe buffer (`:110-117`).
- **Handshake**: one `initialize` round trip carrying token + protocol version + session id
  (`:73-79`); mutual — the child proves identity with its PID, the parent authorizes with a 256-bit
  token. `DesignerProtocol.Version` is bumped on incompatible wire changes
  (`Designer.Remote/DesignerProtocol.cs:19-23`).
- **Death and recovery**: `HostExited` from `Process.Exited`, `SharedDesignerHostPool` with lease
  counting and a per-key `Generation`, and `RecoverableDesignerDocumentHostClient`, which reopens
  each document from **parent-owned source** after a restart.

Two of its rules matter directly here:

1. **No server-side handle ever crosses the wire.** Every designer request carries
   `(sessionId, documentId, baseVersion)` and the server returns state, never a handle. §5.2 shows
   where the language service violates this today.
2. **The parent owns the source of truth**, which is what makes restart recovery a replay rather
   than a resync. `UpsertDocumentAsync` maps onto this cleanly.

What the precedent does *not* solve is §5.1. The designer surface has exactly one
`.GetAwaiter().GetResult()` and it is on the child side (`DesignHost.cs:153`), never in the parent's
UI path.

## 4. Why "Roslyn-specific enhanced LSP" is the right shape

Plain LSP would cover roughly two thirds of the surface and quietly lose the rest. Use LSP's shape
and vocabulary where it fits, and be explicit — not apologetic — about the extensions.

**Maps onto standard LSP directly** (keep the LSP method names, so an LSP-literate reader can
navigate the protocol): `textDocument/didOpen|didChange`, `completion`, `hover`, `publishDiagnostics`,
`definition`, `references`, `formatting`/`rangeFormatting`, `documentSymbol`, `semanticTokens/full`,
`rename` + `prepareRename`, `codeAction`, `typeHierarchy/supertypes|subtypes`.

**Has no LSP equivalent, and these are the reason this is "enhanced":**

| Need | Why LSP does not cover it | Extension |
| --- | --- | --- |
| Multi-targeting | LSP has one view of a document; OpenDevelop keeps one Roslyn document per TFM slice (`_documentVariantsByTfm`) and has an active-TFM concept | `roslyn/targetFrameworks`, and a TFM discriminator on every document-scoped request |
| Project model ownership | LSP servers discover projects themselves; here **MSBuild evaluation lives in the IDE**, and the host must be *told* the project graph | `roslyn/project/load` carrying a `LanguageServiceProjectSnapshot`; `roslyn/solution/closed` |
| Extract Interface | not in LSP | `roslyn/extractInterface/info` + `/apply` |
| Find Member by name | not in LSP (it is not a position-based request) | `roslyn/findMember` |
| Help keyword (F1) | not in LSP | `roslyn/helpKeyword` |
| Containing type (snippets) | expressible via documentSymbol, but a whole-document round trip for one string | `roslyn/containingType` |
| OpenLens counts | not in LSP; and see §6 — the batched form is the whole point | `roslyn/lens/document` |
| Readiness | LSP has no "my index is not ready, ask again" state; a server simply answers | `roslyn/status` + a `readiness` field on results (§5.3) |

The multi-targeting and project-ownership rows are the two that make a stock LSP server the wrong
answer, not merely an incomplete one.

## 5. The five things that must be fixed first

Each of these is a real defect *today*, in-process. The process boundary does not create them; it
removes the slack that currently hides them. All five are worth fixing whether or not the host ever
ships — which is what makes this migration safe to start.

### 5.1 Blocking UI-thread call sites (the dominant risk)

These call `.GetAwaiter().GetResult()` on the UI thread:

| Site | When it runs |
| --- | --- |
| `Src/Internal/ConditionEvaluators/SymbolTypeAtCaretConditionEvaluator.cs:94-95` | **every context-menu build** — and it is *two* blocking calls: a full `UpsertDocumentAsync` of the whole document text, then `GetSymbolKindAsync` |
| `Src/Editor/Commands/FindReferencesCommand.cs:153` (`IsValidIdentifierAsync`) | **per keystroke**, in a modal rename dialog's validation |
| `Roslyn/RoslynCodeCompletionBinding.cs:43-44` | per completion request |
| `Src/Editor/Commands/GoToDefinition.cs:23-24`, `AvalonEdit.AddIn/Src/CodeEditorView.cs:464-465` | Ctrl+Click / F12 |
| `AvalonEdit.AddIn/Src/ContextActions/FindBaseClasses.cs:36-37`, `FindDerivedClassesOrOverrides.cs:36-37` | context action expansion |
| `Src/Editor/Commands/ReformatSelection.cs:58-59` | format command |
| `AvalonEdit.AddIn/Src/Snippets/CodeSnippet.cs:224-225`, `CodeEditorView.cs:259-260` | snippet expansion, F1 |

A cross-process round trip behind a context-menu build or a per-keystroke validator is a visible
freeze. **Prerequisite: none of these may block.**

**Snippet expansion and completion have different constraints.** Inspect the actual callers before
assuming that every synchronous return value requires a synchronous language query:

- `CodeSnippet.GetCurrentClassName` resolves `${ClassName}` while a snippet is being expanded, and
  the string it returns is inserted into the user's code. A cache miss falls through to
  `StringParser.GetValue`, so deferring it would silently insert the WRONG text - worse than a
  brief stall, because the user may not notice.
- `RoslynCodeCompletionBinding.ShowCompletion` is called by `HandleKeyPressed` **after** insertion.
  Its bool only stops trying other completion bindings; it does not swallow the inserted character.
  Completion can claim the request synchronously and display its result asynchronously, provided
  stale responses are discarded when the caret or document changes.

Completion now runs asynchronously, cancels superseded requests and verifies the document text,
file and caret before showing results. Snippet insertion resolves the containing type before
editing, then checks document, caret and selection before opening an undo group. Previews do not
query Roslyn. These paths still require real UI integration coverage for stale replies and undo.

### 5.2 Opaque tokens backed by live Roslyn objects

`CodeActionInfo.Id` (`LanguageServiceContracts.cs:180`) and `ExtractInterfaceMember.Id` (`:205`) are
tokens the UI hands back later. They are backed by `_pendingCodeActionsByDocument`
(`CSharpVBLanguageService.cs:63`) — a dictionary of **live Roslyn `CodeAction` objects**.

This violates the designer rule "no server-side handle on the wire". It forces sticky routing to one
host instance and turns a host restart into silent breakage (a stale id resolves to an empty edit
map — the UI would apply nothing and report success).

Options, in order of preference:

1. Make the id **content-addressed and revalidatable**: `(documentId, documentVersion, span,
   equivalenceKey)`. The host re-derives the action on apply; a version mismatch is an explicit
   `stale` result the UI can retry, not silence.
2. Keep opaque ids but add an explicit `generation` that the host rejects after a restart.

Option 1 is more work and is the one that matches the designer's `baseVersion` discipline.

### 5.3 Readiness is not modelled

This is the lesson of the OpenLens investigation recorded in `openlens.md`: a reference search run
while a document was still registered against the shared loose ad-hoc project returned a perfectly
valid **empty** result, which a caller then cached forever. "Not ready" and "genuinely zero" were
indistinguishable.

In-process this is a latent bug. Across a process boundary it becomes routine, because the host
starts cold and answers before it is warm. The protocol must therefore carry readiness:

- `roslyn/status` → per-project state (`unloaded | evaluating | loading | ready`).
- Every result carries the `readiness` of the projects it was derived from.
- A caller that caches (OpenLens) refuses to cache a result whose readiness is below `ready`.

Do this even if the host never ships. It converts a class of silent wrong answers into an explicit
state.

### 5.4 `TryGetProjectDocument` leaks a live Roslyn `Document`

`CSharpVBLanguageService.cs:1140` returns `Microsoft.CodeAnalysis.Document`, and
`OpenDevelopDevFlowActions.cs:970` walks from it into `Project.GetCompilationAsync()` in-proc. This
cannot cross a process boundary at all.

It is diagnostics-only. Replace it with DTO-shaped RPCs — `roslyn/document/status` returning the
fields those diagnostics actually read (owning project, sibling documents, metadata-reference count,
diagnostic sample). `LanguageWorkspaceStatus` (`:1818`) and `ContainsDocument`/`GetTargetFrameworks`/
`GetActiveTargetFramework` (`:158/:173/:187`) are already DTO-shaped and just need to join the RPC
surface.

`Roslyn/RoslynWorkspaceHelper.cs` is a parallel static Roslyn API taking `ISymbol`/`Solution`
(`:424`, `:452`, `:554`). It moves wholesale into the host; the transitional callers named in
`language-services.md` must migrate to `ILanguageService` first. That migration is already the
stated target — the host just makes the deadline real.

### 5.5 Chatty and unbatched calls

- `GetSemanticTokensAsync` is whole-document and runs from the colorizer per render
  (`LanguageServiceSemanticColorizer.cs:60`).
- `LanguageOpenLensAnchorProvider.cs:44` calls `GetDocumentOutlineAsync` **per discovery pass,
  uncached by design** (its own comment at `:18` says anchor discovery is "cheap").
- `LanguageOpenLensProvider` then issues **one `FindReferencesAsync` per anchor** (`:184`) — eight
  solution-wide symbol searches for the eight-anchor fixture in `OpenLensFixture.cs`.

"Cheap" was true for an in-process call. It is not true for a round trip. §6 addresses the OpenLens
case; semantic tokens need delta support (`semanticTokens/full/delta`) before the colorizer can live
across a boundary.

## 6. OpenLens does not move — its data source does

OpenLens is `OpenLensRenderer`, an `IVisualLineBlockAdornmentGenerator` that builds WPF `TextBlock`
rows into the editor's visual tree, keyed to `textView.VisualLines`. It cannot leave the UI process.
The question "should OpenLens run in the host too?" resolves to: **which half of OpenLens?**

- **Renderer, cache, cadence, invalidation** — stays in the UI process. Unchanged.
- **`LanguageOpenLensAnchorProvider` + `LanguageOpenLensProvider`** — these are pure
  language-service consumers. They should collapse into **one batched host call**:

```
roslyn/lens/document(documentId, tfm)
  -> { readiness, anchors: [ { anchorId, range, symbolKey, overridability,
                               references: int, implementations: int, overrides: int } ] }
```

Today that is 1 outline call + N reference searches + M hierarchy searches, each a separate round
trip and each independently subject to the readiness problem. Batched, it is one call the host can
answer from a single compilation, with one readiness value for the whole set.

This is the largest single performance win in the proposal, and — because it removes the per-anchor
resolve path entirely — it also removes the cadence problem class documented in `openlens.md` §13.2,
where individual resolutions raced the workspace and cached wrong counts.

Note the ordering: **the batched API is worth building in-process first.** It is a strict improvement
today and it is the interface the host would expose anyway.

## 7. Persistence and startup: what actually buys what

Separating the two claims in the original question:

**Serializing project state to disk does not require a separate process.** It already happens:
`LanguageServiceProjectSnapshot` writes evaluated project state (documents, references,
preprocessor symbols, language version, nullable context, analyzers) to
`<solution>/.od/roslyn-tfm-cache/<hash>.json`, keyed by the project file's write time plus a
project-tree scan (`LanguageServiceProjectSnapshot.cs:174, 351, 437`). This is the expensive part —
MSBuild evaluation — and it is already cached.

That cache is also a warning about the persistence idea generally. This session found an entry
holding `References: []` with 5 documents: a snapshot written when nothing resolved, then trusted
forever, because the cache key (write times) never changed afterwards. Every symbol query over that
project answered "found nothing" rather than failing. A guard now rejects a zero-reference entry as
impossible, but the lesson generalises: **persisted semantic state needs a validity model, not just
a freshness key.** More persistence without that is more of this failure mode.

What Roslyn state beyond project evaluation is worth persisting is a real question with a known
answer shape (Roslyn itself has a SQLite-backed persistent store). That remains a substantial
feature in its own right, rather than an accidental consequence of a process split.

**What the process split actually buys:**

| Claim | Verdict |
| --- | --- |
| Faster shutdown | **Yes, directly.** The IDE exits without tearing down a workspace. |
| Faster startup | **Via persisted state, not host lifetime — see §7b.** A host started with the IDE saves nothing by itself; a host that rehydrates an index from `.od/` is warm on first request. |
| Fault isolation | **Yes.** An analyzer that OOMs or a Roslyn crash stops taking the IDE with it. The designers already prove the recovery machinery works. |
| Architectural enforcement | **Yes, and undervalued.** A process boundary makes the `language-services.md` layering rule mechanical instead of aspirational. §5.4 exists precisely because in-process nothing stopped a leak. |
| Memory headroom | **Yes.** The workspace stops competing with the UI process's address space. |
| Serialization | **No.** Orthogonal, and available today. |

## 7b. Host lifetime and persistence

**Decision: one host process per solution, living and dying with the IDE. Warm start comes from
state persisted under `.od/`, not from a process that outlives the window.**

### Why this beats a long-lived daemon

§8's Phase 4 originally proposed decoupling host lifetime from the IDE window, because that is the
only way a *process* can be warm on the next launch. That reintroduces exactly the cost §9 warns
about: another thing that survives a crash and poisons the next run. This session produced a
first-hand example — a `pkill` pattern that never matched (the executable is `OpenDevelop`, not
`SharpDevelop.dll`) left instances accumulating for hours, and because the DevFlow agent is pinned
to one port, a survivor silently captures the next run's traffic.

Persisting state dissolves the problem:

> **Persistence substitutes for process longevity.** To be warm on the next launch, the *state* has
> to outlive the process, not the process itself.

One host per solution, tied to the IDE's lifetime, then needs no orphan reaping, no cross-solution
isolation, and no daemon lifecycle. `.vs/<solution>/v17/` is the same bargain.

### What is actually persistable

Not the workspace. A Roslyn `Compilation` is an object graph holding metadata readers and has never
been serializable, and syntax trees are cheap to re-parse. The durable, expensive-to-rebuild artefact
is the **index**: which files declare and reference which symbol names, plus per-document checksums.

Roslyn's source has a SQLite-backed store, but its relevant service and checksummed-storage
interfaces are internal implementation details, not a supported public API a standalone host can
instantiate. We must therefore not couple OpenDevelop to Roslyn internals or describe this as
"adoption". The supported implementation is an OpenDevelop-owned, content-addressed index whose
entries are independently versioned and disposable.

That bounds the claim honestly:

| Operation | Cold-start cost with a persisted index |
| --- | --- |
| Candidate declaration lookup | token-index-backed; semantic confirmation still builds the candidate project's compilation |
| Solution-wide find references, go to definition | not yet index-backed; correctness still requires the current semantic model |
| Completion, diagnostics, code actions | still needs a compilation **for that project** — but not for the whole solution |

The first implemented slice is deliberately narrow: `PersistentSyntaxIndex` stores only identifier
tokens, keyed by source checksum, language/parse options (including TFM symbols) and compiler
version. `FindMember` uses it only to skip projects that cannot declare the requested type; it
never treats an index hit or miss as a semantic answer. Entries use a digest, a unique temporary
file and atomic replace; malformed, incompatible or inaccessible files rebuild conservatively.
Real-host tests cover a process replacement, broken JSON and a source change with its timestamp
restored. Before broadening this index, measure cold/warm latency and add concurrent-writer and
semantic-equivalence coverage.

### Store: `.od/`

The directory already exists and is already used for exactly this class of data
(`.od/roslyn-tfm-cache/<hash>.json`), is listed in `.gitignore:40`, and has no tracked files. It is
the right home, and `.vs` is the precedent.

Two rules it must follow, both learned the hard way in this repo:

1. **Key by checksum, never by timestamp.** The existing TFM cache keys on the project file's write
   time plus a project-tree scan. That is what let a snapshot holding `References: []` be trusted
   forever: the key never changed again, so the empty answer stuck. Branch switching breaks
   timestamp keys outright (a checkout can move a file's mtime *backwards*). Roslyn's own storage
   content identity and parse configuration are the durable key; copy that principle.
2. **Two IDE windows can open one solution.** Two hosts, one `.od/`. Immutable checksum entries
   make concurrent writers benign: each writer uses its own temporary name then atomically replaces
   the same complete payload. Readers validate the digest and rebuild on any partial/corrupt file.

### Hard prerequisite: RAR first

Persisting an index built from today's evaluations would make current wrongness durable. §7a's
measurement chain — RAR unimplemented → 3 metadata references → every xunit type `CS0246` →
`References: []` written to `.od/` — is what that looks like already, at the evaluation layer.

So the ordering in §7a is not a priority call, it is a **precondition**:

> Implement `ResolveAssemblyReferences` out of process **before** any semantic index or cached
> semantic answer is persisted. The current token-only candidate filter does not persist semantic
> answers and remains conservative when project evaluation is incomplete.

The zero-reference guard added to the TFM cache loader stays useful as a tripwire, but a tripwire is
not an invalidation strategy.

## 7a. Should MSBuild move too?

**Yes — and half of it already has, for a load-bearing reason.** But it is a bigger move than
Roslyn, it should not ride along in the same process, and one piece of it is worth doing
immediately, independent of any host.

### What is already out of process

Builds are. `msbuild.md` records why: the first implementation used in-process
`Microsoft.Build.Execution.BuildManager` and failed instantly with

```
MSB4062: The "AllowEmptyTelemetry" task could not be loaded ... Could not load type
'Microsoft.Build.Framework.IMultiThreadableTask'
```

because the SDK hosting the process ships tasks needing a newer `Microsoft.Build.Framework` than
the copy already loaded in-proc, and a hosted `BuildManager` resolves SDKs and tasks against the
*current process's* runtime location — nothing at the `ProjectCollection` call site can override
that. The fix was a real `dotnet build` child process. The technote's own conclusion is the
important part: *this is exactly why the original SharpDevelop authors used a separate worker
process, and it was load-bearing, not an arbitrary design choice.*

Note what this implies for the host design: **plain evaluation never runs a task**, which is why
Solution Explorer's in-process `Microsoft.Build.Evaluation.Project` usage has always worked. The
conflict only appears when a task actually executes. Evaluation and task execution have different
constraints and should not be assumed to share a process.

### The missing half is RAR, and its absence is corrupting Roslyn's results today

`MinimalMSBuildEngine.ResolveAssemblyReferences`
(`src/Main/SharpDevelop/Project/Build/MinimalMSBuildEngine.cs:75`) returns only the
`additionalReferences` it was handed. Real resolution is unimplemented, and the file's own header
says it "would need the same out-of-process approach as `BuildAsync`".

The consequence chain, measured while investigating an unrelated OpenLens failure:

1. RAR returns nothing.
2. `RoslynWorkspaceHelper.GetMetadataReferences` falls back to the host runtime's trusted platform
   assemblies (documented at `MinimalMSBuildEngine.cs:26-27`).
3. `SampleTestProject` therefore compiles with **3 metadata references**.
4. Every xunit type in it is `CS0246`.
5. `.od/roslyn-tfm-cache` persists a snapshot with `References: []`.

**This corrects an earlier conclusion in §7.** That cache entry was called "poisoned" and a guard was
added rejecting any zero-reference entry as impossible. The guard is defensible as a tripwire, but
it treats a symptom: the cache faithfully recorded a genuinely empty resolution. The defect is
upstream, in RAR.

So the single highest-value item this analysis found is not the host at all:

> **Implement `ResolveAssemblyReferences` out of process, the same way `BuildAsync` already is.**

It fixes wrong semantic results *today*, needs no protocol, no new process model, and no part of
this proposal. It should be done first regardless of whether the host is ever built.

### Why evaluation is a bigger move than Roslyn

The property that makes the Roslyn move cheap — §2's "24 members, not one Roslyn type on the
interface" — **does not hold for the project model**:

```csharp
// src/Main/Base/Project/Project/ISolution.cs:44
Microsoft.Build.Evaluation.ProjectCollection MSBuildProjectCollection { get; }
```

An MSBuild type, on OpenDevelop's own core interface. And the consumer set is far wider than
Roslyn's: Solution Explorer, the build system, the designers (which need references and TFMs),
PackageManagement (which *writes* project files — the NuGet install path mutates the csproj), and
the AddIn system.

**Counted properly, though, the coupling is far smaller than the ~51 references a plain grep
suggests, and that changes the verdict on this step.** Most of those hits are tests, `Fake*`/`Mock*`
doubles, and PackageManagement's `IGlobalMSBuildProjectCollection` — a *different* collection, used
for NuGet install scripts, that only shares the name. The production surface is:

| Site | Count |
|---|---|
| `ISolution.MSBuildProjectCollection` (the declaration) | 1 |
| `Solution.cs` (the implementation) | 1 |
| `MSBuildBasedProject.cs` | ~11 |
| `MSBuildEngine.cs` | 1 |

and `MSBuildBasedProject.MSBuildProjectCollection` is `internal`, forwarding to the parent solution.
Its uses collapse into just three operations:

- `ProjectRootElement.Open/Create(..., collection)` — 7
- `MSBuildInternals.LoadProject/UnloadProject(collection, ...)` — 3
- the forwarding property itself — 1

So the migration is not "51 call sites"; it is one interface member plus three operations behind a
single `internal` property. An abstraction covering those three - created and owned wherever
evaluation lives - removes `Microsoft.Build.Evaluation` from `ISolution` without touching any
consumer that merely reads project data.

Moving evaluation therefore requires either a projected read-model in the IDE fed by the host, or
an RPC per property access. Only the first is viable. The good news is the shape already exists in
miniature: `LanguageServiceProjectSnapshot` **is** a DTO of evaluated project state, already
serialized to disk. The question is not what the DTO looks like, it is who produces it and who
else has to stop touching `ProjectCollection` first.

### Three tiers, not two

Putting MSBuild evaluation *inside* the Roslyn host would recreate the MSB4062 conflict class the
build worker exists to avoid: one process holding both `Microsoft.CodeAnalysis.*` and
`Microsoft.Build.*` plus whatever task assemblies a project drags in (this repo already fights
GitVersion's task host and `MrtCore.PriGen.targets`). Roslyn's own `MSBuildWorkspace` reaches the
same conclusion and spawns a separate build host rather than loading MSBuild in-proc.

The target shape is therefore:

```
IDE process            Roslyn host              MSBuild host
- UI, editors          - workspace, analyzers   - evaluation
- project read-model   - symbol queries         - RAR
- project file writes  - lens batch API         - builds (exists today)
```

with the project read-model in the IDE fed by snapshots, and the Roslyn host receiving the same
snapshots rather than evaluating anything itself. That keeps §4's "the IDE owns the project graph"
rule true — it just moves who computed it.

### Sequencing

Independent of the phases in §8, and orderable on its own:

1. **RAR out of process.** Fixes today's wrong references. No new architecture.
2. **Stop leaking `ProjectCollection`.** Give `ISolution` a DTO-shaped surface, the way
   `ILanguageService` already has one. This is the prerequisite that makes everything after it
   possible.
3. **Move evaluation into the build host** that already exists, and have it produce
   `LanguageServiceProjectSnapshot` for both the IDE's read-model and the Roslyn host.

Step 1 is worth doing this week (and is now done — see §7a).

Step 2 was sized here as "comparable to the whole Roslyn host proposal" on the strength of a raw
grep. That was wrong: counting only production code, it is one interface member, one implementation
and three operations behind an `internal` property (§7a). It is a contained, mechanical change and
should be reordered accordingly - it is the cheap prerequisite, not the expensive one. Step 3
remains the large piece.

## 8. Migration plan

Every phase is independently valuable and independently verifiable. If the project stops after any
phase, the codebase is better than before — that is the criterion the phases are ordered by.

**Phase 0 — remove the blockers (no host yet).**
1. De-block the two indefensible UI-thread call sites (context-menu condition evaluator, per-keystroke
   identifier validation), then the rest of §5.1.
2. Model readiness (§5.3) in `ILanguageService` and make OpenLens refuse to cache a not-ready result.
3. Replace `TryGetProjectDocument` with DTO diagnostics (§5.4).
4. Content-address the code-action and extract-interface ids (§5.2).

Verifiable by the existing integration suite, with no new infrastructure.

**Phase 1 — batch the OpenLens API in-process (§6).** One call per document. Strict win today.

**Phase 2 — introduce the protocol as an in-process implementation.** Define the LSP-shaped surface
of §4 and implement it as a thin adapter over `CSharpVBLanguageService`, still in-process. Route
every consumer through it. This is where the design is proven — a wire-shaped API with no wire.

**Phase 3 — move it out.** Reuse `Designer.Remote` for transport, handshake, pooling and recovery.
The host owns the state listed in `CSharpVBLanguageService.cs:33-73`; there is no static cache to
untangle (all statics in that folder are immutable helpers). The IDE keeps owning MSBuild evaluation
and pushes `LanguageServiceProjectSnapshot` over `roslyn/project/load`.

**Phase 3a — host-agnostic libraries, implemented.** `src/Main/LanguageServices` now contains:

- `LanguageServices.Contracts`: DTOs, snapshot data and protocol declarations; plain `net10.0`,
  no Roslyn, IDE, MSBuild or windowing references.
- `LanguageServices.Roslyn`: the workspace implementation, resource-reference helpers and local
  protocol adapter/dispatcher; plain `net10.0`. Local IDE use injects a snapshot-provider callback.
- `Roslyn.Host`: process bootstrap/RPC wiring referencing these libraries. No linked source,
  duplicated snapshot DTO, fake IDE logging service or `ROSLYN_HOST` conditional compilation.

Project evaluation remains in the IDE's `LanguageServiceProjectSnapshotFactory`. Lifecycle
notifications carry paths rather than IDE project objects. Base forwards the moved public types
for existing add-in type references. `ResourceFiles` uses the standard .NET SDK so it no longer
pulls LibreWPF transitively into the host. Architecture tests inspect both assembly references
and the deployed host's dependency manifest; a direct-reference check alone missed that SDK leak.

**Phase 4 — persisted warm start, with IDE-owned host lifetime (§7b).** Do not introduce a daemon.
First verify which storage APIs are usable by this standalone host and measure a cold-start
baseline. Persist only checksum-validated state, with concurrent-instance isolation and recovery
from corrupt or incompatible caches. Verify identical semantic results for cold and warm starts;
do not assume that a persisted index eliminates compilation for semantic reference searches.

### IDE migration acceptance gate

- Remote mode creates no local Roslyn workspace. All language consumers, including completion,
  semantic coloring, navigation, diagnostics, refactorings and OpenLens, use the remote service.
- The application project build/deployment prepares the host and its runtime dependencies;
  tests must not succeed by finding a host left in another project's output directory.
- Project references and TFM selection work across files/projects, including unsaved editor text.
- Killing the host and then querying recovers the parent-owned state. Ordinary business errors
  do not restart a healthy host. Closing/switching solutions cannot resurrect old state or leave
  a child process behind.
- Actual IDE interaction tests and the full relevant suite pass, with no synchronous UI RPC
  waits. Isolated transport tests alone do not satisfy this gate.

### Verification checkpoint (2026-09-08)

- Follow-up provider regression run: `LanguageOpenLensProvidersTests`, 9 passed, no failures
  or skips. Batch lookup distinguishes service identity, explicit TFM, declaration start position
  (not just line), and the language service's monotonic workspace revision. The revision advances
  for IDE-side remote document updates, project loads and solution closes, and for the equivalent
  local Roslyn mutations, so an edit to a reference file cannot reuse a cached batch for an
  unchanged declaration file. The OpenLens renderer additionally broadcasts a provider-wide
  refresh on a source edit, dropping already-resolved rows in every open document and lazily
  repopulating only visible rows; without that renderer step, a cache-key change could not update
  a row that had already been published. Non-ready batches are not retained across retries.
- `PersistentSyntaxIndexTests`, 2 passed, no failures or skips. It exercises four simultaneous
  real host processes writing one immutable checksum entry, then verifies a fifth host reads that
  entry without rebuilding; it also covers process replacement, corrupt JSON and same-timestamp
  source changes.
- With `OD_ROSLYN_HOST=1`, `AddInTests.OpenLens_RendersEachLensAboveItsDeclarationLine` passed
  against the real application (latest 1 total, 1 passed, 0 failed/skipped; 40.727 s). This covers
  deployed-host startup, remote C# registration, project loading, OpenLens value publication and
  its rendered declaration-line placement. It is a focused IDE gate, not a replacement for the
  remaining full interaction suite.
- Also with `OD_ROSLYN_HOST=1`, focused real-application C# tests passed for cross-file reference
  search (`FindReferences_FindsDeclarationAndCrossFileUsage`, 17.320 s), multi-file rename
  (`RenameSymbol_UpdatesDeclarationAndCrossFileUsage`, 13.520 s), and extract-interface file and
  class edits (`ExtractInterface_GeneratesInterfaceAndAddsToClassWithoutTouchingDisk`, 15.352 s).
  These validate project graph loading and remote recomputation/application paths, but do not
  cover the legacy parser consumers that still require live Roslyn objects.
- `SwitchingSolutions_DropsOldLanguageWorkspaceState` also passed with the remote host (1/1,
  38.285 s): it opens a C# document, switches the real application to a VB solution, verifies the
  replacement document becomes Ready, and proves the old C# path is no longer tracked. This closes
  the old-state-resurrection case for ordinary solution switching.
- The same remote-mode lifecycle test checks `od.roslyn-legacy-workspace.status`: after C# loading
  it reports `remoteHostMode=true` and `workspaceCreated=false` (1/1, 16.480 s).
  `RoslynWorkspaceHelper` therefore cannot silently create a second `AdhocWorkspace`; the child
  owns the sole Roslyn workspace. Old live-symbol parser consumers receive no local fallback in
  remote mode, so their richer DTO migration remains required before making the mode default.
- After that guard was enabled, the real remote `GoToDefinition_FromCrossFileUsage_FindsClass`
  integration test still passed (1/1, 28.824 s), proving cross-file navigation obtains its answer
  from the host rather than accidentally relying on the legacy parser workspace.
- `QuickClassBrowser_RendersForLanguageServiceOutline` passed in remote mode (1/1, 14.480 s).
  Its creation gate now recognises a registered language service even though the compatibility
  parser has no live unresolved types; the UI obtains class/member navigation from the host's
  asynchronous document-outline DTO instead.
- The editor icon bar now likewise derives type/member declaration markers from the asynchronous
  outline DTO, with a request generation guard so a delayed response cannot overwrite bookmarks
  after a file switch. `IconBar_RendersLanguageServiceDeclarationBookmarks` passed in remote mode
  (1/1, 34.173 s): it observes the live `IconBarManager` consumed by the visual gutter and finds
  the `Widget` type and `Name` property `OutlineBookmark`s. `AvalonEdit.AddIn` also builds
  successfully after this migration.
- `WorkbenchTests.SolutionExplorerFixture_TreeFileAndProjectBrowserChecks` was re-run after a
  contaminated full-suite attempt and passed in remote mode (1/1, 15.480 s). In particular, its
  `Program.cs` namespace/type/method folding assertion proves `ParserFoldingStrategy` obtains
  full-declaration `ExtentSpan`s from the host outline; it does not need a compatibility parser
  workspace. The preceding full run did **not** satisfy the acceptance gate: it stalled while the
  UnitTesting MTP discovery repeatedly called `PopulateTree`, then was deliberately terminated;
  its folding failure is therefore treated as timing evidence, not as a functional regression.
- The same interrupted 187-test run reported 3 failures and 27 expected platform skips in
  1044.213 s. All three failures pass independently in a fresh remote-host application: the
  folding check above; `DebugStart_WhenTargetMissing_FailsCleanlyInsteadOfHanging` (1/1, 33.379 s),
  whose first failure was a stale `.movedfortest` artifact left by termination; and the runtime
  upgrade workflow (1/1, 43.274 s), whose first failure was the deliberately terminated app's
  closed DevFlow connection. This narrows the remaining full-suite blocker to the MTP discovery
  busy loop, but does not turn the isolated passes into full-suite acceptance.

- Application, C# binding and VB binding builds succeeded. The application output contains the
  host DLL, deps file and runtime configuration under `RoslynHost/`, prepared by its project.
- The real-host service test exercises registry identity, project-backed readiness, cross-file
  definition, symbol name/kind, semantic tokens, identifier validation, OpenLens, rename comment
  options, unsaved incremental text, and clearing parent-side status on solution close.
- `dotnet run --project tests/OpenDevelop.Base.Tests/OpenDevelop.Base.Tests.csproj -- -parallel none`:
  120 total, 120 passed, 0 failed, 0 skipped. The SDK Web API template test now selects the
  net10 template identity rather than incorrectly treating a short name as globally unique across
  simultaneously installed SDK major versions.
- This is not an IDE integration-suite pass. Explicit TFM queries, forward project references,
  remote diagnostic samples and snippet containing-type lookup now have real-host tests.
  Portable library extraction has architecture tests. Persisted warm start and full UI/lifecycle
  validation (including asynchronous snippet insertion and undo) remain open.
- Recovery transport now serializes host creation/replay and acknowledged state mutations, while
  allowing independent read-only RPCs to share an already-ready host. Lifecycle tests verify a
  stalled query does not block a second query and a failed document update releases the state gate
  for recovery. This is transport concurrency only; host-side semantic work and UI scheduling still
  need their own latency measurements.

## 9. What would make me stop

Recording these now, so the decision to abandon is as available as the decision to continue:

- **If Phase 0 cannot be completed.** A blocking UI-thread call across a process boundary is not a
  performance regression, it is a hang. There is no version of this that survives skipping Phase 0.
- **If Phase 2 shows the DTO surface is not actually sufficient** — i.e. some consumer turns out to
  need a live Roslyn object in a way §5.4 did not anticipate. Phase 2 exists to find that out before
  any process is spawned.
- **If measured round-trip cost dominates.** Completion and semantic tokens are the ones to measure.
  Phase 2 makes them measurable without the boundary; the boundary cost can be estimated from the
  designers' existing StreamJsonRpc traffic.

## 10. Open questions

- ~~One host per solution, or one per machine?~~ **Resolved — see §7b.** One host per solution,
  living and dying with the IDE, with warm start coming from persisted state rather than from a
  long-lived process.
- **Does the F#/LSP path converge on the same protocol?** `LspLanguageService` already speaks LSP. If
  the Roslyn host speaks LSP-plus-extensions, `LanguageServiceRegistry` could resolve both through
  one client. Attractive, and out of scope until Phase 2 exists.
- **Analyzers.** `DirectAnalyzerAssemblyLoader` (`CSharpVBLanguageService.cs:1377`) loads analyzer
  assemblies into the current process. In the host this becomes a *benefit* (isolation from
  third-party analyzer crashes), but the loader and its assembly-resolution behaviour need review
  before they run somewhere with a different base directory.
