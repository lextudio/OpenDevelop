# C# Edit and Continue (SharpDbg)

OpenDevelop now offers Visual Studio-style Hot Reload / Edit and Continue for ordinary C#
projects under the SharpDbg debugger. XAML apps keep their framework-specific agents (WPF,
WinUI); this path is for console apps, libraries, services and other managed C# projects.

## User experience

- Edit C# source while stopped at a breakpoint.
- Press **Continue**, **Step Into/Over/Out**, or **Set Next Statement**. Pending edits are
  compiled to an EnC delta and applied through SharpDbg before execution resumes.
- Press **Apply Hot Reload** explicitly if you want to apply without resuming.
- Enable **Apply Hot Reload changes when a file is saved** in **Tools > Options > Debugging >
  General** to apply on save as well.
- Active statements in the stopped stack are read-only while paused, matching Visual Studio's
  active-statement rule.
- Successful applies are reported in the **Hot Reload** output category and the status bar.
- A rude edit (unsupported change) or a compilation error blocks the resume and shows a warning,
  matching Visual Studio's "changes are not allowed" behaviour.
- The feature is available only when **Enable Hot Reload** is on, the project is built
  unoptimized (Debug), and is not a WPF/WinUI project (those have their own XAML adapters).
- Attach works best-effort when the attached process's main module is a project output in the
  open solution; the baseline is captured from that module and the PDB beside it. It assumes the
  process runs that build. Processes started through the shared `dotnet` host (whose main module
  is `dotnet`, not the app) are not associated automatically.

## How it is wired

| Piece | Location |
|---|---|
| Adapter (claims plain C# projects) | `src/AddIns/Debugger/Debugger.AddIn/Service/Dap/CSharpApplicationHotReloadAdapter.cs` |
| Debugger-only session | `src/AddIns/Debugger/Debugger.AddIn/Service/Dap/CSharpHotReloadSession.cs` |
| Roslyn EnC producer | `src/Main/LanguageServices/LanguageServices.Roslyn/HotReloadDeltaProducer.cs` |
| Delta application over DAP | `src/AddIns/Debugger/Debugger.AddIn/Service/Dap/SharpDbgDeltaApplier.cs` |
| Baseline capture and session ownership | `src/Main/Base/Project/HotReload/HotReloadService.cs` |
| Resume-time apply gesture | `src/AddIns/Debugger/Debugger.AddIn/Service/WindowsDebugger.cs` |

The launch path captures the PE/PDB snapshot before the debuggee starts. The Roslyn host keeps
the matching compilation as the EnC baseline. `CSharpHotReloadSession` becomes ready only after
that baseline is captured; a delta computed against the wrong generation is never applied.

`CSharpApplicationHotReloadAdapter.StartAsync` sets `OD_HOTRELOAD_ENC=1`, which makes
`WindowsDebugger` pass `enableEditAndContinue: true` to SharpDbg. Without that opt-in the
debugger does not enable `ICorDebug` module updates.

## Current limitations

- Optimized (Release) builds are rejected up front.
- Mobile (Android/iOS/Mac Catalyst/tvOS) and MAUI launches keep their existing debugger plumbing and do not use this path.
- Mixed-mode (native + managed) debugging is not supported.
- `MetadataUpdateHandler` (`ClearCache` / `UpdateApplication`) is invoked for assembly-level handlers
  when the debuggee is paused; a delta applied while it is running does not run the callbacks.
