# Edit and Continue (EnC) / code-delta Hot Reload for OpenDevelop and SharpDbg

Status (2026-11-22): E0, E1 and E3a are committed and published. OpenDevelop pins `WpfHotReload.Runtime 0.1.0-ci.2`
(lextudio/WpfHotReload.Runtime run 45065130341, commit `be2ba81`) and SharpDbg `1.0.0-lextudio.45065133674.1`
(lextudio/sharpdbg run 45065133674, `main` at `158e240`):

| Repository | Commits | What |
|---|---|---|
| OpenDevelop | `59366a11e4` E0, `bc6b3839bd` E1+E3a client, `70d3f22794` updated method tokens, `08d12ee36d` host global usings/analyzers/AdditionalFiles, `e498565ea8` delta generations, XAML text, debugger applier, classification, apphost baseline, `504182e9fd` XSG end-to-end tests and docs | producer, agent pipe payload, `DapSession.ApplyDeltasAsync` |
| WpfHotReload.Runtime (pushed) | `6f2be2d` `applyDelta`, `502377a` handler callbacks, `9f5f214` startup guard, `be2ba81` restore-on-failure, `runUpdateHandlers`, property query | Tier 1 applier in the agent |
| sharpdbg (pushed to `main`) | `e0407f8` E2, `e22b991` `sharpdbg/applyDeltas`, `1d01991` attach opt-in, `739eaac` PDB delta symbol fidelity (E3b part), `158e240` apply while running | Tier 2 applier |
| openavalon/LibreWPF (pushed) | `f5df03025` EnC-safe generator, `b66539625` x:Name Name/NameScope; openavalon `d97e51a` pointer, `71068ac` publishes the generator | XAML source generator, published as `LibreWPF.Xaml.SourceGenerator 0.1.0-preview.69.60.1` |

With the old `0.1.0-ci.1` pin, `WpfHotReloadEndToEndTests.ApplyDelta_UpdatesALiveWpfAssemblyAndRunsItsMetadataHandler`
failed (`error: invalid request`); with the published packages it passes.

Tags: **[verified]** run or read in this tree, **[docs]** from published sources, **[open]** unknown, needs an experiment.

## Why

Route D in `xsg-hot-reload.md` delivers C# changes (generated `InitializeComponent`, `{cs: ...}`
expressions, new `x:Name` fields) as metadata deltas. Today OpenDevelop has no delta producer
and SharpDbg has no `ApplyChanges`, so the only option is `dotnet watch`, which cannot coexist with the
IDE debugger. The same machinery would also give OpenDevelop plain C# Hot Reload.

## Two apply paths, one producer

| | Tier 1: no debugger | Tier 2: under SharpDbg |
|---|---|---|
| Applier | agent calls `MetadataUpdater.ApplyUpdate` in-process | debugger calls `ICorDebugModule2::ApplyChanges` |
| Needs | startup hook agent, `DOTNET_MODIFIABLE_ASSEMBLIES=debug` | module JIT flags with EnC, process stopped |
| Debug state | none | breakpoints, active statements, PDB must follow the edit |
| Effort | small | large |

Both consume the same delta triple (metadata, IL, PDB) from one producer, so the producer is built once.

## Findings

