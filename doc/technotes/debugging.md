# Debugging migration plan

**Status update (2026-10-01): an x86 native-host debug session intermittently
hangs. Two SharpDbg Launch-mode bugs were found and fixed; the harness's
"stopped never arrives" turned out to be a harness bug.** The investigation
notes further down are kept as written, but read them with this summary:

1. *Resume gave up too early* - see "Cause of the intermittent IDE failure"
   just below.
2. *Attach raced the debuggee.* dbgshim's `RegisterForRuntimeStartup` holds
   the runtime only while its callback runs. `ClrDebugExtensions.OnRuntimeStartup`
   just completed a `TaskCompletionSource` and returned, and `Initialize` /
   `SetManagedHandler` / `DebugActiveProcess` ran afterwards on another thread
   - by then the runtime was already running, so a host that loads its library
   quickly could execute the breakpoint line before anything was bound (arm64
   harness: the program printed `result 41` and exited, 1 run in 3).
   `Automatic` now takes an `onStartup` callback, and both Launch
   (`PerformLaunch`) and Attach (`PerformAttach`) attach inside it through
   `ManagedDebugger.AttachAtRuntimeStartup`.
3. *The harness* read the adapter's stdout with `BeginOutputReadLine`. DAP
   messages carry no trailing newline, so the last message - the `stopped`
   event - sat in the line reader forever. `Invoke-DapLaunchRaw.ps1` pumps raw
   characters instead. With that harness and both fixes: x86 15/15, arm64
   15/15. The "deterministic failure" and the `OnStopped2` theory below are
   therefore void.

`DebuggerIntegrationTests.ClassLibrary_NativeStartProgramLoadingRuntimeLate_HitsLibraryBreakpoint(hostArchitecture: "x86")`
(the issue #14 regression, a native host that loads a class library late
through hostfxr) fails intermittently. The debuggee is the *native host*, and
the failure is not an assertion mismatch - the session never gets going. The
debuggee's own output says it is stuck before any user code runs:

```text
> NativeHost-x86.exe is a native program; debugging starts once it loads the .NET runtime.
NativeHost: native startup
The runtime has been configured to pause during startup and is awaiting a
Diagnostics IPC ResumeStartup command from a Diagnostic Port.
DOTNET_DiagnosticPorts=""
DOTNET_DefaultDiagnosticPortSuspend=1
Still waiting for NativeHost-x86.exe to load the .NET runtime. ...
```

**Cause of the intermittent IDE failure (found 2026-10-01).** In Launch mode
SharpDbg starts the debuggee with `DOTNET_DefaultDiagnosticPortSuspend=1` and
then `ClrDebugExtensions.Automatic` calls
`DiagnosticClientHelper.DiagnosticClientResumeRuntime`, which retried
`ResumeRuntime` only 5 times (50+100+200+400+800 ms, ~1.5s in all) before
giving up. A native host only opens the diagnostic port once it loads the
runtime through hostfxr, and an x86 host under a full test-class run can take
longer than 1.5s to get there - so the resume was never sent and the runtime
stayed parked on "awaiting ResumeStartup", exactly the output above. The fix
(in `externals/sharpdbg`, `DiagnosticClientHelper.cs`) keeps retrying with a
backoff capped at 500 ms for as long as the debuggee process is alive. The
`Automatic` 5s wait for the runtime-startup callback starts only after the
resume succeeds, so it needs no change.

`Still waiting` is the notice `DapSession.ConfigurationDoneAsync` emits after
30s, so the runtime is still suspended when that prints. The test's 60s budget
then expires. Measured rate: three full-class runs gave 14/14, **13/14**,
14/14, while running that one theory in isolation gave 3/3. So roughly one run
in three, and never alone - it is timing- or interaction-dependent, not
deterministic. arm64 and x64 native hosts, and the managed-app theory, passed
in every run.

**Why there was no adapter evidence at all: the adapter logs nothing unless
asked.** `SharpDbg.Cli/Arguments.cs` accepts `--engineLogging=<path>`, and
`SharpDbg.Cli/Program.cs` only opens the writer when that argument is present
(`if (!string.IsNullOrEmpty(logPath))`). `DapSession.LaunchAdapter` passed
only `--interpreter=vscode`, so a session that went quiet produced no adapter
diagnostics whatsoever - not because they were missed, but because none were
written. `DapSession` now passes `--engineLogging` per session, writes to
`%TEMP%\OpenDevelop-DebugAdapter\sharpdbg-<timestamp>-pid<n>.log`, quotes the
tail in two places where a session fails or stalls (adapter death, and the
Launch-mode 30s notice), deletes the file when the session ends without
incident, and sweeps anything older than a day - `CleanupSession` does not run
when the IDE is killed outright, which is exactly how a test run ends, so
without the sweep a hard kill leaks one file per session.

