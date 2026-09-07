# Out-of-Process Roslyn Host — Design

Status: **proposal**. Nothing described here is implemented.

## 1. The question

Should the Roslyn backend (and OpenLens with it) run in its own process, so that per-project state
can be serialized to disk and startup/shutdown get faster?

**Short answer.** Yes for Roslyn, and the boundary is far cheaper than it looks. No for OpenLens —
it is a WPF renderer bound to the editor's visual tree and cannot leave the UI process; only the
*data* its providers consume moves, and that move is where most of the win is.

But the honest framing of the payoff is different from the one in the question:

- **Out-of-process does not give persistence.** Persisting project state is a separate feature. It
  is possible today, in-process (§7).
- **What out-of-process gives is a lifetime that is independent of the IDE window**, plus fault
  isolation and a hard architectural boundary. Fast startup follows from the *lifetime*, not from
  the process split as such.
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
freeze. **Prerequisite: none of these may block.** The two at the top are also the two that are
indefensible in-process, so fix them first and independently.

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
answer shape (Roslyn's own persistent storage keeps a symbol index in SQLite). That is a
substantial feature on its own and should not be smuggled in as a side effect of a process split.

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

Roslyn already ships this (`IPersistentStorageService`, SQLite-backed) and Visual Studio stores it in
`.vs/<solution>/v17/`. **This is adoption, not invention.**

That bounds the claim honestly:

| Operation | Cold-start cost with a persisted index |
| --- | --- |
| Solution-wide find references, go to definition, symbol search | index-backed — fast without building any compilation |
| Completion, diagnostics, code actions | still needs a compilation **for that project** — but not for the whole solution |

Shutdown is fast for a different reason: SQLite writes incrementally while idle, so exit has nothing
to flush.

### Store: `.od/`

The directory already exists and is already used for exactly this class of data
(`.od/roslyn-tfm-cache/<hash>.json`), is listed in `.gitignore:40`, and has no tracked files. It is
the right home, and `.vs` is the precedent.

Two rules it must follow, both learned the hard way in this repo:

1. **Key by checksum, never by timestamp.** The existing TFM cache keys on the project file's write
   time plus a project-tree scan. That is what let a snapshot holding `References: []` be trusted
   forever: the key never changed again, so the empty answer stuck. Branch switching breaks
   timestamp keys outright (a checkout can move a file's mtime *backwards*). Roslyn's own storage
   keys on checksums; copy that.
2. **Two IDE windows can open one solution.** Two hosts, one `.od/`. Needs either a lock file or
   per-instance subdirectories, or they overwrite each other's index. `.vs` uses the latter.

### Hard prerequisite: RAR first

Persisting an index built from today's evaluations would make current wrongness durable. §7a's
measurement chain — RAR unimplemented → 3 metadata references → every xunit type `CS0246` →
`References: []` written to `.od/` — is what that looks like already, at the evaluation layer.

So the ordering in §7a is not a priority call, it is a **precondition**:

> Implement `ResolveAssemblyReferences` out of process **before** any semantic index is persisted.

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

An MSBuild type, on OpenDevelop's own core interface, with ~51 references across the tree. And the
consumer set is far wider than Roslyn's: Solution Explorer, the build system, the designers (which
need references and TFMs), PackageManagement (which *writes* project files — the NuGet install path
mutates the csproj), and the AddIn system.

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
   possible, and the one with ~51 call sites to migrate.
3. **Move evaluation into the build host** that already exists, and have it produce
   `LanguageServiceProjectSnapshot` for both the IDE's read-model and the Roslyn host.

Step 2 is the real cost and it is worth being honest that it is comparable in size to the whole
Roslyn host proposal. Step 1 is worth doing this week.

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

**Phase 4 — decouple host lifetime from the IDE window**, which is the only thing that makes startup
faster. Requires Phase 0's readiness model to be honest about a warm-but-stale host.

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
