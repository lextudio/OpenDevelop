# MSBuild integration (real build support in the MVP)

## Status

Real (not mocked) `IMSBuildEngine` is implemented and working: `Project` → `Build` actually
compiles fixture/user projects, reports real errors/warnings, and populates the Output pad -
end-to-end, verified via `tests/OpenDevelop.IntegrationTests/BuildTests.cs`.

## Background: why this didn't work at all before

`IMSBuildEngine` (`src/Main/Base/Project/Project/Build/IMSBuildEngine.cs`) is declared as an
AddInTree service in `ICSharpCode.SharpDevelop.addin`, but the class it pointed at
(`ICSharpCode.SharpDevelop.Project.MSBuildEngine`, in
`src/Main/SharpDevelop/Project/Build/MSBuildEngine/*.cs`) is excluded from this MVP build:

```xml
<!-- Out of MVP scope: out-of-process MSBuild worker subsystem (BuildJob/EventTypes/EventSource linked
     from the separate ICSharpCode.SharpDevelop.BuildWorker WinForms console project, which is itself
     out of scope per docs/opendevelop.md). Not needed to boot an empty WPF workbench window; real build
     integration is deferred (R6+). -->
<Compile Remove="Project\Build\MSBuildEngine\**\*.cs" />
```

That real engine drives builds through a bespoke out-of-process worker
(`BuildWorkerManager.cs`/`WorkerProcess.cs`/`MSBuildEngineWorker.cs`, ~1350 lines total) talking to
a separate `ICSharpCode.SharpDevelop.BuildWorker` console project - itself a WinForms-adjacent,
out-of-scope subsystem. Because the class never compiled in, the AddInTree's declarative
`<Service id="...IMSBuildEngine" class="...MSBuildEngine"/>` entry silently failed to resolve (a
"cannot find class" warning, easy to miss), and nobody had exercised the Build command until this
session - so `SD.MSBuildEngine` throwing `ServiceNotFoundException` the first time something
actually called it (both `BuildService.BuildAsync` and, less obviously, every
`IProject.ResolveAssemblyReferences` call, since `MSBuildBasedProject.ResolveAssemblyReferences`
delegates straight to `SD.MSBuildEngine.ResolveAssemblyReferences`) had gone unnoticed.

A related, separately-discovered bug in the same crash path: `WorkbenchStartup.cs` registers
`IOutputPad` as `CompilerMessageView.Instance`, but the real `CompilerMessageView`
(`src/Main/Base/Project/Src/Gui/Pads/CompilerMessageView/CompilerMessageView.cs`, which implements
`IOutputPad`) was *also* excluded from the MVP build behind a stale comment claiming it was a
WinForms pad. Reading the file showed it was already fully WPF (`System.Windows.Controls.Grid`/
`ToolBar` + an `AvalonEdit.TextEditor` for the message text) - the exclusion predates AvalonEdit
being wired into the MVP build and was never revisited. A no-op stub
(`CompilerMessageViewStub.cs`) had been substituted so `MessageViewCategory.Create()` kept
compiling, but that stub doesn't implement `IOutputPad` - so `SD.OutputPad` threw
`InvalidCastException` the moment anything touched it. Fix: un-excluded
`CompilerMessageView.cs`/`CompilerMessageViewToolbarCommands.cs`, deleted the stub. See
`src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj`'s `<Compile Remove>` list history for the
exact lines removed.

## MinimalMSBuildEngine

New file: `src/Main/SharpDevelop/Project/Build/MinimalMSBuildEngine.cs`, registered directly in
`WorkbenchStartup.InitializeWorkbench`:

```csharp
SD.Services.AddService(typeof(IMSBuildEngine), new MinimalMSBuildEngine());
```

(The stale `<Service id="...IMSBuildEngine" class="...MSBuildEngine"/>` addin entry was removed
from `ICSharpCode.SharpDevelop.addin` since it pointed at the still-excluded real engine.)

### Design: a real `dotnet build` child process, not in-process `Microsoft.Build.Execution`

The first implementation used `Microsoft.Build.Execution.BuildManager` directly in-process (the
app already references `Microsoft.Build`/`.Framework`/`.Runtime` 18.0.2 via
`SharpDevelop.csproj`). It failed immediately with:

```text
MSB4062: The "AllowEmptyTelemetry" task could not be loaded from the assembly
.../Microsoft.NET.Build.Tasks.dll. Could not load type
'Microsoft.Build.Framework.IMultiThreadableTask' from assembly
'Microsoft.Build.Framework, Version=15.1.0.0, ...'.
```