**A 20-second harness for this class of problem** (much faster than the
~8 minute IDE class run) lives at
`%TEMP%\opencode\sharpdbg-repro\Invoke-DapLaunch.ps1`. It drives
`AddIns\Debugger\SharpDbg.Cli.dll` - the adapter the IDE actually loads, not
the one in the submodule's own output - through the same request sequence
`DapSession` sends (`initialize` with `adapterID: "sharpdbg"`, `launch` with
`env`, `setBreakpoints`, `setExceptionBreakpoints` with `user-unhandled`,
`configurationDone`; note there is no `justMyCode` on the `launch`), with
`--engineLogging` on, and prints the full DAP trace plus the engine log. Build
the fixture the same way the test does: `native_host.c` comes out of the test's
own `NativeHostSource` literal, compiled per architecture, against an AnyCPU
library so one build serves all three.

That harness reproduces a **deterministic, always-happens** failure in Launch
mode, for x86 *and* arm64, with both the debug and the release adapter build:

```text
Breakpoint bound at AddIn.cs:9 -> resolved to line 9, IL offset 5 in method 0x6000001
Event: BreakpointCorDebugManagedCallbackEventArgs
<nothing at all for the rest of the run>
```

The breakpoint **fires** - a bound breakpoint only fires once its IL offset has
actually executed - and the debuggee never prints its result line, so it is
parked on the breakpoint. `configurationDone` returns **success**, so
`ClrDebugExtensions.Automatic` completed: `RegisterForRuntimeStartup`
succeeded, the runtime-startup callback arrived, and its 5s wait was satisfied.
`OnStopped2` is subscribed once from `DebugAdapter.SubscribeToDebuggerEvents`
and `_debugger` is constructed once, so it cannot be null. The only missing
piece is that the DAP `stopped` event never reaches the client, and the
adapter says nothing about it.

Two conclusions here are **negative findings worth keeping**, because both
cost real time:

- **A stack dump's silence is not evidence.** `AsyncStepper.TryHandleBreakpoint`
  opens with `using (await _lock2.LockAsync())` on a non-reentrant
  `NeoSmart.AsyncLock` that is also taken *synchronously* at
  `AsyncStepper.cs:582` and `:594`, so a sync-over-async deadlock looked like
  the obvious culprit. `dotnet-stack report` shows no `AsyncStepper` frame and
  appears to refute it - but a **suspended `async Task` state machine lives on
  the heap and is invisible to a thread-stack dump**. Use
  `dotnet-dump collect` + `dumpasync`; it lists suspended async methods
  (`dumpstack` is WinDbg, not SOS - SOS spells it `clrstack`, and `clrstack -a`
  only walks the *current* thread).
- **The diagnostic-port resume is not the trigger, at least in isolation.** Run
  directly, the runtime starts normally every time - `System.Private.CoreLib`
  loads, the breakpoint binds - and `awaiting ResumeStartup` never appears.
  Repeating the harness with the adapter environment the IDE gives it
  (`DOTNET_ROOT`/`DOTNET_HOST_PATH` set, `DOTNET_ROOT*` cleared, exactly what
  `DapSession.UseDotNetHost` does) changes nothing. So the visible "awaiting
  ResumeStartup" in the IDE is a *symptom* of the same wedge, not its cause.

`dumpasync` on a wedged adapter shows the managed event pump **idle again**
(`ManagedDebugger+<ProcessRuntimeEventQueue>d__77` awaiting), with no
`AsyncStepper` async frame at all: the breakpoint handler ran to completion,
raised nothing, and logged nothing - the `catch` in `ManagedDebugger.OnAnyEvent`
that would log `Error handling event ...` never fired. So the remaining gap is
between `OnStopped2.Invoke` and the client receiving the event, and pinning it
down needs one log line inside SharpDbg's `OnStopped2` lambda around
`Protocol.SendEvent`.

