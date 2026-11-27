# Workspace Trust and Restricted Mode

OpenDevelop opens solutions from sources the user may not control (a cloned repository, a downloaded
sample). Several things the IDE does on open or on a keystroke execute code that comes from that
folder. Workspace Trust is the IDE's counterpart of VS Code's Restricted Mode: a folder is either
**Trusted** or **Restricted**, and every operation that can execute workspace-supplied code asks one
service before it runs.

## 1. What executes workspace code today

| Entry point | Where | How it runs workspace code |
|---|---|---|
| Roslyn analyzers / source generators | `LanguageServiceProjectSnapshot` builds `AnalyzerAssemblyFileNames`; `CSharpVBLanguageService.CreateAnalyzerReferences` does `Assembly.LoadFrom` | In the IDE process, on open, with no isolation |
| Reference / analyzer resolution | `MinimalMSBuildEngine.RunResolveReferences`, `ResolveAssemblyReferencePathsFrom`, `ResolveAnalyzers` | Spawns `dotnet msbuild -t:ResolveReferences`: executes the project's targets and tasks, on every open |
| Restore, build, clean | `BuildService.BuildAsync`, `MinimalMSBuildEngine.RestoreAsync/BuildAsync/BuildInOneProcessAsync` | `Exec`, Pre/PostBuildEvent, custom tasks, `Directory.Build.targets` |
| `.sln` to `.slnx` migration | `SDProjectService.MigrateToSlnxIfPossible` | Runs `dotnet sln migrate` in the folder on open |
| Run / debug / test | `DefaultProjectBehavior`, `WebBehavior`, `DebuggerCommands` -> `SD.Debugger.Start*` | Launches the project's own output |
| Designers | `WpfViewContent.AddProjectDlls`, WinForms/WinUI/Gtk designer hosts | Load project assemblies, run control constructors |
| Terminal | `TerminalPaneModel` -> `TerminalSession.StartAsync` | A shell in the workspace folder |
| Git status overlay | `GitStatusService.RunGit` | `.git/config` can set `core.fsmonitor`, an arbitrary command run by `git status` |