Root cause: the SDK actually hosting this process (librewpf's local net10.0 preview install)
bundles a `Microsoft.NET.Build.Tasks.dll` that needs a newer `Microsoft.Build.Framework` API than
whatever copy of that assembly is already loaded in-process. This only surfaces when an SDK task
*actually runs* - plain project **evaluation** (e.g. Solution Explorer's file listing, which uses
`Microsoft.Build.Evaluation.Project` directly) never executes a task, so it was never hit before.
Neither clearing `MSBuildSDKsPath`-family environment variables nor passing an explicit
`MSBuildSDKsPath` *global property* changed the outcome - a hosted `BuildManager`'s SDK/task
resolution is tied to the current process's own runtime location, not something a
`ProjectCollection`/`ProjectInstance` call site can override per-call. This is, in retrospect,
exactly why the original SharpDevelop authors used a separate worker *process* for builds - not
an arbitrary design choice, a load-bearing one.

**Fix: shell out to a real `dotnet build` child process instead.** A separate process gets its own
clean MSBuild host with no shared-assembly-identity conflict. Trade-off: `ResolveAssemblyReferences`
is not implemented this way (would need the same out-of-process treatment as `BuildAsync`, wasn't
needed yet - `RoslynWorkspaceHelper.GetMetadataReferences` already falls back to the host runtime's
trusted platform assemblies when this comes back empty). `BuildAsync` is the one that matters for
the Build command and is fully implemented.

### Four more environment bugs found running the child process from inside the app

Getting a plain `dotnet build <fixture>.csproj` to succeed from *inside the running app* (as
opposed to from an interactive terminal) took four separate, independently-diagnosed fixes. Each
one was invisible from a normal terminal test and only reproduced when the build was launched from
`MinimalMSBuildEngine.BuildAsync` itself:

1. **Inherited `DOTNET_ROOT`/`MSBuildSDKsPath`.** `launch.sh` exports `DOTNET_ROOT`,
   `DOTNET_HOST_PATH`, `MSBuildSDKsPath`, `MSBuildExtensionsPath`,
   `MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET`, `MSBUILD_NUGET_PATH` (all pointing at librewpf's
   local preview SDK) before running the app, and the running app process inherits them. A child
   `dotnet build` process inherits them too, and the dotnet muxer honors `DOTNET_ROOT` to pick
   which runtime/SDK to use *regardless of which `dotnet` binary path is actually invoked* - so
   even launching a different, ordinary GA SDK's `dotnet` binary
   (`/Users/lextm/.dotnet/dotnet`, SDK 10.0.201) kept resolving back to the same incompatible
   preview SDK. Fix: explicitly `psi.EnvironmentVariables.Remove(...)` all of the above on the
   child `ProcessStartInfo` before starting it.

2. **The repo's own `global.json`.** `OpenDevelop/global.json` pins
   `"version": "11.0.100-preview.4.26210.111"` - the exact SDK version that only exists in
   librewpf's local install. `dotnet`'s SDK resolution walks *up from the current working
   directory* looking for `global.json`; running the child process with
   `WorkingDirectory = project.Directory` (a path under the OpenDevelop repo tree) found and
   honored that pin even after fix #1, immediately failing with "Requested SDK version ... not
   found" (or, worse, hanging - see #4). Fix: run the child process with
   `WorkingDirectory = Path.GetTempPath()` (anywhere outside the repo tree) and pass the project
   file as an **absolute path** argument; `global.json` resolution only looks at CWD, not at the
   target project's own path.

3. **`CurrentSolutionConfigurationContents` can't survive `-p:Name=Value` CLI encoding.**
   `MSBuildBasedProject.CreateProjectBuildOptions` sets this property to an XML blob describing the
   whole solution's project/configuration mapping, so `ProjectReference`s resolve correctly when
   building a `.sln` directly. Forwarding it naively as `-p:CurrentSolutionConfigurationContents=<xml>`
   broke MSBuild's own `AssignProjectConfiguration` task with `MSB3108: '{' is an unexpected token`
   (no escaping for `{`, `;`, quotes, etc. in a single CLI token). Since `MinimalMSBuildEngine`
   always builds one `.csproj` directly (never a `.sln`), this property is simply unneeded - fixed
   by skipping it, and separately passing `-p:BuildingInsideVisualStudio=true` (matching how any
   IDE - not a bare CLI solution build - invokes single-project builds, per that target's own
   comment: *"Inside VS, we do not need to add synthetic references... we only do this on the
   command line"*).
4. **Locale-sensitive parsing inside `AssignProjectConfiguration`.** Even after fix #3, the exact
   same `MSB3108: '{' unexpected token` symptom persisted, but *only* when the build was launched
   from the running GUI app - never from an interactive terminal testing the identical arguments
   and dotnet host. The difference: the app process's ambient locale/culture (inherited from the
   desktop session) versus the terminal's plain `LANG=C.UTF-8`. Fix: force
   `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` plus `LANG`/`LC_ALL=en_US.UTF-8` on the child
   process's environment, regardless of the app's own locale.

All four are applied in `MinimalMSBuildEngine.BuildAsync` before `Process.Start`.

### Output parsing

`dotnet build`'s console output is parsed line-by-line with a regex matching the standard MSBuild
diagnostic shape:

```text
/path/File.cs(12,34): error CS1002: ; expected [/path/Project.csproj]
/path/File.cs(12,34): warning CS0168: The variable 'x' is declared but never used [/path/Project.csproj]
```

Every line (matched or not) is also forwarded verbatim to `feedbackSink.ReportMessage(...)`, which
is what populates the real Output pad (`CompilerMessageView`/`MessageViewCategory.Text`) - so the
raw build log is always available even for lines the regex doesn't parse into a structured
`BuildError`.

### Output pad text was invisible even though it was captured correctly

`od.output-text` returned the right log content from the start (proving `MessageViewCategory.Text`
was populated correctly), but nothing was visible in the Output pad's `AvalonEdit.TextEditor`. Same
root cause as the code editor's blank-text bug: `CompilerMessageView.SetTextEditorFont()` reads its
font from `OutputWindowOptionsPanel.DefaultFontDescription()`, which defaulted to `"Consolas"` -
unresolvable outside Windows, which makes WPF's `TextFormatter` fall through to
`SimpleTextLine.CreatePortableFallback()` (an empty placeholder line, not a real glyph run - see
`Typeface.CheckFastPathNominalGlyphs`'s `NullFont` check) instead of throwing. Two more
`"Consolas"`-only defaults were found and fixed the same way, in
`Editor/IEditorControlService.cs` (`EditorControlServiceFallback.FontFamily`) and
`Editor/AvalonEditTextEditorAdapter.cs`. All four hardcoded-font call sites in the MVP build
(`CodeEditorOptions.cs`, `OutputWindowOptionsPanel.xaml.cs`, `IEditorControlService.cs`,
`AvalonEditTextEditorAdapter.cs`) now use `OperatingSystem.IsWindows() ? "Consolas" : "Menlo"`.

## DevFlow actions

`src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs`:

- **`od.build-solution(projectName?)`** - builds the current solution (all projects) or a single
  named project. Returns:
  ```json
  {
    "success": true,
    "result": "Success" | "Error" | ...,
    "errorCount": 0,
    "warningCount": 0,
    "messageCount": 0,
    "diagnostics": [
      { "isWarning": false, "isMessage": false, "fileName": "...", "line": 1, "column": 1, "errorCode": "CS0001", "errorText": "..." }
    ]
  }
  ```
  `"success": false` (with an `"error"` string) means a *harness*-level failure (no solution open,
  unknown project name) - distinct from a real build failure, which is `"success": true` with
  `"result": "Error"` and a non-empty `diagnostics` array.
- **`od.output-text(category?)`** - returns `{ "category": "...", "text": "..." }`, the full
  accumulated text of an Output pad category (default `"Build"`, i.e.
  `SD.OutputPad.BuildCategory`).

## Test coverage

`tests/OpenDevelop.IntegrationTests/BuildTests.cs`, 3 tests against the
`SolutionExplorerFixture`/`SampleApp` fixture (`tests/fixtures/SolutionExplorerFixture/`):

- `BuildSolution_FixtureProjectBuildsSuccessfully` - asserts `result == "Success"`,
  `errorCount == 0`, `warningCount == 0`, empty `diagnostics`.
- `BuildSolution_OutputPadCapturesRealBuildLog` - asserts the Output pad's `"Build"` category text
  contains `"Build started."`, `"Build succeeded."`, and the project name.
- `BuildSolution_UnknownProjectNameReturnsError` - asserts the harness-level error path for a
  project name that doesn't exist in the current solution.

## Known gaps / non-goals

- `IMSBuildEngine.ResolveAssemblyReferences` returns only `additionalReferences` (a no-op for the
  real MSBuild-based resolution) - real resolution would need the same out-of-process treatment as
  `BuildAsync`. Not yet needed: `RoslynWorkspaceHelper.GetMetadataReferences` already falls back to
  the host runtime's trusted platform assemblies when this comes back empty (see
  `doc/technotes/csharp-roslyn.md`).
- `BuildTarget.Rebuild` maps to a plain `dotnet build` (no separate clean-then-build step) since
  `dotnet build` has no single "rebuild" verb; output is still correct, just not force-rebuilt from
  clean.
- `CompileTaskNames`/`AdditionalTargetFiles`/`AdditionalMSBuildLoggers`/`MSBuildLoggerFilters` on
  `IMSBuildEngine` are empty/no-op - these were the excluded real engine's logger-pipeline
  extension points; nothing in this MVP build populates or reads them.
- Building a `.sln` with multiple, cross-referencing projects hasn't been exercised - the fixture
  is a single project. `BuildEngine.cs`'s own dependency-graph scheduling (see `BuildSystem.txt`)
  should still work correctly per-project; each project's `MinimalMSBuildEngine.BuildAsync` call is
  independent.
