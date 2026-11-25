# XAML Source Generator (XSG) Hot Reload

> **Syntax update (2026-11-25, local, uncommitted).** The generator no longer reads `{cs: ...}`; it follows .NET MAUI's
> expression syntax (`{= Foo}`, a bare `{Foo}`, single-quoted strings, event lambdas). Every `{cs:}` below means such an
> expression, and `{cs: "x"}` is now `{= 'x'}`. See `xsg-maui-alignment-plan.md` and `xsg-gaps.md`.

Status: research. Applies to LibreWPF's optional `LibreWPF.Xaml.SourceGenerator` path
(`UseLibreWpfXamlSourceGenerator=true` / `LIBREWPF_USE_XSG=1`).

## Background

LibreWPF now ships a Roslyn XAML Source Generator that runs in parallel with the
classic BAML/`PresentationBuildTasks` path. It generates `InitializeComponent()`
as C# code, including typed `x:Name` fields, `{cs: …}` expressions, and
Simpler XAML (implicit namespaces). See `openavalon/LibreWPF/Documentation/XamlSourceGeneratorUsage.md`.

## Two delivery routes

OpenDevelop's `WpfHotReload.Runtime` agent pushes XAML **text** to the running app.
The agent escalates through levels:

| Level | Mechanism | Works with XSG output? |
|---|---|---|
| 0 | Property patch (individual property changes) | yes |
| 1/3 | Re-parse XAML with `XamlReader`, merge into live tree | **no for `{cs:}`** — `System.Xaml` rejects `cs:` ("Invalid XAML type name") |
| 2 | Resource dictionary reload | yes |
| 4 | Restart required | yes |

The agent also calls a convention hook `__WXSG_ApplyHotReload(string path, string xaml)`
on the live root when present.

## Route D: metadata delta

.NET's `MetadataUpdateHandler` + `dotnet watch` can apply C# deltas without restarting.
Experiment (net11 console app, stand-in generator, `DOTNET_MODIFIABLE_ASSEMBLIES=debug`):

- An `AdditionalFiles` XAML edit regenerates `*.g.cs` and the delta is applied in 30–477 ms.
- `{cs: …}` lambda-body edits apply (the gap Route T cannot close).
- Adding an `x:Name` (new field on the live instance) applies.
- **Removing** an `x:Name` is a rude edit → restart required.
- An edit that makes the generated C# invalid leaves the app on the last good state and
  auto-resumes when fixed.
- Compile errors are reported against `View.g.cs(8,21)`, not the XAML line — needs `#line`
  directives (now emitted by the generator).

## Constraints

- Route D uses an OpenDevelop producer. SharpDbg E2 has proved debugger-side `ApplyChanges`; the
  remaining work is lifecycle wiring and the two consumers, not shared ownership by `dotnet watch`.
- `sharpdbg-enc.md` tracks the public-API producer (`Compilation.EmitDifference`) feeding either the
  agent (`MetadataUpdater.ApplyUpdate`, no debugger) or SharpDbg (`ICorDebugModule2::ApplyChanges`).
- LibreWPF has no `[MetadataUpdateHandler]` yet; generated classes should grow one
  (register live instances in a weak registry, re-apply on `UpdateApplication`).
- The `WpfHotReload.Runtime` agent's `Initialize` must detect it is inside the target
  app (a WPF `Application` type is loadable) and do nothing in `dotnet watch`/build
  host processes, because `DOTNET_STARTUP_HOOKS` is inherited.

## AXSG prior art

The closest design (wieslawsoltes/XamlToCSharpGenerator) uses:
- idempotent generated graph (clear-before-add, `-=` before `+=`)
- weak instance registry keyed by type
- `[MetadataUpdateHandler]` re-applying the graph to live instances
- replacement-type mapping via `MetadataUpdateOriginalTypeAttribute`
- serialized update queue, phased handlers, state capture/restore
- last-known-good output on invalid XAML
- convergence invariant: apply → revert == baseline

