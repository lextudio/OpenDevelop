# Fast mode: applying C# Dev Kit 11's performance work to OpenDevelop

Microsoft's [Faster, lighter C# Dev Kit](https://devblogs.microsoft.com/dotnet/faster-lighter-csharp-dev-kit/)
reports 22-180x faster solution loads and 80-85% less memory, from five techniques. This note maps
each technique onto OpenDevelop's actual code, says whether it transfers, and orders the work.

## Progress

**Step 0 (done): measurement.** `PerfTimeline` (`src/Main/Base/Project/Src/Services/PerfTimeline.cs`)
records milestones of the latest solution open and build; each is also logged as
`perf: scope/name +Nms`. `od.perf.timeline` returns them with the IDE's working set and managed heap.

Baseline, macOS arm64, Debug build, SDK `~/.dotnet` 10.0.201, restored projects:

| Solution | Projects evaluated (UI thread) | `SolutionOpened` handlers (UI thread) | Roslyn snapshots built | Pushed to RoslynHost |
|---|---|---|---|---|
| SolutionExplorerFixture, 1 project | 0.2 s | 0.3 s | 0.9 s | 1.6 s |
| SolutionExplorerFixture, not yet restored | 0.4 s | 0.6 s | 8.7 s | 9.9 s |
| OpenDevelop.Mvp.slnx, 87 projects | 12.0 s | 20.8 s | 83.3 s | **138.1 s** |

Times are cumulative from the start of the open. For OpenDevelop.Mvp the window is frozen for the
first 21 s, and the Roslyn snapshots alone take 62 s: one `dotnet msbuild -t:ResolveReferences`
child per project, serially (about 0.7 s each). That confirms step 2 below as the biggest lever.

A side finding: the same no-op `dotnet build` of one project takes 1.0 s with `~/.dotnet` 10.0.201
(the SDK the IDE selects here) and 0.5 s with `/usr/local/share/dotnet` 10.0.200, whatever the
environment variables. SDK choice matters as much as several of the steps below.

**Step 1 (done): restore once per build.** `BuildEngine` restores the open solution once before
starting any project, and every project whose global properties match that restore builds with
`--no-restore`; a project with different properties, or every project when the restore fails,
restores itself as before. It applies only when the build covers at least half the solution, since
restoring a whole solution for one small project is slower than that project's own restore.
`OD_BUILD_UPFRONT_RESTORE=0` turns it off. On a generated 12-project chain, a no-op build went from
16.1 s to 12.8 s (restore 1.5 s, all 12 projects skipping their own). The six build integration
tests pass.

**Step 2 (first part done): reference-resolution cache.** The per-project `dotnet msbuild
-t:ResolveReferences` child is now cached in `<solution>/.od/roslyn-reference-cache/`
(`ReferenceResolutionCache` in `LanguageServiceProjectSnapshot.cs`). The key is every input of that
evaluation: the selected SDK, the project file, each import the in-process evaluation read
(`MSBuildBasedProject.GetEvaluationInputFiles`), and `project.assets.json`. Files inside the solution
directory are keyed by content, files outside it (SDK, NuGet cache) by path, size and write time.
An empty resolution is never cached. On the generated 12-project chain:

| Open | Roslyn snapshots | All projects pushed | Cache |
|---|---|---|---|
| Cold | 11.6 s | 15.2 s | 12 misses |
| Warm | 0.6 s | 3.5 s | 12 hits |
| One project file touched, content unchanged | | | 12 hits |
| One project file edited | | | 1 miss, 11 hits |

The eleven C#/VB language-service integration tests (completion, go to definition, find references,
rename, OpenLens) pass.

Two findings from measuring OpenDevelop.Mvp, which is why it is not the benchmark for this step:

