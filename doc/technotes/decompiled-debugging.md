# Debugging decompiled code (ILSpy-generated source and PDB)

Status (2026-11-12): **implemented on both sides, one release step open.** The `sharpdbg` change is committed
(`92a259a`, unpublished); the IDE side (this repository) is in the commit that carries this note. Until a SharpDbg package
that understands `decompileExternalCode` is published and `SharpDbgCliVersion` in `Directory.Packages.props` is moved, the pinned adapter
ignores the argument and keeps decompiling by itself, so the IDE behaves as before. Facts below were checked against the
code and, for the end-to-end behaviour, against a real IDE session on macOS. Anything marked *open* is not verified.

Related notes: [`debugging.md`](debugging.md) ("External-frame decompilation", the DAP backend and
SharpDbg bundling) and [`ilspy.md`](ilspy.md) (the decompiler host and its sequence points). This
note builds on both and does not repeat them.

## Goal

Match Visual Studio's "decompile source to symbol files": when execution reaches code that has no
PDB (a framework or NuGet assembly), **ILSpy generates the decompiled source and a PDB for it**, and
the debugger uses that PDB, so the user can stop, **step into, step through and set breakpoints**
in the decompiled document, not merely look at it.

The feature belongs to the ILSpy add-in. **Disabling the ILSpy add-in switches it off completely**:
no decompilation of any kind happens during debugging, by anyone. That requires the debug adapter
to stop decompiling on its own, which it does today (see "Why a client-side change is not enough").

Out of scope: debugging code that already has a PDB, Source Link, and the Android/iOS adapters
(they share the DAP seam but are verified separately; see [`debugging-mobile.md`](debugging-mobile.md)).

## What exists today (verified in code)

| Piece | Where | Behaviour |
|---|---|---|
| Adapter decompiles by itself | `../sharpdbg` `ManagedDebugger_FrameSourceInfo.cs` `GetSourceInfoAtFrame(frame, decompileIfNeeded)`, `Decompilation/PortablePdbWriter2.cs` | A module with `MetadataReader.HasSymbols == false` is decompiled with the adapter's own ILSpy and a PDB is written to `<LocalAppData>/Temp/SharpIdeSymbolCache/<asm>/<mvid>/<asm>.decompiled.pdb`, then loaded with `ModuleMetadataReader.TryLoadSymbols`. A cached file is loaded without decompiling. |
| `resolveStackFrame` | `ResolveStackFrameRequest.cs`, `DebugAdapter.cs:603` | Answers a frame again with the adapter's virtual `source.path` (`decompiled/<asm>/<mvid>/<asm>/<Type>.cs`), a line, and `decompiledSourceInfo`. |
| `decompiledSourceInfo` is conditional | `CreateDecompiledSourceInfo` | `null` unless a **user-code assembly** is on the physical caller chain (`FindCallingUserCodeAssemblyPath`). A stack made only of framework frames therefore gets no `DecompiledSource`. Measured 2026-11-12 with a thread started on `Monitor.Exit`. |
| IDE opens the decompiled type | `IDecompiledSourceNavigator` (Base), `ILSpyAddIn/DecompiledSourceNavigator.cs`, `WindowsDebugger.OpenFrameAsync` | Uses the frame's `MethodToken` + `ILOffset` and the IDE's **own** sequence points. The adapter's line numbers are not used because the two sides decompile with different `DecompilerSettings`. |
| Stepping into decompiled code (old engine) | `ILSpyAddIn/ILSpySymbolSource.cs` | SharpDevelop's `ISymbolSource` implementation: fed ILSpy sequence points to the in-process debugger, active only with Just My Code off (`Handles` returns `!SD.Debugger.Options.EnableJustMyCode`). **Excluded from the build** (`ILSpyAddIn.csproj`: `Compile Remove="ILSpySymbolSource.cs"`); it targets `Debugger.Core`, which is gone. It is the precedent for the shape of this design: the ILSpy add-in supplies the symbols, so disabling it removes the feature. |
| IDE can write a PDB | `ILSpyAddIn/ILSpy/Commands/GeneratePdbContextMenuEntry.cs` | Wraps ICSharpCode.Decompiler's `PortablePdbWriter` for an assembly (the "Generate PDB" command). |
| Frame data on the client | `DapStackFrameInfo` (`Debugger.AddIn/Service/Dap/DapModels.cs`) | `FilePath`, `IsResolved`, `InstructionPointerReference`, `MethodToken`, `ILOffset`, `DecompiledSource`. |