MAUI's framework-owned handler is still an open proposal (dotnet/maui#35278).

## What is already done in LibreWPF

- Generator is idempotent (Clear-before-Add, event `-=`/`+=`).
- `SetResourceReference` for DynamicResource (propagates edits).
- `#line` directives map generated code to the XAML file.
- XamlXmlReader + MarkupCompiler implicit-namespace injection.
- `__WXSG_ApplyHotReload`-style hook can be added to generated classes.

## What remains

- `[MetadataUpdateHandler]` + weak registry + re-apply on the XSG path.
- Classification of edits: fast path (text push) vs slow path (delta) per edit content.
- `dotnet watch` end-to-end test on a real LibreWPF app with a live window.
- OpenDevelop's own delta producer and appliers: planned in `sharpdbg-enc.md` (E0/E1/E3a/E3b/E4). SharpDbg
  already applies a method-body delta under the debugger (E2, `sharpdbg` e0407f8, local).
- Convergence tests (apply→revert == baseline).

## End-to-end plan (OpenDevelop)

Route T stays the fast path (unsaved buffer, no build). Route D is the slow path, only for
`{cs:}`, event or `x:Name` changes. Phases are ordered by what blocks the next one.

| Phase | Work | Where | Status |
|---|---|---|---|
| 0a | Can `dotnet watch` and the OpenDevelop debugger own one process? | superseded: no; the plan replaces `dotnet watch` with an OpenDevelop producer plus agent (no debugger) or SharpDbg (debugger), see `sharpdbg-enc.md` | closed |
| 0b | A real LibreWPF app with a live window under XSG, Route D | OpenDevelop + openavalon | **done locally** with the OpenDevelop producer instead of `dotnet watch`: `XsgHotReloadEndToEndTests` (see below) |
| 1 | Generator hot-reload ready in Debug: typed `x:Name` fields, event wiring, DynamicResource via `SetResourceReference`, `[MetadataUpdateHandler]` + weak registry | openavalon | done; typed fields, events, `SetResourceReference`, idempotence, `#line` were already in `f6cf29056` |
| 2 | `HotReloadWorkflow.Classify` from a real XAML parse diff: Route T / `BuildRequired` (`{cs:}`, events) / `RestartRequired` (`x:Name` removed, `x:Class` changed) | OpenDevelop | done, and only for projects that opted in to XSG. The IDE asks the project's evaluated `UseLibreWpfXamlSourceGenerator` (`MSBuildBasedProject.GetEvaluatedProperty`), so Directory.Build.props, imported targets and the environment switch count as they do for the build; without an evaluated value it falls back to the project file and `LIBREWPF_USE_XSG`. BAML and WinUI keep the text path. Event-handler edits are recognised when the value is a bare identifier that the x:Class code-behind (`View.xaml.cs`, the editor's text if open) declares as a `void` method, so any event name counts; without code-behind, a name heuristic (`Preview*`, `*Changed`, `*Click`, `Loaded`...) is the fallback. Over-recognising only costs a rebuild |
| 3 | Agent: only start in the target app; atomic apply, last-known-good, serialized queue | WpfHotReload.Runtime | target-process guard published (`9f5f214`). Serialized queue already holds: the pipe server has one instance and handles each request synchronously on the dispatcher. Last-known-good: published (`be2ba81`, `0.1.0-ci.2`). A merge that throws re-applies the last text that applied, or the IDE's `previousXamlText` before any; covered by `WpfHotReloadEndToEndTests.FailedMerge_RestoresTheLastAppliedState` through the test-only `WPF_HOTRELOAD_TEST_FAULT_AFTER_MERGE` marker. A restore that itself fails half-way falls back to replacing the root's content with a fresh tree of the last good text in one assignment (control state is reset, the edit reports `Failed`, no restart); only a root that is not a content control or decorator, or a failing replacement, reports a restart. Covered by `WpfHotReloadEndToEndTests.FailedRestore_ReplacesTheContentAtomically` (test marker `WPF_HOTRELOAD_TEST_FAULT_RESTORE_MERGE`); needs `WpfHotReload.Runtime` 0.1.0-ci.3 or newer (commit `585cf12`), which OpenDevelop pins |
| 5 | Route D without `dotnet watch`: delta producer (E0), agent `applyDelta` (E1), SharpDbg `sharpdbg/applyDeltas` (E3a), debug-state follow-up (E3b) | OpenDevelop + sharpdbg | E2 done; E0 done locally (host producer, launch DLL/PDB capture, EnC PDB maps); schedule in `sharpdbg-enc.md` |
| 4 | Convergence tests (apply, revert, compare) and an XSG journey beside `WpfHotReloadJourneyTests` | OpenDevelop | Base-level XSG journey done (`XsgHotReloadEndToEndTests`), including convergence for Route D (edit, second edit, revert reaches the launch text, agent and SharpDbg) and Route T rollback; IDE (DevFlow) journey done locally (`XsgProject_CsExpressionEdit_AppliesAsACodeDelta`) |