Plain MSBuild *evaluation* (Solution Explorer's in-process load) executes no tasks and stays available
in Restricted Mode.

## 2. Model

- `TrustState`: `Trusted` or `Restricted`.
- The unit is a **folder**. The *trust root* of a solution or project file is the nearest ancestor
  directory containing `.git`; with none, the file's own directory. A root that is a filesystem root
  or the user's profile directory is never used (too broad): the file's own directory is used.
- A path is trusted if it equals or lies under a stored trusted root. The comparison is
  case-insensitive except on Linux.
- Unknown folders are **Restricted** by default. Choosing "Restricted Mode" is not persisted, so the
  next open asks again (same as VS Code).
- Trusted roots persist in `<config dir>/workspace-trust.json`:
  `{ "version": 1, "trusted": [ { "path", "trustedAt", "source" } ] }`. A corrupt file is treated as
  empty and is never silently overwritten with a partial state.
- Solutions created through the IDE's New Project flow are trusted (the user just authored them).

### Switches

| Switch | Effect |
|---|---|
| `OD_WORKSPACE_TRUST=0` | Feature off: everything is allowed, no prompt |
| `OD_WORKSPACE_TRUST=1` | Feature on even under `OD_TEST_MODE=1` (used by tests of the gates) |
| `OD_TEST_MODE=1` alone | Off, because nobody can answer the dialog in an integration run |

## 3. Capability matrix

| Capability (`TrustCapability`) | Restricted | Trusted |
|---|---|---|
| Edit text, syntax highlighting, file tree, find | yes | yes |
| Project evaluation (in-process, no tasks) | yes | yes |
| Lexical (syntax) highlighting from the editor's own highlighter | yes | yes |
| `SemanticAnalysis`: completion, diagnostics, quick info, navigation, outline, semantic colouring, OpenLens (every `ILanguageService`) | no | yes |
| `LoadAnalyzers`: third-party analyzers and generators | no | yes |
| `ExecuteBuild`: restore, build, clean, reference resolution, slnx migration | no | yes |
| `RunProgram`: run, debug, test | no | yes |
| `LoadProjectAssemblies`: every secondary view, i.e. the Design tab of all designers (WPF, WinForms, WinUI, Gtk), the XML tree and the translation view. Code view only | no | yes |
| `LaunchTerminal` | no | yes |
| Git status | yes, with `core.fsmonitor=false` forced for every repository | yes |

Restricted Mode is therefore a plain text editor with lexical colouring over a browsable solution.
Semantic analysis is off as a whole, not degraded: without `ExecuteBuild` there are no resolved
references, so a half-working language service would only fill the editor with false "unresolved
type" errors. The Restricted dialog and status message say so.

## 4. Architecture

```
Open solution --> SDProjectService.EstablishWorkspaceTrust(file)
                     |-- IWorkspaceTrustService.GetTrustRoot(file)
                     |-- trusted?  -> SetCurrentWorkspace(root)
                     `-- unknown   -> dialog: Trust folder / Trust parent / Restricted Mode
                                      -> Trust(root|parent) | nothing; SetCurrentWorkspace(root)

Every executing operation --> WorkspaceTrust.Demand(capability)  (static helper)
                              `-> IWorkspaceTrustService.IsAllowed(capability)
```

- `IWorkspaceTrustService` (`src/Main/Base/Project/Services/WorkspaceTrust/`) is registered in
  `ICSharpCode.SharpDevelop.addin` and reachable as `SD.GetService<IWorkspaceTrustService>()`.
- `WorkspaceTrust.IsAllowed/Demand` is the static entry the gates call. With no service registered
  (unit tests, tools) it allows everything, so only the IDE host enforces.
- Gates sit at the **lowest** point that executes, not at the menu command, so DevFlow actions, the
  designer's own builds and any future caller get the same answer:
  - `MinimalMSBuildEngine.CreateDotnetChildStartInfo` throws `WorkspaceNotTrustedException` (backstop
    for every child `dotnet` run), and `RunResolveReferences` and `ResolveAnalyzers` return empty first;
  - `BuildService.BuildAsync` returns a failed result with an explanatory error;
  - `LanguageServiceRegistry.TryGetService/TryGetProtocol` return nothing without `SemanticAnalysis`
    (`GetService` returns the no-op fallback), so no language service is resolved or started and the
    out-of-process Roslyn host is never contacted; `RegisterCSharpLanguageServiceCommand` also skips
    pushing reloaded projects. Callers take their existing "no language service" path;
  - `DisplayBindingService.AttachSubWindows` attaches no secondary view without
    `LoadProjectAssemblies`;
  - the snapshot builder drops analyzers, and its cache fingerprint includes the trust state so a
    restricted (reference-less) snapshot is never reused after trusting, and the reverse;
  - `DefaultProjectBehavior`, `WebBehavior`, `DebuggerCommands`: no `SD.Debugger.Start*`;
  - `MtpServerProcess.StartAsync` (test discovery and test runs both execute the test assembly)
    requires `RunProgram`;
  - `TerminalPaneModel` refuses to start a session;
  - `GitStatusService.RunGit` always passes `-c core.fsmonitor=false`.
- Granting trust **reopens the solution**: analyzers, references and snapshots are all derived from
  state at load time, so reloading is the one path that makes every consumer consistent.
- Revoking trust cannot unload an assembly already in the process. The UI says so and asks for a
  restart; the stored decision takes effect on the next open.

## 5. User interface

- Opening an unknown folder shows a dialog with three choices: **Trust this folder**, **Trust the
  parent folder** (hidden when the parent is too broad), **Restricted Mode**. Closing the dialog means
  Restricted.
- Restricted Mode is announced by a **"Restricted Mode" badge** in the status bar (`SDStatusBar`,
  button `WorkspaceTrustBadge`, orange, right side): it is visible exactly while a workspace is open,
  trust is enabled and the workspace is Restricted, follows `TrustChanged`, and a click opens
  Tools > Workspace Trust. The status message at open time and the Build output error when a build is
  refused say the same in words.
- `Tools > Workspace Trust...` opens `WorkspaceTrustWindow`, with or without a solution open. It shows
  the state of the open workspace and lists every stored trusted folder; **Revoke Selected** and
  **Revoke All** remove them (revoking a parent folder removes the trust of everything under it, which
  is why the list shows stored roots rather than the current workspace). From Restricted,
  **Trust Current Workspace...** offers the same choices as the open-time dialog and reloads the
  solution. Revoking trust of the open workspace cannot unload code already running, and the window
  says so.

### Localization

Every string comes from `data/resources/StringResources*.resx` under the keys `WorkspaceTrust.*` and
`XML.MainMenu.ToolMenu.WorkspaceTrust` (the menu label, referenced as `${res:...}`); `Global.Close`
is reused. English is the neutral file and Simplified (`zh-Hans`) and Traditional (`zh-Hant`) Chinese are
maintained beside it; other languages fall back to English. Code reads them through
`WorkspaceTrust.Text(key, english)`, which returns the English default when the resource service is not
running (unit tests, tools). Denial messages are one key per capability
(`WorkspaceTrust.Denied.<Capability>`), so a new `TrustCapability` needs its key in all three files;
`WorkspaceTrustResourcesTests` enforces that and the matching `{0}` placeholders.

## 6. Phases

| Phase | Scope | State |
|---|---|---|
| P0 | Service, persistence, dialog, gates listed in 4, git hardening, tests | this change |
| P0b | Code view only (no secondary views) and no semantic analysis | done |
| P1 | `.addin` workspace settings; T4 templates; an allow-list of harmless secondary views | planned |
| P1 | Restricted-mode project parsing that gives real references without running targets | planned |
| P2 | Analyzers loaded in an isolated host process so trust can be revoked live; organisation policy (`trustedRoots`/`untrustedRoots`); audit log | planned |

## 7. Known gaps and non-goals

- Restricted Mode is a guard against *accidental* execution on open, not a sandbox. A trusted folder
  can still run anything; a malicious IDE add-in is out of scope (add-ins have their own install
  review, see `addin-manager.md`).
- Symbolic links are compared by their path text. A trusted folder that contains a link to an
  untrusted location treats files reached through it as trusted.
- Closing the Design tab is not retroactive: documents already open when trust is revoked keep the
  views they have until the solution is reopened.
- Because every secondary view is dropped (not only designers), the XML tree and translation views
  are unavailable in Restricted Mode too; an allow-list can bring back the harmless ones later.
- External language servers (TypeScript, HTML, XAML) sit behind the same registry and are off in
  Restricted Mode as well, which also keeps workspace-configured server executables from starting.
- Source-link / `global.json` `msbuild-sdks` resolution during evaluation may download an SDK
  package; that is network access, not code execution, and is not gated.

- `AspNet.Mvc` (`WebBehavior`) carries the same `RunProgram` check, but that project is not part of
  `OpenDevelop.Mvp.slnx` and does not build today, so that edit is unverified.
- `ClassBrowser` "Debug this assembly" and the Debugger menu's "Start executable" run a file the
  user picked explicitly, not workspace code, and are not gated.

## 8. Tests

Verified end to end through DevFlow with `OD_TEST_MODE=1 OD_WORKSPACE_TRUST=1` (the dialog
auto-answers its cancel button, which is Restricted Mode): `od.open-solution` on
`tests/fixtures/SampleTestProject` opens, `od.build-solution` returns the Restricted Mode error and
starts no `dotnet`, test discovery logs the denial and the IDE stays up, and no trust file is written.
In the same session `od.completions`, `od.diagnostics` and `od.document-outline` on a `.cs` file answer
"no language service for file" and `od.wpf-designer.status` on a XAML file reports `active: false`.
Without `OD_WORKSPACE_TRUST` the same calls return 4358 completions and an active WPF designer.
The UI tree shows `WorkspaceTrustBadge` with a visible parent `StatusBarItem` in the Restricted run and a
collapsed parent when trust is off.

`tests/OpenDevelop.Base.Tests/WorkspaceTrustTests.cs` covers trust-root discovery, containment and
case rules, persistence round trip and corrupt files, the too-broad guard, the enable switches, the
`IsAllowed` matrix, `TrustChanged`, the language-service registry gate and the git fsmonitor guard. Run:

```bash
/usr/local/share/dotnet/dotnet test --project tests/OpenDevelop.Base.Tests/OpenDevelop.Base.Tests.csproj -c Debug -- --filter-class '*WorkspaceTrust*'
```