### Why a client-side change is not enough

Both sides decompile today and nothing tells the adapter to stop. If the IDE merely wrote its own PDB
into the adapter's `SharpIdeSymbolCache` path, the adapter would load it only because of an
undocumented cache convention, and with ILSpy disabled the adapter would still decompile by itself.
The requirement "disabled ILSpy means no decompilation" therefore needs an adapter option, and the
IDE must hand the PDB over through a defined request rather than a shared folder.

## Design

Three parts, built and tested one after the other.

### 1. `../sharpdbg`: client-supplied symbols and a switch for self-decompilation

**Implemented in the `sharpdbg` working tree (2026-11-12), not yet published.** Tests:
`tests/SharpDbg.Cli.Tests/ClientSymbolsTests.cs` (declined symbols leave the frame unresolved and nothing is decompiled by the adapter;
supplied symbols resolve the frame), plus the existing `StackTraceTests`, `StepTests`, `AttachOptionsTests` and `ProtocolTests`
unchanged.

- **Launch/attach argument `decompileExternalCode`** (bool, default `true`). When `false`, `GetCachedOrGeneratePdb` neither reads
  the adapter's `SharpIdeSymbolCache` nor generates a PDB: it asks the client (below). The default keeps an older IDE and the other
  clients on today's behaviour. OpenDevelop always sends `false`, which is what makes "no ILSpy, no decompilation".
- **Event `symbolsNeeded { moduleName, modulePath, mvid }`**, raised when a frame's module has no symbols and the debugger needs them
  (a stop, a step that reaches the module, `resolveStackFrame`). The thread that needs them **waits**, exactly as it always waited
  while decompiling, until the client answers or `ClientSymbolsTimeout` (180 s) passes. This answers the earlier open question on
  holding a step: no new pause protocol is needed, because every call site was already synchronous.
- **Request `loadSymbols { mvid, pdbPath }`** answers it (empty `pdbPath` = the client has none; a timeout is remembered the same
  way). It may also arrive before the event, so a client can provide symbols up front. It is handled **outside** the request/runtime
  event lock, which the waiting thread holds.
- **No deadlock**: requests are dispatched one at a time on the connection's dispatch loop until a handler yields. With
  `decompileExternalCode=false`, `resolveStackFrame` and `stackTrace` therefore run their body on the thread pool after taking the
  lock (`RunOffDispatchLoopIfClientSymbols`), so the dispatch loop can deliver `loadSymbols`. Lock order is unchanged.
- Ending the session releases every waiter (`Dispose`). `initialize` reports `supportsClientSymbols: true`. A restart keeps the
  setting (`SubscribeToDebuggerEvents` re-applies it).
- Still to do: publish through the `sharpdbg` checkout's `publish-opendevelop-packages.yml`, then move `SharpDbgCliVersion` in
  `Directory.Packages.props` (see `OpenDevelop/AGENTS.md`); check `SharpDbg.AndroidAgent`, which has its own request switch
  (`AndroidDapServer.cs:152`) and does not take part yet.

### 2. ILSpy add-in: the symbol provider (implemented)

Decision (2026-11-12): **the decompiled sources are files on disk, as in Visual Studio**, not `ilspy://` virtual documents. The
debugger then reports a real, read-only file for a decompiled frame, and breakpoints, `JumpToCurrentLine` and stepping all use the
ordinary source-file paths. The line numbers are the PDB's own because the PDB and the files come from the same generation.

- **Seam in Base**: `ICSharpCode.SharpDevelop.Debugging.IDecompiledSymbolProvider` (`src/Main/Base/Project/Debugging/`), AddInTree path
  `/SharpDevelop/Services/DecompiledSymbolProvider`, one method
  `Task<string> GetSymbolsAsync(assemblyPath, mvid, progress, ct)` returning the PDB path or null. **No implementation registered
  (ILSpy add-in disabled) means nothing is decompiled while debugging.** `ILSpyAddIn.addin` registers
  `DecompiledSymbolProvider` there.