**None of this is proven to be the IDE's x86 failure**, and the two are kept
separate on purpose. What they share is: Launch mode, the debuggee stopped on a
breakpoint that did fire, the client never told, and the adapter's event pump
silent. Attach mode behaves correctly in both - and that is the most useful
lead, because it is the one variable the harness and the IDE were not sharing
before it was found.

Fixing this means editing the `externals/sharpdbg` submodule, and that has a
process cost worth stating: the pinned revision `93b25ac` is not on any remote
branch (`origin/main` is a single squashed root commit from 2026-08-05, and
`93b25ac` is 2026-09-15), so a fix means a new branch, a push, and a submodule
pointer bump here.

Also note that the "sharpdbg submodule and bundling" section below still says
to build `SharpDbg.Cli` only "if `SharpDbg.Cli.dll` is missing". That gate is
wrong and was removed: it froze each configuration at its first build, so a
Release payload kept a two-week-old adapter that still ran native hosts like
managed apps. The adapter is now built on every build; `dotnet build` is
incremental, so an unchanged adapter costs a no-op.

---

**Status update (2026-07-27): one shared DAP session backend for both hosts.**
UnoDevelop used to carry its own from-scratch DAP client (`DapClient.cs`) and
session/workbench-glue class (`DebugService.cs`), independently reimplementing
the same Content-Length framing protocol `Debugger.AddIn`'s `Service/Dap/`
already had. That duplication is gone: `Service/Dap/DapClient.cs`,
`DapSession.cs`, and `DapModels.cs` are the one DAP transport/session
implementation, linked into UnoDevelop's `SharpDevelop.csproj` the same way
`UnitTesting.csproj` links the classic unit-testing backend (see
`unit-testing.md`), and `UnoDevelop.Services.DebugService` is now a thin
wrapper around `DapSession` rather than a second thing owning its own
`DapClient`/JSON parsing.