- 69 of its 87 projects fail reference resolution on every open, so they are never cached and reach
  Roslyn without resolved references. 66 fail with NETSDK1047 ("no target for
  `net10.0-windows/osx-arm64`") or MSB4018 (`ResolvePackageAssets` crashing). The likely cause, seen
  in the sampled errors but not checked project by project: this repository restores with
  `-p:ProGpuWpfUseCurrentRuntimeIdentifier=false`, the resolution child does not pass it, and the
  LibreWPF SDK then picks a RID the assets file has no target for. An ordinary solution does not
  hit it.
- The other 3 declared `TargetFrameworks` (plural) with one entry and failed with MSB4057, because
  the unpinned evaluation is the outer build. Fixed: the child now pins `TargetFramework`
  (`MinimalMSBuildEngine.InnerBuildTargetFramework`).

**Step 3 (first part done): the `SolutionOpened` freeze.** `OnSolutionOpened` now records every
step slower than 50 ms, and `SlnxSolutionLoader` every project slower than 200 ms to load, as
`slow-step`/`slow-project-load` milestones. On OpenDevelop.Mvp they showed, with `dotnet-stack`
samples of the UI thread naming the culprits:

| UI-thread time after the projects are evaluated | Before | After |
|---|---|---|
| CodeCoverage's `SolutionOpened` subscriber | ~5.5 s | 0 (thread pool) |
| Unit Tests pad counting discovered tests | ~3.4 s | spread over dispatcher turns |
| All `SolutionOpened` handlers | 8.8 s | 1.4 s |

- CodeCoverage located every project's coverage file on the UI thread, and the path needs the
  output directory for the active configuration: a full MSBuild re-evaluation per project, almost
  all with no coverage at all. `CodeCoverageService.SolutionLoaded` now looks results up on the
  thread pool and shows them on the UI thread if the same solution is still open.
- The Unit Tests pad initialized every test project's tree (an MSBuild evaluation each) in one go
  to show "Total: N". `UnitTestsPad.CountDiscoveredTestsAsync` counts one project per dispatcher
  turn and publishes the total unless a test run has started meanwhile (`RunVersion`).
  `UnitTestPad_ShowsDiscoveredTotalInStatusBar` now waits for a non-zero total instead of reading
  the first one; the 16 unit-testing and coverage integration tests pass.

**Step 3 (second part done): project loading itself.** Moving project creation off the UI thread
is not possible as things stand: `AbstractProject.FileName` and `ProjectChangeWatcher` assert the
main thread, and pumping the dispatcher between projects is unsafe under LibreWPF, whose native
input is delivered synchronously by `host.DoEvents()` during a nested frame (reentrancy). Sampling
the UI thread during a warm open showed half of the samples in `ProjectChangeWatcher.SetWatcher`,
not in MSBuild: starting a `FileSystemWatcher` is synchronous and slow on macOS (one FSEvents
stream each). Two changes, both scoped to `OpenSolutionInternal` and each with an off switch:

- `ProjectChangeWatcher.DeferEnabling()` (`OD_DEFER_PROJECT_WATCHERS=0` disables): watchers created
  while a solution loads start afterwards, one per idle dispatcher turn. A watcher compares the
  write time recorded at creation when it starts, so a project file changed in the meantime is still
  reported; verified with edits made immediately after the open returned and a few seconds later.
  A watcher whose directory is gone by then (a deleted temporary copy) is dropped instead of
  throwing `DirectoryNotFoundException`, which the first version did.
- `MSBuildInternals.BeginSharedEvaluation()` (`OD_SHARED_EVALUATION=0` disables): one shared MSBuild
  `EvaluationContext` for the load, caching file-system lookups, globs and SDK resolution between
  projects. It helps the first open of a session only (warm opens already hit MSBuild's caches).

OpenDevelop.Mvp, 87 projects, time until the `SolutionOpened` handlers are done (the window is
frozen until then):

| | Baseline | Now |
|---|---|---|
| First open in a session | 20.8 s | 8.8 s |
| Later opens | 11.4 s (after step 3's first part) | 5.2 s |

Regression runs: 23 solution/project tests and 16 build, language-service, unit-test and coverage
tests. Three of the 23 (`OpenSlnx_AddReference_ProjectAndBrowsedAssembly` and two that fail only
after it) fail because of DevFlow 0.3.0, not this work: with every change here in place but the
agent pinned back to 0.2.13, `OpenSlnx_AddReference_ProjectAndBrowsedAssembly` passes; with 0.3.0
the agent rejects the test's `ui/tap` with `ui-mutation-busy` while the modal `od.menu.invoke` that
opened the dialog is still running, and the dialog stays open for the tests that follow.

Still open in step 2: making the cache committable (keys still contain absolute paths), and
covering the multi-target path's `TfmEvaluationCache`, which keeps its timestamp key.

## The five techniques, and where OpenDevelop stands

| C# Dev Kit 11 | OpenDevelop today | Transfers? |
|---|---|---|
| Persistent project cache: first load is evaluated, later loads come from the cache, and the cache can be committed | Partial. `TfmEvaluationCache` (`.od/roslyn-tfm-cache`) covers **multi-target projects only**, is keyed by absolute path and timestamps, and is gitignored. Single-target projects run an out-of-process `dotnet msbuild -t:ResolveReferences` on **every** load. | **Yes. Biggest win.** |
| Active file first, rest asynchronous | No. `SlnxSolutionLoader.ReadSolution` evaluates projects serially, on the UI thread, in solution order. The Roslyn push follows `solution.Projects` order. | **Yes.** |
| Fast up-to-date check, build acceleration | No. `MinimalMSBuildEngine.BuildAsync` starts `dotnet build` per project, with restore every time. `BuildModifiedProjectsOnlyService` only remembers "unmodified since last build" in memory. | **Yes.** |
| Six processes consolidated into one Native AOT process | Not comparable. OpenDevelop's children exist for isolation (designers, per-runtime XAML servers) and are already lazy and pooled. | **Partly.** Startup of the hot children, not merging them. |
| Compact in-memory project model | Unknown. Not measured. | Measure first. |

## 0. Measure before changing anything

The blog's gains are relative to a baseline; we have none. Add timing to the paths below, write
it to the app log, and record a baseline on two fixtures: a small solution
(`tests/fixtures/SolutionExplorerFixture`) and a large one (OpenDevelop.Mvp.slnx itself, ~150
projects).

- **Solution open → Solution Explorer populated**: `ProjectService.OpenSolutionInternal`
  (`src/Main/SharpDevelop/Project/ProjectService.cs:183`).
- **Solution open → first C# document has semantic services** (completion returns a result):
  `RegisterCSharpLanguageServiceCommand.PushSolutionAsync`
  (`src/AddIns/BackendBindings/CSharpBinding/Project/Src/RegisterCSharpLanguageServiceCommand.cs:51-91`).
- **Solution open → all projects pushed** to the Roslyn host.
- **No-op build** and **one-file-change build** of the whole solution.
- **Working set** of the IDE and of `RoslynHost` after full load.

Expose them through a DevFlow action (e.g. `od.perf.last-load`) so an integration test can
assert a ceiling and catch regressions. Without step 0, nothing below can be shown to have helped.

## 1. Project cache for every project (largest expected win)

**Why it applies.** Dev Kit's headline numbers come from not re-evaluating projects that have not
changed. OpenDevelop pays the most expensive part, reference resolution, on every load for every
single-target project: `LanguageServiceProjectSnapshot.FromProject` →
`ResolveReferencePaths` → `MinimalMSBuildEngine.ResolveAssemblyReferences`
(`src/Main/SharpDevelop/Project/Build/MinimalMSBuildEngine.cs:229-302`), one `dotnet msbuild`
child process per project, with a 120 s timeout. Before it moved to `Task.Run`, this froze the UI for
about 9 s per project (comment at `RegisterCSharpLanguageServiceCommand.cs:66-79`).

**How.** Extend the existing `TfmEvaluationCache`
(`src/Main/Base/Project/Src/LanguageServices/LanguageServiceProjectSnapshot.cs:332-500`) rather
than add a second cache:

1. Cover single-target projects. Cache the `ResolveReferences` result together with the items
   the snapshot already stores (documents, references, project references, defines, language
   version, nullable, analyzers).
2. Key entries by **input content, not timestamps**. Hash the project file, every imported
   `.props`/`.targets` reported by the evaluation (`Project.Imports`; today imports outside the
   project directory are not tracked, `:328-330`), `obj/project.assets.json` (restore output,
   which is what actually changes references), the SDK version and the target framework.
   `roslyn-host-process.md` §7b already asks for checksum keys.
3. Store paths relative to the solution directory, so the cache survives a move, a new worktree,
   and a commit.
4. Make the cache optionally committable. A `.od/project-cache/` that a repository may check in
   gives a fresh clone instant semantic services, which is Dev Kit's argument for agent workflows.
   Keep it gitignored by default and document how to opt in.
5. Revalidate in the background. Serve from the cache immediately, recompute after load, and push
   a corrected snapshot only when the hash differs.

**Risk.** A stale cache gives wrong diagnostics, not a crash. The hashed import list and
`project.assets.json` are what keep this from going stale silently; do not fall back to timestamps.

## 2. Active document first, everything else asynchronous

**Why it applies.** Dev Kit reports the active file ready in about 0.5 s regardless of solution size,
because it loads that project first. OpenDevelop does the opposite: projects are evaluated serially
on the UI thread (`SlnxSolutionLoader.ReadSolution`, `src/Main/SharpDevelop/Project/SlnxSolutionLoader.cs:133-145`,
behind the global `MSBuildInternals.SolutionProjectCollectionLock`), and only then are they pushed
to the Roslyn host one at a time in solution order.

**How.**

1. Split solution open into two phases. Phase one parses the `.slnx` and shows every project node
   in Solution Explorer straight away (name, path, type) without evaluating it. Phase two evaluates
   projects off the UI thread.
2. Order phase two: first the projects of documents being restored by the layout
   (`LayoutSnapshotConverter.ReopenDocuments`, `src/Main/SharpDevelop/Workbench/LayoutSnapshot.cs:161-180`),
   then their project references, then everything else.
3. Do the same for the Roslyn push. `RemoteLanguageService` already has an on-demand loader for a
   document's own project (`RegisterCSharpLanguageServiceCommand.cs:34-38`); make that path take
   priority over the bulk push and skip that project when the bulk push reaches it.
4. Once step 1 exists, phase two for an unchanged project is a cache read, so step 2's ordering
   matters most on a cold first open.

**Risk.** The UI-thread assumption in the project model. Evaluation is serialized by a global lock
today; moving it off the UI thread keeps that lock and only stops it from freezing the window.
Parallel evaluation is a separate, later question.

## 3. Builds: skip what is up to date, stop restoring every time

**Why it applies.** Dev Kit's no-op build of 407 projects went from 34.4 s to 0.88 s. OpenDevelop's
`MinimalMSBuildEngine.BuildAsync` (`:304-397`) starts a fresh `dotnet build` per project, at most four
at a time (`BuildOptions.DefaultParallelProjectCount`), **with NuGet restore every time** (no
`--no-restore`). A no-op build therefore still pays for process start-up, a restore and a full
MSBuild evaluation per project.

**How, in increasing order of effort.**

1. **Restore once per build, not per project.** Run one `dotnet restore` on the solution, then
   build each project with `--no-restore`. Cheap and safe.
2. **A fast up-to-date check** before starting a project's `dotnet build`, modeled on
   Visual Studio's (it is what Dev Kit borrowed). Record, after a successful build, the hashes or
   sizes and times of the project's inputs (Compile, EmbeddedResource, None with
   CopyToOutputDirectory, the project file and its imports, `project.assets.json`, referenced
   project outputs) and its primary output. If nothing changed, skip the process entirely. Persist
   it under `.od/` so it survives restarts, which `BuildModifiedProjectsOnlyService` does not.
   Always provide a way out: Rebuild bypasses it.
3. **Build acceleration** (VS's `AccelerateBuildsInVisualStudio`): when a referenced project's
   change does not alter its reference assembly, copy its outputs instead of rebuilding dependents.
   This needs `ProduceReferenceAssembly`, which SDK projects enable by default.
4. **Fewer processes.** One `dotnet build` of a generated traversal or `.slnx` filter with
   `-graph`, instead of one process per project. This gives up the per-project progress parsing
   the current regex does, so do it last, and only if 1-3 are not enough.

## 4. Process start-up: ReadyToRun, not one process

**Does consolidation transfer?** Mostly no. Dev Kit merged processes that only existed for
historical reasons. OpenDevelop's children exist on purpose: designers must survive a crash in user
code, each XAML runtime needs its own assembly world, and they are already lazy and pooled
(`SharedDesignerHostPool`, `src/Main/Designer/Designer.Remote/SharedDesignerHostPool.cs`). Merging
them would undo that isolation.

**What does transfer is start-up cost.** No project in the repo sets `PublishReadyToRun`,
`PublishAot` or `TieredPGO`. The children that start on the critical path are worth precompiling:

1. **RoslynHost** starts on solution open and is on the path to "first completion". Publish it
   ReadyToRun. Full Native AOT is not realistic: Roslyn and its analyzers rely on reflection
   and dynamic loading.
2. **The XAML language servers**: one per runtime **per project**
   (`LspServiceManager` keys by workspace root). On a solution with several XAML projects that is
   several identical cold starts. Two fixes: ReadyToRun, and one server per runtime serving several
   workspace roots (LSP supports multiple workspace folders).
3. The IDE itself: ReadyToRun for the host and the always-loaded assemblies. Measure the startup
   delta, since it costs package size.
4. **Startup work that is not needed yet**: about 23 add-ins run `/SharpDevelop/Autostart`
   commands synchronously, and AddInManager2 downloads its package list on start-up when update
   checks are on (`src/AddIns/Misc/AddInManager2/Project/Src/Commands.cs:46-55`). Move anything
   that is not required to show the window to after the workbench is idle.

## 5. Memory

Dev Kit's 80-85% reduction came largely from the process consolidation and a compact project
representation. Neither has a known counterpart here until step 0 measures the IDE and RoslynHost
working sets. Likely candidates once measured: the per-target-framework `ProjectCollection`
created by `TryEvaluateForTargetFramework` (`LanguageServiceProjectSnapshot.cs:151`) and kept
`ProjectRootElement` instances in the per-solution collection.

## Not applicable or already done

- **MSBuild file editing, package completions, "C# Doctor"**: features, not performance work.
- **Lazy child processes**: already the case for every designer and language server.
- **MEF start-up**: `OpenDevelopMefHost` is lazy and scans one assembly; it is not a cost.

## Suggested order

| # | Work | Why this position |
|---|---|---|
| 0 | Timing and a DevFlow perf action | Nothing else can be judged without it |
| 1 | Restore once, `--no-restore` per project | One-line class of change, immediate build win |
| 2 | Content-keyed project cache for all projects | Removes the per-load `dotnet msbuild` per project |
| 3 | Two-phase solution open, active document first | Turns the cache into "first file ready at once" |
| 4 | Persistent fast up-to-date check | The no-op build case |
| 5 | ReadyToRun RoslynHost and XAML servers; shared XAML server | Cold-start cost of the children |
| 6 | Defer non-essential Autostart work | Window shows sooner |
| 7 | Build acceleration, graph build | Only if 1 and 4 are not enough |