- **`DecompiledSymbolProvider`** (ILSpyAddIn): decompiles the whole module, writes the PDB and the `.cs` files to
  `<config dir>/cache/DecompiledSymbols/<assembly>/<mvid>/` (`<assembly>.pdb`, `src/<namespace>/<Type>.cs`, a `.complete` marker written
  last), the files read-only and UTF-8 without BOM, because the PDB holds each document's SHA-256 of those exact bytes. A module with a
  `.complete` marker is not decompiled again; a half-written directory (killed IDE) is thrown away. One generation per MVID at a time.
  The constructor takes an optional cache root for tests.
  Requests for the same module directory (cache root + name + MVID) share one generation, started under a single lock so two can never
  write into the same directory; a caller's `CancellationToken` ends only that caller's wait, the generation finishes and the next request
  finds it. A module with **no CodeView debug-directory entry** (a PDB must carry the PE's id) is refused before anything is decompiled, and
  any module for which generation failed is remembered for the process, because the adapter asks again at every stop and a whole-module
  decompile is expensive; restarting the IDE retries.
- **`DecompiledPdbWriter`** (ILSpyAddIn): derived from ICSharpCode.Decompiler's `PortablePdbWriter` through SharpDbg's `PortablePdbWriter2`
  (both MIT; the file header credits them). Differences from the stock writer: a `SourceRoot` makes each document's name the absolute path of
  its file (the stock names are relative and cannot be told apart between assemblies), and the source is not embedded. It needs
  ICSharpCode.Decompiler's internal `DebugInfo` types, so `ILSpyAddIn.csproj` references **Krafs.Publicizer 2.3.0** (build time only,
  MIT, version added to `Directory.Packages.props`) with the same `Publicize` item sharpdbg uses.
  **Trap:** Publicizer applies `[assembly: IgnoresAccessChecksTo]` through the SDK's generated assembly attributes, and this project sets
  `GenerateAssemblyInfo=false`, so the attribute was silently missing and the first run threw `MethodAccessException`. The attribute is
  now written explicitly in `DecompiledPdbWriter.cs`.