Phase 1 delivery: under `#if DEBUG` each generated class carries a nested `__XsgHotReload` registered
with `[assembly: MetadataUpdateHandler]`. `InitializeComponent` registers `this` (weakly, once);
`UpdateApplication` re-runs `InitializeComponent` on each live instance through its own dispatcher when
the updated types are null or include the class. Verified by behaviour in `openavalon/LibreWPF/src/LibreWPF.Xaml.SourceGenerator.Tests/HotReloadRegistryTests.cs`
(generated code compiled against WPF stand-ins and run: a collected window is pruned and not re-initialized, a live one
is, an unrelated type is a no-op, a rerun does not register twice) and against real LibreWPF with real deltas by
`XsgHotReloadEndToEndTests`.

Deliberately **no** `__WXSG_ApplyHotReload`. The agent treats a `false` return as a final error and
does not fall back, and a `true` return would claim a text edit was applied when only the compiled graph
re-ran. Without the method the agent keeps its XamlReader path (Route T) for XSG classes; Route D is
the handler's job.

Generator issue fixed locally in `openavalon/LibreWPF`: element locals now use an incrementing counter
(`__<Type>_<n>`) in XAML traversal order, rather than a GUID. Rebuilding the minimal source-generator
fixture no longer creates an artificial whole-method edit just because generation ran again.