The two hosts genuinely differ in how they hand the debuggee to the adapter,
so that had to be modelled rather than merged away: OpenDevelop's
`WindowsDebugger` sends a plain DAP `launch` (the adapter spawns the process),
while UnoDevelop's `DebugService` spawns the debuggee itself, suspended, and
sends `attach` with its process id, resuming the runtime once the DAP
configuration window closes (`DiagnosticsClient.ResumeRuntime()`) - closer to
SharpDbg's own out-of-process test practice. `DapSession.StartAsync` takes a
`DapLaunchMode` parameter (`Launch` default, `AttachToSuspendedProcess`) to
cover both without either host bending to the other's launch semantics.
`DapClient` also picked up UnoDevelop's two real improvements in the merge:
write/request locking (needed once reverse DAP `request` messages share the
same writer as outgoing requests) and auto-acking reverse requests the adapter
sends (OpenDevelop's copy had neither). `DapSession`'s constructor takes a
`clientId` (shown in adapter logs) and an optional log sink instead of either
being hardcoded.

Not merged, and not worth merging: OpenDevelop's WPF debugger pads
(`Pads/*.cs`), `TreeModel/*`, and the text/XML/grid visualizers are a genuine
superset with no UnoDevelop analogue - they consume `DapSession` from above,
same as before. UnoDevelop's MSBuild build+`TargetPath` resolution
(`ResolveBuildOutputAsync`) stays host-specific for the same reason project
launch resolution was always going to differ (see "Project launch resolution"
below). The two independently-authored `sharpdbg` submodule build/bundling
MSBuild targets (one per host's own `.csproj`) were left as-is - both build the
one nested submodule under `externals/OpenDevelop/externals/sharpdbg`, but
unifying MSBuild target definitions across two SDK projects with different
output layouts was judged lower value than the C# duplication above.

---

**Status update (2026-07-26): the DAP-backed engine itself is in place.**
The plan below originally called for a separate
`DapDebugger.AddIn` skeleton alongside the legacy `Debugger.AddIn`. In practice
the migration was done in place instead: `Debugger.AddIn`'s `WindowsDebugger`
now talks DAP directly (`Service/Dap/DapSession.cs`), `Debugger.Core` (ICorDebug)
is no longer referenced, and the standalone `DapDebugger.AddIn` project has been
removed - `OpenDevelop.Mvp.slnx` now builds `Debugger.AddIn.csproj` directly.
Known gaps versus the old ICorDebug engine: attach-to-process, set-next-
statement, run-to-cursor, thread freeze/priority, per-frame module/argument
display toggles, Class Browser integration for the debuggee's loaded modules,
and the ObjectGraph visualizer (no live-object identity/cycle data over DAP).
The rest of this document is kept for historical background.

## SharpDbg visualizers and regression coverage

The WPF Debugger add-in retains three DAP-backed visualizers:

- **Text visualizer** for a non-empty scalar value.
- **XML visualizer** for a scalar whose adapter-rendered value begins with XML
  (the opening/closing DAP string quotes are removed before it is shown with XML
  syntax highlighting).
- **Collection visualizer** for any DAP variable with a
  `variablesReference`; it shows the adapter's immediate children as Name,
  Value and Type rows. DAP cannot reliably distinguish a collection from an
  arbitrary expandable object, so this availability rule is deliberately broad.

`ObjectGraphVisualizer` is intentionally not compiled or registered. The old
implementation depended on ICorDebug object identity/address information to
walk cycles; standard DAP, including SharpDbg, does not expose an equivalent.
It must remain documented as unsupported unless SharpDbg gains an explicit
extension that provides stable object identity.

`DebuggerIntegrationTests.SharpDbgVisualizers_OfferAndRenderTextXmlAndCollectionValues`
is the end-to-end regression test. It starts the real bundled SharpDbg adapter,
stops `DebugTestApp` at a deterministic breakpoint, obtains the real
`ValueNode.VisualizerCommands`, and invokes the production command for each
visualizer. The DevFlow probe schedules inspection while the command's real
`ShowDialog()` modal loop is running, records the actual WPF window content,
then closes it. It asserts text content, XML syntax highlighting, and the two
DAP collection rows; this prevents an `evaluate`-only test from being mistaken
for visualizer coverage.

OpenDevelop should not port the SharpDevelop debugger engine as-is. The old
SharpDevelop addin is useful as a workbench integration reference, but its engine
is a Windows-era managed debugger built around CorDebug wrappers and WinForms UI.
The OpenDevelop target is a modern, cross-platform .NET SDK IDE, so the debugger
backend should be Debug Adapter Protocol (DAP), with `sharpdbg` bundled by
default in the same style as UnoDevelop.

## External-frame decompilation and the Eval console (SharpIDE v0.1.32 parity)

Two SharpIDE v0.1.32 features (2026-10-25) that OpenDevelop already has most of the groundwork
for. This note records what exists, the small gap on each side, and the tests that pin them.
The decompiler host is described in `ilspy.md`.

### 1. Opening an external (decompiled) stack frame

Goal: with "Show External Code" on, double-clicking a call-stack frame that has no source (a
framework or NuGet frame without a PDB) opens the decompiled type in the ILSpy document tab and
puts the caret on the statement the frame is executing, instead of doing nothing.

#### What already exists

- `SharpDbg` returns external frames from `stackTrace` unresolved (no `source`,
  `presentationHint: subtle`, `isResolved: false`). The custom request `resolveStackFrame`
  (`ResolveStackFrameRequest.cs`) generates a decompiled PDB for the module on demand and answers
  with the frame again, now carrying `decompiledSourceInfo { typeFullName, assembly { assemblyPath,
  mvid }, callingUserCodeAssemblyPath }`. `decompiledSourceInfo` is null when no user-code
  assembly is on the physical caller chain.
- `instructionPointerReference` on every frame is `"0x" + methodToken + "+0x" + ilOffset`
  (`ManagedDebugger_RequestHandlers.cs`), but `methodToken` is an ICorDebugSharp `mdMethodDef`, so
  the string actually reads `0xmdMethodDef { Rid = 702, Type = mdtMethodDef, Value = 100663998,
  IsNil = False }+0x4` (offset decimal). That is an adapter formatting quirk; `DapSession` parses
  both this and the plain `0x<token>+0x<offset>` shape. It is the only place the method token and IL
  offset reach the client.
- `resolveStackFrame` answers with the adapter's own virtual `source.path`
  (`decompiled/<asm>/<mvid>/<asm>/<Type>.cs`) and line, **and** a PascalCase `DecompiledSourceInfo`
  object nested under the camelCase `decompiledSourceInfo`; the client reads both spellings and must
  test for `decompiledSourceInfo` before it trusts `source.path`.
- `DapSession.GetStackFramesAsync` already reads `presentationHint` (`IsExternalCode`) and the
  call-stack pad already hides external frames unless `ShowExternalCode` is set.
- ILSpyAddIn decompiles a type with `ILSpyDecompilerService.DecompileType` and keeps
  `DecompiledTypeResult.DebugSymbols` (`MethodDefToken` + IL-offset sequence points mapped to
  lines of **our** decompiled text). `NavigateToDecompiledEntityService` opens/reuses the
  `DecompiledViewContent` for an `ilspy://` virtual file.

#### The gap

`DapSession` never sends `resolveStackFrame`, ignores `decompiledSourceInfo` and
`instructionPointerReference`, and `CallStackPadViewModel` double-click returns early for a frame
without `FilePath`. Debugger.AddIn does not reference ILSpyAddIn, so it needs a seam.

#### Design

1. `DapStackFrameInfo` gains `IsResolved`, `InstructionPointerReference`, `MethodToken`,
   `ILOffset` and `DecompiledSource` (`AssemblyPath`, `TypeFullName`).
2. `DapSession.ResolveStackFrameAsync(frame)` sends `resolveStackFrame` and returns the refreshed
   `DapStackFrameInfo`. An error response (`success: false`) is thrown as
   `InvalidOperationException`; `WindowsDebugger.OpenFrameAsync` catches it, logs it, still makes the
   frame the active one and reports `Resolved = false` with nothing opened.
3. Seam in Base: `ICSharpCode.SharpDevelop.Debugging.IDecompiledSourceNavigator`
   (`Task<bool> NavigateAsync(string assemblyPath, string typeFullName, int methodToken, int
   ilOffset)`), built from the AddInTree path `/SharpDevelop/Services/DecompiledSourceNavigator`.
   ILSpyAddIn registers the implementation.
4. The ILSpy implementation takes the top-level type of `typeFullName` (text before the first
   `+`; nested types live in their declaring type's document), opens the document through
   `NavigateToDecompiledEntityService` and awaits its `DecompilationTask`. `DecompiledViewContent`
   keeps the `DebugSymbols` of its own decompile (so the type is decompiled once), the navigator
   finds the entry whose `MethodDefToken == methodToken` and the sequence point containing
   `ilOffset`, and jumps there. (`SD.ParserService.Parse` does not return an
   `ILSpyParseInformation` for these virtual files in the current host, so `ILSpySymbolSource`'s
   route is not used.) When no
   sequence point matches it falls back to the start of the method; when the method is missing it
   leaves the caret at the type.
5. `WindowsDebugger.OpenFrame(frame)` is the shared path for the double-click and the DevFlow
   action: normal source → `JumpToCurrentLine`; no source and unresolved → resolve, then call the
   navigator with the resolved frame's data. It never changes the current stack frame's
   `FilePath` (there is no real file), only the active frame id, so Locals/Watch still evaluate in
   that frame.

The adapter's own line numbers for the decompiled PDB are **not** used: the adapter and OpenDevelop
decompile with separate `DecompilerSettings`, so only the IL offset is portable between them.

#### Tests

`DebuggerFeatureIntegrationTests.ExternalFrame_OpensDecompiledSource` (fixture: the
`System.Linq` `Select` lambda in `tests/fixtures/DebugTestApp/Program.cs`) stops inside the lambda,
asks `od.debug.open-frame` for the first frame that has no file and asserts: the frame was
resolved, an `ilspy://` document is the active document, its text contains the declaring type,
and the caret line is non-zero and inside the document. The action exercises the same
`WindowsDebugger.OpenFrame` the double-click calls, not the final seam alone.

### 2. Eval window

SharpIDE's Eval window is an editor with C# highlighting and completion that evaluates in the
current frame. OpenDevelop's equivalent is the **Debug Console** pad (`ConsolePadViewModel`):
`WindowsDebugger.EvaluateAsync(code, "repl")` evaluates through the DAP `evaluate` request.

#### Gaps

- The console never called `ConsolePadCore.SetHighlighting`, so input was plain text.
- **Completion never worked.** `ConsolePadViewModel` (and `AutoCompleteTextBox`, the Watch pad and
  breakpoint-condition editor) asked `DebuggerDotCompletion` for a binding from
  `SD.LanguageService.GetLanguageByExtension(".cs").CreateCompletionBinding(...)`. On the C# binding
  that method is a stub that always returns null (`CSharpLanguageBinding.cs`; completion for real
  editors is driven by `RoslynCodeCompletionBinding`, registered separately), so no completion
  window was ever shown, in any frame. An earlier revision of this note wrongly listed completion as
  already present.
- The console state was not observable from DevFlow.

#### Design

- Call `core.SetHighlighting("C#")` when the pad is created.
- Completion goes through the language-service contract that `RoslynCodeCompletionBinding` uses
  (`LanguageServiceRegistry`, `UpsertDocumentAsync`, `GetCompletionsAsync`), because these editors
  hold no file of their own. `DebuggerDotCompletion.GetCompletionsAsync(file, line, expression)`
  upserts a copy of the context frame's source file (the open editor's live text, else the file)
  with `expression;` inserted at the start of the frame's line, so the frame's locals and the
  enclosing type are in scope, asks for completions right after the expression, and always puts the
  file's current text back (`finally`, re-read so an edit made meanwhile is not reverted). `ShowCompletionAsync` then shows the window with
  `LanguageServiceCompletionItemList` unless the typed text changed meanwhile. The console, the
  Watch/condition `AutoCompleteTextBox` and the DevFlow action share it. A statement spanning several
  lines is entered at the frame's reported line, so context is best-effort there.
- The context frame (`WindowsDebugger.GetCompletionContextFrameAsync`) is the current frame when it
  has real source and is not external; otherwise the first caller with `!IsExternalCode` and a
  `FilePath`, so completion keeps working after an external frame is opened. That frame's own
  locals are not completable (they live in the decompiled code), but names of the user caller are.
- `od.debug.console-state` reports the pad's `Text` and the name of the highlighting attached to the
  real editor; `od.debug.console-eval` runs a command through the console's evaluate path and
  returns the console text; `od.debug.console-completions` returns the context frame and the
  completion labels for an expression; `od.debug.console-type-dot` types an expression ending in `.`
  into the console and reports whether the completion popup appeared, so a test reads state rather
  than pixels.

#### Tests

`DebuggerFeatureIntegrationTests.DebugConsole_HighlightsAndEvaluates`: stop at the `answer`
breakpoint, assert highlighting is attached, evaluate `answer + 1` through
`od.debug.console-eval` (the same `AcceptCommand` path) and assert `43` is appended to the
console. `ExternalFrame_ConsoleCompletionResolvesInTheNearestUserFrame` stops in the `Select` lambda,
checks `numbers.` completes to a list containing `Length` in that frame, opens the `System.Linq`
frame and checks the context is now the caller `Main` in `Program.cs` (a different line) and the
same expression still completes to `Length`. It then types `numbers.` into the console through
`od.debug.console-type-dot` (the code path typing `.` runs) and asserts the console editor shows a
completion popup with a selected item; the console's editor is created by
`SD.EditorControlService.CreateEditor`, i.e. the `CodeCompletionEditorAdapter` that implements
`ShowCompletionWindow` (the base adapter returns null).

#### Verification record

2026-10-25 (OpenDevelop master `545fa3e205` plus the working tree of this change, Debug, macOS,
SDK 10.0.200 from `/usr/local/share/dotnet` only): `ExternalFrame_OpensDecompiledSource` and
`DebugConsole_HighlightsAndEvaluates` each pass when run alone
(`-method OpenDevelop.IntegrationTests.DebuggerFeatureIntegrationTests.<name>`). The frame test lands
on the `ArraySelectIterator.Fill` statement (line 4303 of the decompiled `System.Linq.Enumerable`).

Caveat: both runs needed a local, uncommitted workaround for LibreWPF `0.1.0-preview.69`, whose
`PortableTextLine` throws `PlatformNotSupportedException` for any `TextFormattingMode.Display` text
(no shipped service implements `IPortableDisplayTextFormatting`; the earlier "declines to
SimpleTextLine" behaviour is gone), which crashes the IDE at startup. The workaround forced `Ideal`
through a coercing metadata override and was not kept.

## What SharpDevelop did

SharpDevelop exposes debugging through `ICSharpCode.SharpDevelop.Debugging`.
The important shell contract is `IDebuggerService`, registered by the debugger
addin at `/SharpDevelop/Services`:

```xml
<Service id="ICSharpCode.SharpDevelop.Debugging.IDebuggerService"
         class="ICSharpCode.SharpDevelop.Services.WindowsDebugger" />
```

That service drives the existing Debug menu, breakpoint commands, editor
tooltips, debug layout changes, and debugger pads. The concrete implementation is
`WindowsDebugger` from `src/AddIns/Debugger/Debugger.AddIn/Service`. It wraps the
old `Debugger.Core` engine (`NDebugger`, CorDebug interop, PDB symbol source,
CorPublish attach support) and directly owns process state, stack frames,
threads, evaluations, module lists, current-line markers, and pad refreshes.

This design is tightly coupled to:

- Windows-only debugging APIs and COM interop.
- The old .NET Framework debugging model.
- WinForms dialogs and pad/tree models.
- Synchronous service methods that assume in-process debugger state.

For OpenDevelop, this means `Debugger.AddIn` should be mined for UI contracts and
commands, not used as the runtime engine.

## UnoDevelop reference design

UnoDevelop already follows the shape we want:

- `src/Main/SharpDevelop/Services/DapClient.cs` implements minimal DAP framing
  over stdin/stdout with `Content-Length` messages.
- `src/Main/SharpDevelop/Services/DebugService.cs` implements the debugger
  service by launching an external adapter process.
- `externals/sharpdbg` is a git submodule.
- The main app build builds `externals/sharpdbg/src/SharpDbg.Cli` when needed and
  copies the adapter output to `$(OutputPath)/Debugger`.
- At runtime the service resolves `Debugger/SharpDbg.Cli.dll` first, then falls
  back to the submodule artifacts during development.

The launch shape is:

```text
dotnet SharpDbg.Cli.dll --interpreter=vscode
```

The DAP session sequence is:

1. `initialize`
2. `setBreakpoints` for all known editor breakpoints
3. `launch`
4. `configurationDone`
5. react to DAP events such as `stopped`, `continued`, `thread`, `output`, and
   `terminated`

This is the path OpenDevelop should copy conceptually.

## Target architecture

Keep the SharpDevelop-facing service name and the menu/pad integration points,
but replace `WindowsDebugger` with a DAP-backed implementation.

The compatibility boundary should be:

- Existing consumers keep using `SD.Debugger` and
  `ICSharpCode.SharpDevelop.Debugging.IDebuggerService`.
- `IDebuggerService` remains the public shell API for now, even if the
  implementation internally uses async DAP requests.
- The new implementation owns a `DapClient`, the adapter process, debugger state
  caches, and translation between SharpDevelop concepts and DAP concepts.
- Old `Debugger.Core` and CorDebug code are not part of the MVP backend.

The first DAP-backed service should support:

- Start debugging current SDK-style .NET project.
- Start without debugging by running the resolved output normally.
- Stop, continue, pause, step into, step over, step out.
- Breakpoints from the editor bookmark/breakpoint manager.
- Current execution location in the editor.
- Output window forwarding.
- Threads, call stack, locals, watches, modules at a basic level.
- Hover/evaluate support via DAP `evaluate`.

Attach, set-next-statement, exception settings, advanced symbol settings, and
legacy visualizers can come later.

## sharpdbg submodule and bundling

Add `sharpdbg` as a submodule under OpenDevelop:

```text
externals/sharpdbg -> https://github.com/MattParkerDev/SharpDbg.git
```

Then add an OpenDevelop build target equivalent to UnoDevelop's:

- Define `SharpDbgProject` pointing at
  `externals/sharpdbg/src/SharpDbg.Cli/SharpDbg.Cli.csproj`.
- Define `SharpDbgBinDir` pointing at
  `externals/sharpdbg/artifacts/bin/SharpDbg.Cli/$(ConfigurationLower)/`.
- Before building the main app, build `SharpDbg.Cli` if
  `SharpDbg.Cli.dll` is missing.
- After building the main app, copy the adapter output to
  `$(OutputPath)/Debugger/`.

Runtime resolution should prefer:

1. `Path.Combine(AppContext.BaseDirectory, "Debugger", "SharpDbg.Cli.dll")`
2. `externals/sharpdbg/artifacts/bin/SharpDbg.Cli/debug/SharpDbg.Cli.dll`
3. `externals/sharpdbg/artifacts/bin/SharpDbg.Cli/release/SharpDbg.Cli.dll`

This keeps developer builds and packaged builds using the same adapter.

## Project launch resolution

Do not use old SharpDevelop project output assumptions. For SDK-style projects,
resolve debug output with modern MSBuild:

```text
dotnet msbuild <project.csproj> -getProperty:TargetPath -p:Configuration=Debug
```

If `TargetPath` is missing or the file does not exist, run:

```text
dotnet build <project.csproj> -c Debug
```

Then query `TargetPath` again. Launch DAP with:

- `program`: resolved target DLL
- `cwd`: project directory or `RunWorkingDirectory` when available
- `console`: `internalConsole` for MVP
- `stopAtEntry`: `BreakAtBeginning`

Later, launch profiles should come from `launchSettings.json`, project
properties, and CPS data, not from old `.csproj.user` Debug tab assumptions.

## Migration phases

### Phase 1: backend skeleton

- Add `externals/sharpdbg` submodule.
- Add main-app build targets that build and bundle `SharpDbg.Cli`.
- Add a small `DapClient` service.
- Add `DapDebuggerService : BaseDebuggerService`.
- Register `DapDebuggerService` in place of `WindowsDebugger` for MVP builds.
- Implement start, stop, continue, pause, step into, step over, step out.
- Forward adapter output to the Output pad.

Exit criteria: a simple SDK-style console app can hit a line breakpoint and
continue/step.

### Phase 2: editor and breakpoints

- Map SharpDevelop breakpoint bookmarks to DAP `setBreakpoints`.
- Resend breakpoints when the user toggles them during a session.
- Translate DAP stopped stack frame source/line to SharpDevelop current-line
  markers.
- Clear current-line markers on continue/stop.
- Implement hover/evaluate through DAP `evaluate`.

Exit criteria: source editor interaction feels like an IDE debugger, even if pads
are still minimal.

### Phase 3: pads

Migrate the existing debugger pads onto DAP data instead of `Debugger.Core` tree
nodes:

- Threads pad: DAP `threads`.
- Call stack pad: DAP `stackTrace`.
- Locals pad: DAP `scopes` + `variables`.
- Watch pad: DAP `evaluate`.
- Modules pad: DAP `modules` if supported by sharpdbg; otherwise hide or mark
  unavailable for MVP.
- Console/output pad: DAP `output` events.

Do not port old WinForms tree models. Build small WPF view models that reflect
DAP state.

Exit criteria: paused sessions show thread, stack, and local variable data.

### Phase 4: project-system integration

- Start debugging selected startup project from Solution Explorer.
- Respect SDK-style target framework and runtime identifier selection.
- Add launch profile selection after CPS project data is stable.
- Support project references by building through `dotnet build` on the selected
  startup project.
- Persist simple debug settings in OpenDevelop settings, not legacy
  SharpDevelop project upgrade/debug properties.

Exit criteria: real Uno SDK projects can be launched from OpenDevelop without
legacy Project Upgrade behavior.

### Phase 5: advanced features

- Attach to process if sharpdbg exposes the required DAP attach path.
- Exception break settings.
- Conditional breakpoints and logpoints.
- Run to cursor.
- Set next statement only if the adapter/runtime can support it correctly.
- Symbol settings and source lookup.
- Visualizers, starting with text/XML/object graph.

These should be feature-gated. The UI must not advertise old SharpDevelop
commands until the DAP backend actually supports them.

## Things to avoid

- Do not port `Debugger.Core` as the main backend.
- Do not depend on Windows-only CorDebug/CorPublish code.
- Do not revive old Project Upgrade or .NET Framework debug property pages for
  SDK-style projects.
- Do not make the debugger service block the UI thread while waiting for DAP
  responses.
- Do not expose menu commands whose DAP implementation is missing.

## Initial file map

Useful SharpDevelop files:

- `src/Main/Base/Project/Debugging/IDebuggerService.cs`
- `src/Main/Base/Project/Debugging/BaseDebuggerService.cs`
- `src/AddIns/Debugger/Debugger.AddIn/Debugger.AddIn.addin`
- `src/AddIns/Debugger/Debugger.AddIn/Service/WindowsDebugger.cs`
- `src/AddIns/Debugger/Debugger.Core/`

Useful UnoDevelop files:

- `src/Main/SharpDevelop/Services/DapClient.cs`
- `src/Main/SharpDevelop/Services/DebugService.cs`
- `src/Main/Debugger/IDebuggerService.cs`
- `src/Main/Debugger/*Pad.cs`
- `src/Main/SharpDevelop/SharpDevelop.csproj` sharpdbg build/copy targets
- `externals/sharpdbg/src/SharpDbg.Cli/SharpDbg.Cli.csproj`
