# Debugging migration plan

**Status update:** the plan below originally called for a separate
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

OpenDevelop should not port the SharpDevelop debugger engine as-is. The old
SharpDevelop addin is useful as a workbench integration reference, but its engine
is a Windows-era managed debugger built around CorDebug wrappers and WinForms UI.
The OpenDevelop target is a modern, cross-platform .NET SDK IDE, so the debugger
backend should be Debug Adapter Protocol (DAP), with `sharpdbg` bundled by
default in the same style as UnoDevelop.

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