Phase 0b prerequisite, now met: the generator is published with the LibreWPF closure as
`LibreWPF.Xaml.SourceGenerator 0.1.0-preview.69.60.1`, and `tests/fixtures/XsgLocalFeedFixture` (a code-only `WinExe`
compiled only by the generator, BAML task disabled) restores it; the real-window journeys built on it are described
under "Route D end to end" and "IDE journey" below. (`RealXamlCompilerHarness` exercises BAML features outside the
generator's supported subset, so it is not a valid minimal XSG fixture.)

Phase 3 guard: `StartupHook.Initialize` returns unless `PresentationFramework` resolves, so inherited
`DOTNET_STARTUP_HOOKS` in `dotnet watch` and build hosts no longer start a listener on the app's pipe.
Not yet exercised against a real `dotnet watch` run (phase 0b). First published in `WpfHotReload.Runtime 0.1.0-ci.2`; OpenDevelop now pins
`0.1.0-ci.3` (see `sharpdbg-enc.md`, Status).

## Route D end to end (2026-11-22, local)

`tests/OpenDevelop.Base.Tests/XsgHotReloadEndToEndTests.cs` runs `tests/fixtures/XsgLocalFeedFixture` (now a code-only
`WinExe`) under the agent, loads its real compiler inputs into the Roslyn host (via `dotnet msbuild -getItem`), captures
the launch DLL/PDB, edits `{cs: "Hot reload ready"}` in the unsaved XAML text and asserts the live `Status.Text` becomes
the new value. Passing on macOS arm64. What it took:

- **Host had no XAML.** `LanguageServiceProjectSnapshot` now carries `AdditionalFileNames` (both evaluation paths and
  the `.od` cache; an old cache entry without them is re-evaluated). The host keeps them as AdditionalDocuments, so its
  compilation runs the generator.
- **The XAML buffer never reached the host.** `HotReloadDeltaRequest.AdditionalTexts` carries it; the producer applies
  it to the AdditionalDocument before compiling. `AgentPipeHotReloadSession` sends it for a `.xaml` change.
- **Generator: `x:Name` was only a field.** It now also sets `FrameworkElement.Name`/`FrameworkContentElement.Name` and
  registers the name in the root `NameScope` (unregistering first, since Hot Reload reruns `InitializeComponent`), as
  BAML does. Without it `FindName`, `ElementName` bindings and the agent's lookup by name fail. Unit tests:
  `openavalon/LibreWPF/src/LibreWPF.Xaml.SourceGenerator.Tests` (5).
- **Agent:** query `<Name>.<Property>` reads any public property of a named element (was a fixed list).

`EventHandlerEdit_ReachesTheLiveWindowThroughACodeDelta` does the same for an event: the fixture's root `Grid` names `OnLoadedA`, `Loaded` fires again for each tree the rerun builds, and switching the attribute to `OnLoadedB` shows in `Marker.Text` (then reverts to `handler A`). The generated `InitializeComponent` builds a fresh element tree each run, so an old handler does not stay attached to the live one.

The same test runs under SharpDbg (`CsExpressionEdit_UnderSharpDbg_ReachesTheLiveWindow`, E3a in `sharpdbg-enc.md`).

The test skips where the fixture is not built, and CI does not build it. That is deliberate for now: putting the fixture into a plan entry's `additionalProjects` (for example `base-tests` on `macos-arm64`) puts it into the workspace archive every lane unpacks, which would also start `WpfHotReloadJourneyTests.XsgProject_CsExpressionEdit_AppliesAsACodeDelta` in `wpf-hot-reload-journeys` (`required` on Windows x64 and macOS ARM64), and the SharpDbg variants `Assert.True` that the debugger add-in is deployed instead of skipping. Neither has run in CI. Enable it as its own observed step (a separate entry, journey held back) before promoting anything.

## IDE journey (2026-11-22, local)

`WpfHotReloadJourneyTests.XsgProject_CsExpressionEdit_AppliesAsACodeDelta` drives the edit through the IDE (open the
fixture, Start Hot Reload, edit the unsaved buffer, Apply) and passes. Getting there fixed:

- **The first Apply of a session was never classified.** With no accepted text yet, the command used the editor
  buffer as the "previous" text, so previous == current and `Classify` returned `FullDocument`; a `{cs:}` edit went
  out as text. It now falls back to the file on disk (what the launch build compiled).
- **The agent's XML fallback set markup-extension text as a literal** (`Status.Text` became `{cs: "..." + "..."}`).
  Attribute values starting with `{` (other than the `{}` escape) are skipped.
- **The IDE's Roslyn host compiled every SDK project without its global usings and without package analyzers or
  source generators** (also the targeting pack's Regex/LibraryImport generators). Snapshots now carry
  `GlobalUsings` from the evaluated `Using` items (the host adds them as an in-memory `obj/OpenDevelop.GlobalUsings.g.cs`),
  and the `ResolveReferences` run that already resolves references also returns the `Analyzer` items (single project:
  `-getItem:Analyzer`; batch: an injected `ODResolveAnalyzers` target through `CustomAfterMicrosoftCommonTargets`,
  importing the project's own value first). They are cached with the references (an older cache entry is resolved
  again once). The SDK's code-quality analyzers (`Sdks/Microsoft.NET.Sdk/analyzers`) are left out: diagnostics only,
  and the costly part. Tests: `ReferenceResolutionBatchTests` (real MSBuild collects the Regex generator),
  `RoslynHostProcessTests.GlobalUsings_FromTheSnapshot_AreInTheProjectsCompilation`.
- **The launch baseline was the apphost**, a native executable ("PE image doesn't contain managed metadata"). The
  baseline is now the managed assembly beside it (`HotReloadLaunchBaseline.ManagedModuleFor`).
- **Baseline failures were only in the log file**; Apply then said "No launch baseline exists". They now go to the
  Hot Reload output channel ("Code changes cannot be applied in this session: ...").

Still passing after these changes: both existing WPF Hot Reload journeys, `ProjectReloadTests.ExternalProjectFileChange_*`,
`AddInTests.OpenLens_*`, and 131 related Base tests. Solution-open cost, OpenDevelop.Mvp.slnx (89 projects), macOS arm64 Debug, wall time from `od.open-solution` to
the `roslyn-projects-pushed` milestone; "cold" deletes the solution's `.od` caches, "warm" reopens in the same
process; `OD_RESOLVE_ANALYZERS=0` turns the collection off:

| Analyzers | Cold (s) | Warm (s) |
|---|---|---|
| off | 41.2, 49.0, 35.2 | 15.5, 14.0, 13.5 |
| on | 38.8, 40.1, 48.7, 37.3, 38.2 | 14.1, 14.0, 16.8, 14.1, 13.5 |

No difference above the run-to-run noise. All 89 projects resolved analyzers (1,316 paths in total; the Regex,
LibraryImport, System.Text.Json, logging and MessagePack generators among them). The first two "on" runs still
loaded the SDK's CA analyzers: MSBuild reports them as `.../Sdks/Microsoft.NET.Sdk/targets/../analyzers/...`, which
the filter only matched after normalising the path. The milestone covers loading projects into the host, not running
the generators: those run lazily, when a compilation is first needed.

## Local iteration

Everything is published and pinned: `WpfHotReload.Runtime 0.1.0-ci.3`, `LeXtudio.SharpDbg.* 1.0.0-lextudio.45102390477.1`,
and the LibreWPF closure `0.1.0-preview.69.60.1` (openavalon run 45075138396), which now also publishes
`LibreWPF.Xaml.SourceGenerator` (openavalon `71068ac`); `sync-openavalon-feed.ps1` fetches it with the closure and
`tests/fixtures/XsgLocalFeedFixture` references that version. CI does not build the fixture yet (see above), so the XSG tests skip there; build it (`dotnet build tests/fixtures/XsgLocalFeedFixture -c Debug`) to run them.

To iterate on an unpublished package, pack it with a new unique version into `artifacts/openavalon-feed`, move the pin
locally, and bump the version on every repack (NuGet caches a version forever). `sync-openavalon-feed.ps1` refuses a
pin that GitHub Packages does not have; write `<package>.nupkg.verified` (two lines: id, version) beside a local package
so the sync skips it and still fetches everything else. SharpDbg's `iac/pack-opendevelop-cli.ps1` must run under
`pwsh -NoProfile` with `/usr/local/share/dotnet` first on `PATH`, because the profile puts Homebrew's dotnet first.

## Open questions

1. ~~Can `dotnet watch` and OpenDevelop's debugger both own the process?~~ Not needed: SharpDbg can apply deltas itself (verified on macOS arm64, method-body edit). Breakpoint/PDB fidelity after an edit is verified for updated methods (portable-PDB overlay, source-breakpoint rebind) and an active frame is remapped at an unchanged statement (E3b); still open: the stepping state when a step is in progress as the delta lands.
2. ~~Should the generator emit a `MetadataUpdateHandler` by default in Debug, or behind a flag?~~ By default, under `#if DEBUG`; Release output has none.
3. ~~How does the agent coexist with a delta producer?~~ The classification picks one per edit: text push for what a re-parse carries, a code delta for `{cs:}`, events and new names; without a producer a code delta reports `BuildRequired`.