### Runtime support on macOS/Linux [verified]
`src/coreclr/clrdefinitions.cmake` in dotnet/runtime `main` defines `FEATURE_REMAP_FUNCTION`
(which gates `CordbModule::ApplyChanges`) for AMD64 or ARM64 on **every OS**, and for i386 only on Windows.
The 2021 Rider post that says EnC under a debugger is Windows-only is out of date. The netcoredbg
maintainers reached the same conclusion in Feb 2026 (Samsung/netcoredbg#214): the debugger only needs to
supply deltas and keep breakpoint locations correct. netcoredbg's own implementation is MI-protocol only
and uses an `NCDB_DOTNET_STARTUP_HOOK`; its `test-suite/NetcoreDbgTest/Delta` shows how it generates deltas.

### ICorDebugSharp 0.1.8 already exposes the primitives [verified, reflection over the package]
- `ICorDebugModule2.TryApplyChanges(uint cbMetadata, byte[] pbMetadata, uint cbIL, byte[] pbIL)`
- `ICorDebugModule2.TrySetJITCompilerFlags` / `TryGetJITCompilerFlags`
- `ICorDebugProcess7.TrySetWriteableMetadataUpdateMode`
- `ICorDebugManagedCallback.EditAndContinueRemap` plus `GetEnCRemapSequencePoints`
- legacy snapshot API (`GetEditAndContinueSnapshot`, `CommitChanges`): not needed.

### SharpDbg groundwork [verified, read `ManagedDebugger_EventHandlers.cs`]
- Modules are tracked in `_modules` with `ModuleInfo` (ICorDebugModule, MVID, metadata reader, symbols).
- It already reads `corModule.JITCompilerFlags` and treats `DISABLE_OPTIMIZATION`/`ENABLE_ENC` as "user code",
  so the runtime already enables EnC on debuggable user modules.
- Missing: a request to apply deltas, writable-metadata mode, remap callback handling, PDB delta merge,
  breakpoint rebinding after an edit, and mapping a delta's module id to `ModuleInfo`.

### Runtime metadata behavior and provenance [verified]
No additional metadata interop library is required for `ApplyChanges`.  In
[`dotnet/runtime` `CordbModule::ApplyChanges`](https://github.com/dotnet/runtime/blob/d7a6547f2a039031d9c29180bb95541a06a86276/src/coreclr/debug/di/module.cpp#L1973-L2047),
commit `d7a6547f2a039031d9c29180bb95541a06a86276` (MIT), CoreCLR itself applies the
metadata delta to its debugger-side `IMDInternalImport`, replaces its public `IMetaDataImport`, and
then sends the same metadata/IL buffers to the debuggee.  This is exactly the operation reached by
`ICorDebugModule2::ApplyChanges`; SharpDbg must not separately call
`IMetaDataEmit::ApplyEditAndContinue` or manufacture a metadata importer.

This was checked because [`ICorDebugSharp`](https://github.com/MattParkerDev/ICorDebugSharp), MIT,
copyright (c) 2026 Matthew Parker, currently omits `IMetaDataEmit` even though its generator knows
about it.  Its historical generated binding (`ddd05614c7e083913b1dc18bea97e7fb041c4e71`) still
compiles against current source (`7b513750d8c3f90c877b2b7b8eac7289f2e7bc02`), but it is not needed
for this path and no external source text, generated binding, or raw-vtable call has been copied into
SharpDbg.  Therefore the existing package provenance is sufficient; any later imported source must
record its exact URL, commit, copyright, and MIT notice in `sharpdbg/THIRD-PARTY-NOTICES.txt` before review.

### The producer works with public Roslyn API [verified spike, macOS arm64, net11]
`Compilation.EmitDifference` with `EmitBaseline.CreateInitialBaseline` produced a 440 B metadata, 27 B IL and
400 B PDB delta, and `MetadataUpdater.ApplyUpdate` changed a live method from `"v1"` to `"v2"` in the same
process. Source in the appendix.

Limits of the spike: one method-body edit, no locals or lambdas (empty slot and lambda maps), semantic edit
computed by hand. A real producer must diff syntax trees into `SemanticEdit`s, read slot and lambda maps
from the PDB, and detect rude edits. Roslyn does that in `Microsoft.CodeAnalysis.EditAndContinue` (in
`Microsoft.CodeAnalysis.Features`, which OpenDevelop already references at 5.6.0). The supported entry point,
`Microsoft.CodeAnalysis.ExternalAccess.HotReload`, is what dotnet-watch and Rider consume; it is **not on
nuget.org** (404 on the flat container) **[open]**: confirm which feed ships it and whether its friend access works for us.

## Design

### Producer: `IHotReloadDeltaProducer` in OpenDevelop
Input: previous and current solution snapshot. Output per module: MVID, metadata/IL/PDB delta, updated
method tokens, updated types, and a verdict (`Applied`, `RudeEdit`, `CompileError`). A `RudeEdit` maps to
`HotReloadOutcome.RestartRequired`, a compile error to the existing build-failure state. Deleting an
`x:Name` field is a rude edit (confirmed with `dotnet watch` in `xsg-hot-reload.md`).

### Tier 1: agent applies the delta
New pipe message `applyDelta { mvid, metadata, il, pdb, updatedTypes }`. The agent resolves the assembly by MVID
and calls `MetadataUpdater.ApplyUpdate`; on the current LibreWPF/net10 path the agent explicitly invokes
assembly-level `[MetadataUpdateHandler]` methods (`ClearCache` for every handler, then `UpdateApplication`), because
the successful runtime update did not itself invoke the plugin handler in the fixture experiment. This reports
`HotReloadChangeKind.CodeDelta`, which is defined today but produced by no adapter.

### Tier 2: SharpDbg applies the delta
Custom DAP request `sharpdbg/applyDeltas` with the same payload. Sequence:
1. Ensure the process is stopped (`ICorDebugProcess.Stop`); resume afterwards if it was running.
2. Find the module by MVID in `_modules`.
3. `SetWriteableMetadataUpdateMode(AlwaysShowUpdates)` once per process, before the first apply.
4. `ICorDebugModule2.ApplyChanges(metadata, il)`.
5. Apply the PDB delta to the module's `ModuleMetadataReader` and re-resolve sequence points.
6. Rebind line breakpoints in updated methods; report moved breakpoints through DAP `breakpoint` events.
7. Tell the agent `updatedTypes` so Tier 1 can provide handler type context; Tier 2’s handler dispatch remains
   **[open]** because `ICorDebugModule2::ApplyChanges` is a distinct runtime path.
8. Handle `EditAndContinueRemap`: first version applies only when no active frame is in an updated method,
   otherwise it reports "apply pending until the frame returns".

## OpenDevelop integration points [verified, read in this tree]

| Piece | Where | What exists | What EnC needs |
|---|---|---|---|
| Change kinds | `Main/Base/Project/HotReload/HotReloadContracts.cs` | `HotReloadChangeKind.CodeDelta`, `HotReloadOutcome.{Applied,RestartRequired,BuildRequired,Failed}` | already enough; no adapter reports `CodeDelta` yet |
| Adapter | `IApplicationHotReloadAdapter` / `IHotReloadSession.ApplyAsync(HotReloadDocumentChange)` | WPF adapter (`WpfDesign.AddIn/Src/HotReload`) pushes XAML text through the agent pipe | advertise `CodeDelta` in `GetCapabilities`; in `ApplyAsync` route `.cs`/generated-code edits to the delta path |
| Debug flag | `HotReloadLaunchContext.WithDebugger` | adapter already knows if a debugger is attached | `false` -> Tier 1 (agent `applyDelta`), `true` -> Tier 2 (DAP request) |
| Roslyn | `ICSharpCode.SharpDevelop.csproj` references `Microsoft.CodeAnalysis.CSharp.Workspaces` and `.Features` | workspace and compiler available in the IDE | the delta producer (E0) lives here, next to `HotReloadService` |
| DAP client | `Debugger.AddIn/Service/Dap/DapSession.cs` (`WindowsDebugger.CurrentSession`) | custom requests already used: `loadSymbols` via `client.SendAnswerAsync`, `SendRequestAsync` for normal ones | add `ApplyDeltasAsync(mvid, metadata, il, pdb)` sending `sharpdbg/applyDeltas`; adapter reaches it through `WindowsDebugger.CurrentSession` |
| Session lifetime | `HotReloadService` disposes the session on `SD.Debugger.DebugStopped` | exists | none |

Interface the adapter needs, to keep the add-in free of Roslyn types:

```csharp
// Main/Base/Project/HotReload
public interface IHotReloadDeltaApplier   // Tier 1 and Tier 2 each implement it
{
    Task<HotReloadApplyResult> ApplyAsync(ModuleDelta delta, CancellationToken token);
}
public sealed record ModuleDelta(Guid Mvid, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypeTokens);
```

Adapter wiring: `ApplyAsync` -> producer (`IHotReloadDeltaProducer`) -> `ModuleDelta` -> applier chosen by
`WithDebugger`. A producer verdict of rude edit becomes `HotReloadOutcome.RestartRequired`, a compile error
`BuildRequired`.

Gaps found while reading:
- The producer needs the baseline (the bytes the debuggee actually loaded). The IDE builds the project, so it can
  snapshot the output dll/pdb at launch time; a rebuild by the user between launch and edit invalidates it.
- `HotReloadDocumentChange` carries one file's text; a code delta is solution-wide (generated `.g.cs` changes with
  XAML). The producer must work from the solution, not from the change object, and use it only as a trigger.
- Launch config: Tier 1 needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug`; Tier 2 needs SharpDbg `EnableEditAndContinue`
  turned on from the launch request. Both belong in `TryConfigureLaunch(project, startInfo, withDebugger)`.

## Plan

Status: **done** / **next** / todo. "Done" means committed locally and tested.

| Phase | Content | Status | Risk |
|---|---|---|---|
| E2 | Tier 2 spike: `ApplyChanges` under SharpDbg on macOS arm64 | **done** (`sharpdbg` e0407f8; `EditAndContinueTests`; full suite 182/185, the 1 failure predates it) | high |
| E0 | Producer protocol in the Roslyn host with tests: method-body edit, add field, rude edits, compile error; capture the actual launch PE/PDB and produce from the host-owned project/TFM compilation. Read PDB slot/lambda maps; move to the Features service if broader rude-edit detection needs it. | done (`59366a11e4`) | medium |
| E1 | Tier 1: agent `applyDelta` pipe message, `MetadataUpdater.ApplyUpdate`, explicit `[MetadataUpdateHandler]` invocation, `IHotReloadDeltaApplier`, WPF adapter reports `CodeDelta`, `TryConfigureLaunch` sets `DOTNET_MODIFIABLE_ASSEMBLIES=debug`. Cross-platform. | done and published (OpenDevelop `bc6b3839bd`, agent `6f2be2d`/`502377a`, pinned `0.1.0-ci.2`) | low |
| E3a | SharpDbg DAP request `sharpdbg/applyDeltas` (takes `DapRequestAndRuntimeEventLock`, wraps COM failures, stops and resumes the process), plus `DapSession.ApplyDeltasAsync` and the launch flag that turns `EnableEditAndContinue` on. | done and published (sharpdbg `e22b991`/`1d01991`/`158e240`, OpenDevelop `bc6b3839bd`/`70d3f22794`/`e498565ea8`); **real journey passes** (`XsgHotReloadEndToEndTests.CsExpressionEdit_UnderSharpDbg_ReachesTheLiveWindow`, see below) | medium |
| E3b | SharpDbg state after an edit: PDB delta merge into `ModuleMetadataReader`, breakpoint rebind, `EditAndContinueRemap`/active statements, stepping state, handler invocation (X2). | partial, committed as sharpdbg `739eaac`: portable-PDB `EncMap` overlay and source-breakpoint rebind are verified in a real debuggee; active-frame remap, stepping-state handling and handler invocation remain | high |
| E4 | OpenDevelop classification: text push vs delta per edit, rude edit -> `RestartRequired`, compile error -> `BuildRequired`, UI state. | todo | low |

Order: E0 -> E1 (XSG `{cs:}` Hot Reload without a debugger) -> E3a (same edits under the debugger, no
breakpoint fidelity yet) -> E3b -> E4 refinement. E3a before E3b is intentional: an applied delta with stale
breakpoints is already useful and testable, and it puts the OpenDevelop side under test early.

Review follow-ups from the e0407f8 review (non-blocking): document or take `DapRequestAndRuntimeEventLock` in
`ApplyDelta` (done by E3a), try/catch around the COM calls, derive the runtimeconfig version in
`EditAndContinueTests` from `Environment.Version` instead of hardcoding `net11.0`.

## Open experiments

- ~~**X1**~~ **Answered yes** (see E2 result). Original question: does `ApplyChanges` succeed on macOS arm64 against a debuggee under SharpDbg, with an
  EmitDifference delta? Needs the E2 spike.
- ~~**X2**~~ **Answered:** nobody, by itself; the UI stayed unchanged although `ApplyChanges` succeeded. After a successful debugger apply, `AgentPipeHotReloadSession` sends the agent `runUpdateHandlers { mvid, updatedTypeTokens }` and the agent runs `ClearCache` then `UpdateApplication` on the assembly's handlers. A paused debuggee cannot answer; the result is still `Applied`, with a note that the view refreshes when it runs.
- **X3** **Answered for updated methods:** retain each portable-PDB delta reader, use its `EncMap` to map aggregate MethodDef rows to the dense delta `MethodDebugInformation` table, and overlay those entries by original method token. The first implementation rebinds source breakpoints after that overlay; active-frame remap remains part of E3b.
- **X4** ICorDebugSharp marshalling for multi-hundred-KB deltas.
- **X5** Where `Microsoft.CodeAnalysis.ExternalAccess.HotReload` is published and whether OpenDevelop may reference it.
- **X6** Partly answered: launched under SharpDbg with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` (the adapter sets it for every launch), `ApplyChanges` works. Attaching later is untested.

## E3a journey (2026-11-22)

The XSG fixture runs under the real SharpDbg (`DapSession`, `EnableEditAndContinue`, the adapter's launch environment),
the Roslyn host produces the delta for a `{cs:}` edit, `SharpDbgDeltaApplier` sends `sharpdbg/applyDeltas`, and the
live window shows the new text, with the application never paused. Two changes made that work:

- **SharpDbg applied only to a stopped debuggee**, and the IDE applier refused unless the user paused first, so Hot
  Reload under the debugger was unusable in the normal flow (edit while the app runs). `ManagedDebugger.ApplyDelta` now
  brackets a running process with `ICorDebugController::Stop(0)`/`Continue(false)` (nested, no debugger events), and
  `SharpDbgDeltaApplier` only requires a live session. SharpDbg test: `ApplyDelta_WhileRunning_TakesEffectWithoutAStop`.
- **Handlers (X2)**, as above.

## Delta generations (2026-11-22)

Deltas are a chain: each names the generation it builds on (`EncBaseId`). The producer used to diff every edit
against the launch baseline, so the second delta of a session claimed to build on generation 0 while the application
ran generation 1. Under SharpDbg that hung the debuggee; it surfaced as soon as a test made a second edit and a
revert. Now the producer keeps the generation the application runs: a produced delta is pending until the next
`HotReloadDeltaRequest` reports `PreviousDeltaApplied`, which advances to `EmitDifferenceResult.Baseline` and the
current compilation; otherwise the pending one is dropped. `AgentPipeHotReloadSession` sets the flag from the apply
outcome. Tests: `RoslynHostProcessTests.HotReloadDelta_BuildsOnTheAppliedGenerationOnly` (reads `EncId`/`EncBaseId`,
runs in CI) and both `XsgHotReloadEndToEndTests` (edit, second edit, revert to launch text, with and without SharpDbg).

## E2 spike result (macOS arm64, net11, SharpDbg `ManagedDebugger.ApplyDelta`)

Spike code: `sharpdbg/src/SharpDbg.Infrastructure/Debugger/ManagedDebugger_HotReload.cs` (uncommitted), driven by a
console app that emits v1, launches it under SharpDbg, stops at `Debugger.Break()`, and applies a 444 B / 27 B delta.

- **[verified]** `SetWriteableMetadataUpdateMode(AlwaysShowUpdates)` returns `CORDBG_E_UNSUPPORTED` (0x80131C4E).
  Source: `CordbProcess::SetWriteableMetadataUpdateMode` rejects it whenever `m_pShim != NULL`, i.e. for the
  legacy shim-based (dbgshim) debugging path SharpDbg uses. Not fatal; skip the call.
- **[verified]** `TryGetJITCompilerFlags` on the user module reports `DISABLE_OPTIMIZATION` only, **no `ENABLE_ENC`**.
  SharpDbg must call `TrySetJITCompilerFlags(ENABLE_ENC | DISABLE_OPTIMIZATION)` in the module-load callback.
- **[verified]** The SIGSEGV was a null dereference in `CordbModule::ApplyChanges` (crash report: `libmscordbi`):
  it uses `m_pIMImport`, which only exists after the first `GetMetaDataInterface<IMetaDataImport>()` on the module.
  SharpDbg reads metadata from files, so it never created it. Calling it before `TryApplyChanges` fixes the crash.
- **[verified] X1 answered:** with `ENABLE_ENC` set at module load plus the metadata interface touched, `ApplyChanges`
  returns S_OK on macOS arm64 / net11 under SharpDbg, and the debuggee printed `first=v1` then `second=v2`
  after resuming. The later E3b local work additionally verifies portable-PDB overlay and a moved source breakpoint.
- Productised in sharpdbg: `ManagedDebugger.EnableEditAndContinue` (opt-in, default off, set before launch) and
  `ManagedDebugger.ApplyDelta(mvid, metadata, il)`, covered by `EditAndContinueTests.ApplyDelta_MethodBodyEdit_TakesEffectAfterResume`.
- Remaining for E3: `EditAndContinueRemap`/active statements, stepping-state preservation, handler invocation (X2), a real OpenDevelop DAP journey, and review/commit.

## Appendix: Tier 1 spike

Run with `DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run` on a net11 console project referencing
`Microsoft.CodeAnalysis.CSharp` 4.12.0. Output: `before: v1`, `EmitDifference success=True ...`, `after:  v2`.

```csharp
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

Console.WriteLine($"MetadataUpdater.IsSupported={MetadataUpdater.IsSupported}");
if (!MetadataUpdater.IsSupported) { Console.WriteLine("set DOTNET_MODIFIABLE_ASSEMBLIES=debug"); return 2; }

string Src(string v) => "public static class Target { public static string Get() { return \"" + v + "\"; } }";

var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
var refs = tpa.Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll")
              .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
CSharpCompilation Make(string v) => CSharpCompilation.Create("target",
    new[] { CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(Src(v), System.Text.Encoding.UTF8), path: "Target.cs") }, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

var v1 = Make("v1");
var dir = Path.Combine(Path.GetTempPath(), "encspike"); Directory.CreateDirectory(dir);
var dll = Path.Combine(dir, "target.dll");
var pe = new MemoryStream(); var pdb = new MemoryStream();
var emit = v1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
if (!emit.Success) { Console.WriteLine(string.Join("\n", emit.Diagnostics)); return 1; }
File.WriteAllBytes(dll, pe.ToArray());

var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
string Call() => (string)asm.GetType("Target")!.GetMethod("Get")!.Invoke(null, null)!;
Console.WriteLine("before: " + Call());

// Baseline from the emitted image. No locals/lambdas, so empty debug-info maps are valid.
var md = ModuleMetadata.CreateFromImage(pe.ToArray());
var baseline = EmitBaseline.CreateInitialBaseline(v1, md,
    _ => EditAndContinueMethodDebugInformation.Create(ImmutableArray<byte>.Empty, ImmutableArray<byte>.Empty),
    _ => default(StandaloneSignatureHandle),
    hasPortableDebugInformation: true);

var v2 = Make("v2");
var oldM = v1.GetTypeByMetadataName("Target")!.GetMembers("Get")[0];
var newM = v2.GetTypeByMetadataName("Target")!.GetMembers("Get")[0];
var edits = new[] { new SemanticEdit(SemanticEditKind.Update, oldM, newM) };

var mdS = new MemoryStream(); var ilS = new MemoryStream(); var pdbS = new MemoryStream();
var diff = v2.EmitDifference(baseline, edits, _ => false, mdS, ilS, pdbS);
Console.WriteLine($"EmitDifference success={diff.Success} md={mdS.Length}B il={ilS.Length}B pdb={pdbS.Length}B updatedMethods={diff.UpdatedMethods.Length}");
if (!diff.Success) { Console.WriteLine(string.Join("\n", diff.Diagnostics)); return 1; }

MetadataUpdater.ApplyUpdate(asm, mdS.ToArray(), ilS.ToArray(), pdbS.ToArray());
Console.WriteLine("after:  " + Call());
return Call() == "v2" ? 0 : 3;
```