- Measured on macOS: `System.Private.CoreLib` (1744 files, a 2 MB PDB) takes about 20 s, `System.Linq` (21 files) a few seconds.
- *Open*: a partial PDB for only the requested type, an option to turn the feature off while keeping the add-in, a size limit for the cache,
  and the decompiler settings (the writer uses `new DecompilerSettings()`, not the document view's).

### 3. Debugger add-in: wiring (implemented)

- `DapSession` sends `decompileExternalCode: false` in the three launch/attach argument sets (an adapter that does not know it ignores
  it), raises `SymbolsNeeded(session, e)` for the `symbolsNeeded` event and answers with `LoadSymbolsAsync(mvid, pdbPath)`. With no
  subscriber it answers at once with no PDB, so the adapter never waits for its timeout.
- `WindowsDebugger.SessionSymbolsNeeded` asks the provider (`AddInTree.BuildItems<IDecompiledSymbolProvider>`), reports progress in the
  Debug output, and **always** answers in a `finally`, with the PDB or with nothing; failures are logged, never shown as a modal dialog.
- **Client request lock.** `DapClient.SendRequestAsync` serialises requests with a lock, so `loadSymbols` sent through it queued behind
  the `resolveStackFrame` that was waiting for it (a 30 s timeout, mistaken at first for a slow decompile). `DapClient.SendAnswerAsync`
  sends an answer without that lock; `LoadSymbolsAsync` uses it. It is only for answers to something the adapter is waiting for.
- `ResolveStackFrameAsync` waits up to 4 minutes (the default request timeout is 30 s) because the adapter may be waiting for the
  decompile; the adapter's own limit is 3 minutes.
- A frame resolved this way carries the PDB's document name, an ordinary absolute path, so `WindowsDebugger.HasSourceFile` is true and
  `OpenFrameAsync` ends in `JumpToCurrentLine`. The older `IDecompiledSourceNavigator` / `decompiledSourceInfo` path remains for an
  adapter that decompiles by itself.
- Stepping into external code needs Just My Code off, as before; the IDE currently starts sessions with it on, so what this changes today is
  the explicit "open external frame" and the exception Break path (issue #27), not stepping by default. *Open*: an option to enable it.

## Compatibility and rollout

| IDE | Adapter | Result |
|---|---|---|
| new | new (not yet published) | This design. |
| new | pinned one (ignores `decompileExternalCode`) | The adapter still decompiles by itself; the IDE keeps using `decompiledSourceInfo` + `IDecompiledSourceNavigator`. Verified: `ExternalFrame_OpensDecompiledSource` passes. |
| old | new | The default `true` keeps today's behaviour. |

Release steps still to do: publish the two SharpDbg packages from the `sharpdbg` checkout's `publish-opendevelop-packages.yml`, then move
`SharpDbgCliVersion` and `SharpDbgAndroidAgentVersion` in `Directory.Packages.props`. `SharpDbg.AndroidAgent` has its own request switch
(`AndroidDapServer.cs:152`) and does not take part yet.

Local testing before that: build `../sharpdbg` and copy `SharpDbg.{Application,Infrastructure,Cli}.dll/.pdb` over `AddIns/Debugger/`. Any
build of `Debugger.AddIn` (and building the integration project) puts the packaged adapter back, so copy again right before a run.

## Tests

- **sharpdbg** (`tests/SharpDbg.Cli.Tests/ClientSymbolsTests.cs`, 6 tests): decline, supply, supply up front, timeout, disconnect while
  waiting, and a step that reaches external code. The whole suite (146) passes.
- **Unit** (`tests/OpenDevelop.Base.Tests`):
  `DecompiledSymbolProviderTests` (the PDB's documents are the files on disk and their hashes match the bytes, read-only files, no second
  decompile of a complete module, per-MVID directories, a missing assembly, a half-written directory is deleted and regenerated without touching
  its neighbours, different cache roots do not share a generation, a cancelled wait does not cancel the generation, a module without a CodeView
  entry is refused once and not retried) and `DapSymbolsNeededTests` (with a fake adapter: no subscriber
  means an immediate empty answer; the answer is not queued behind the request waiting for it).
- **Integration** (one method at a time): `DebuggerFeatureIntegrationTests.ExternalFrame_OpensDecompiledSource` accepts either route (the
  `ilspy://` document of an adapter that decompiles by itself, or the `.cs` file ILSpy wrote) and passes with the pinned adapter and with the new one;
  with the new one the run's config directory holds `cache/DecompiledSymbols/System.Linq/<mvid>/System.Linq.pdb` and 21 files.
- **Live, by hand** (macOS, DevFlow with a mutation lease): a thread whose whole stack is framework code (`Monitor.Exit`), the case
  `decompiledSourceInfo` could not handle. `od.debug.open-frame` resolved the `Exit_Slowpath` frame in about 24 s including the decompile of
  `System.Private.CoreLib`, the active document was the cached `Monitor.cs`, caret on line 122, the statement the frame executes.
- Not covered: a journey with the ILSpy add-in disabled (there is no switch for disabling one add-in in a test run; the invariant is
  unit-tested at the `DapSession` level and holds by construction, because no provider means `LoadSymbolsAsync(mvid, null)`), and the
  Break button itself (a modal dialog that `OD_TEST_MODE` auto-answers; it needs a non-test-mode launch and a real click, which a locked
  screen prevents).

## Open questions

1. ~~Can the stepper be held while the client supplies symbols?~~ Yes: it already blocked on decompilation (part 1).
2. Do breakpoints set in a decompiled file bind after `loadSymbols`? They are ordinary source breakpoints, but the module has no symbols
   until the adapter asks, so they must be re-sent. Not tested.
3. Whole-module versus per-type PDB generation for very large modules, and a cache size limit.
4. Should stepping into external code be an explicit opt-in, as in Visual Studio, and how does it interact with the Just My Code option?
5. Whether a user can switch the feature off without disabling the whole ILSpy add-in.
