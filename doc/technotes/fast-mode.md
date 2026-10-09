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

**Roslyn push and snapshots.** After a solution opens, the snapshots are built and pushed to
RoslynHost; on OpenDevelop.Mvp that took 138 s end to end.

- Push, 46-55 s → 9 s. Per-project timing showed an even ~0.5 s per project, and sampling RoslynHost
  showed it idle most of that time. `PushSolutionAsync` resumed after every project on the UI thread
  (deliberately, "as before"), so each one queued behind whatever the dispatcher was doing right
  after the open. The loop now runs on the thread pool; it touches no UI, and
  `RemoteLanguageService` already serializes its updates under a lock. Async continuations do not
  appear in stack samples, which is why measuring each push was needed to find this.
- Snapshots, 52-57 s → 19-21 s. `LanguageServiceProjectSnapshotFactory.FromSolution` processes up to
  four projects at once, ordered: the expensive part is the independent `dotnet msbuild
  -t:ResolveReferences` child per project, while in-process evaluation still serializes on
  `MSBuildInternals`' lock.

Open until every project is pushed: 138 s → 48-54 s. The push now takes 18-25 s rather than 9 s;
the likely reason, not yet proven, is that more projects reach it with resolved references (reference
cache hits went from 16 to 32), so RoslynHost loads more metadata. 23 language-service, build,
unit-test, coverage and solution tests pass.

**More from C# Dev Kit: warm start, active document first, a "doctor".**

- Warm start (`OD_WARM_START=0` disables). `FromSolution`'s result is cached per solution in
  `.od/roslyn-solution-snapshots/`. On the next open, if the cached project set equals the current
  one, the cached snapshots are pushed first; the fresh ones are then built and only those that
  differ (compared as JSON) are pushed. A changed project set skips the warm start, because the host
  cannot remove a project and closing its workspace would drop unsaved buffers. Building the fresh
  snapshots alongside the cached push was tried and reverted: it doubled the cached push
  (20.6 s → 40.6 s), and being usable early is the point. Verified on the 12-project chain: an added
  `.cs` file re-pushes exactly that project, an added project skips the warm start, an unchanged
  solution re-pushes nothing.

  | OpenDevelop.Mvp | Cold | Warm |
  |---|---|---|
  | Every project usable in RoslynHost | 47.5 s | 20.6 s |

- Active document first. Before pushing, snapshots are reordered: the projects of open documents
  (the active one first) and everything they transitively reference, then the rest in solution
  order. With one CSharpBinding file open, its 11 snapshots were ready at 32.3 s against 65.3 s for
  all of them (`roslyn-open-documents-ready` / `roslyn-cached-open-documents-ready`).
- Reference-resolution "doctor". A failed or timed-out `ResolveReferences` now adds one Error List
  warning per project (`ReferenceResolutionDiagnostics`), naming the first MSBuild error and the
  consequence for the language service; it is withdrawn when the resolution later succeeds and reset
  on every open. On OpenDevelop.Mvp it lists 53 projects, all NETSDK1047.

The 23 language-service, build, unit-test, coverage and solution tests and the Error List / Task
List tests pass.

**TFM-specific evaluations are kept, like the current one.** `MSBuildBasedProject.OpenConfiguration`
kept the active configuration's evaluation (`currentlyOpenProject`) but evaluated the whole project
afresh, and unloaded it again, for every read that pins a TargetFramework: the active TFM's items
(Solution Explorer, the language service), `MtpTestProject`'s output path, ... The remaining 1.2 s
in `MessageBus SolutionOpenedMessage` was `SDTestService` doing exactly that per test project on the
UI thread. TFM-pinned evaluations of the active configuration are now cached per configuration,
platform and TFM, `ReevaluateIfNecessary` picks up edits, and they are unloaded together with
`currentlyOpenProject` (configuration change, disposal). `OD_CACHE_TFM_EVALUATIONS=0` disables.
OpenDevelop.Mvp, two opens each:

| | Off | On |
|---|---|---|
| `SolutionOpened` handlers done (window frozen until then) | 5.7-8.9 s | 4.2-5.9 s |
| Cached snapshots pushed (language service usable) | 20.2-24.8 s | 14.8-15.5 s |
| Every project pushed | 39.3-47.1 s | 30.7 s |
| Managed heap | 549-652 MB | 797-835 MB |

The cost is about 200 MB of managed heap on this 87-project solution. The IDE's working set was
about 1 GB either way, against 716-858 MB in the step 0 baseline; that difference predates this
change and is not yet attributed. The 22 solution/project and 23 language-service/build/test tests
pass.

**Project file watchers start off the UI thread.** Measured after the deferral above, the 88
postponed `FileSystemWatcher` starts still took 6.5-7 s of UI-thread time (~75 ms each) in the
seconds after an open. Each is now started on the thread pool, one after another; only the state
checks around it run on the UI thread, now 3-7 ms in total (`project-watchers-started`). A
FileSystemWatcher is not thread-safe, so while one is starting in the background `SetWatcher` only
records that it must re-run and `Dispose` leaves the object to `CompleteDeferredStart`, which
reconciles both and compares the recorded write time. All watchers are running 45-50 s after an open
instead of ~20 s; the write-time check covers the gap (verified with edits during and after it). A
single recursive watcher over the solution was considered and not done: it would receive every
bin/obj event of a build and, on Windows, risk buffer overflows that lose events.

Two crashes found on the way, both a directory deleted before a postponed watcher starts (a
temporary solution copy cleaned up): `DirectoryNotFoundException` from the deferred start, and from
`SetWatcher` re-run after it. `SetWatcher` caught only `FileNotFoundException`; it now catches
`IOException`, which was enough only while it always ran at load time, when the directory existed.

**Fast up-to-date check for builds (step 4 of the plan).** `FastUpToDateCheck` skips starting
`dotnet build` for a project unchanged since its last successful build, as Visual Studio does:
same global properties and SDK, same list of input files, every input written before that build
started, output assembly present. Inputs are the project file and every import, the evaluated
items that are files, every file under the project directory (excluding bin, obj and hidden
directories), `project.assets.json`, and referenced projects' output assemblies, so a rebuilt
dependency rebuilds its dependents. Multi-targeted projects and builds of a non-active configuration
always build; Rebuild always runs MSBuild; `OD_FAST_UP_TO_DATE=0` disables. Records live in
`.od/build-up-to-date/`. On the 12-project chain:

| Build | Projects built | Time |
|---|---|---|
| First | all 12 | 20.6 s |
| Nothing changed | none (12 skipped) | 1.7-2.2 s (was 12.8 s) |
| File added / edited / deleted in L5 | L5-L12; L1-L4 skipped | 14-25 s |

Each change was checked in the output assembly (added type present, renamed type present, deleted
type gone). What is left of a no-op build is mostly the up-front restore (~1.5 s).
`od.build-solution` takes an optional second argument, `rebuild`;
`BuildSolution_ChecksResultOutputPadErrorListAndUnknownProject` uses it, because it asserts on
MSBuild's own log, which a skipped build does not produce.

Building this found a bug in the TFM-evaluation cache above: a kept MSBuild evaluation is not
re-globbed by `ReevaluateIfNecessary`, so a file added to the project directory stayed out of the
active TFM's item list (the up-to-date check called an L5 with a new `.cs` "up to date", and
Solution Explorer would have missed it too). Item lists now force the kept evaluation to
re-evaluate when a directory under the project (bin, obj and hidden ones excepted) was written since
it was made: adding, removing or renaming a file updates its directory, editing content does not.
Forcing it on every read instead cost the warm open its gain (cached push 14.6 s → 26-29 s). An
externally added file appears in Solution Explorer with the cache on and off; the 32 solution,
language-service, build and test integration tests pass.

### Whole-solution no-op build: 150 s → 12 s

Measured on OpenDevelop.Mvp (87 projects), the "no-op" build above only held for the 12-project
chain; across the whole solution the up-to-date check skipped 24-30 projects and a build with
nothing changed still took 150-170 s. Four causes, all fixed:

- `MinimalMSBuildEngine` passes `BuildingInsideVisualStudio=true`, which makes
  `_ComputeNonExistentFileProperty` add `__NonExistentFile__` to `CoreCompile`'s outputs (Visual
  Studio's host compiler does its own change detection). Every project MSBuild did build therefore
  recompiled and rewrote its assembly, and every dependent then saw a changed input. The legacy
  in-process engine overrode that target (`MSBuildEngineWorker`); the minimal engine now passes
  `UseHostCompilerIfAvailable=false`, which turns the target's condition off. This alone was the
  cascade: the two multi-targeted Designer projects the check cannot judge rebuilt everything above
  them.
- Referenced projects outside the solution (OpenDevelop.Mvp.slnx leaves out Widgets,
  DesignerCanvas, ...) made the check give up. Their project trees, their own references' trees
  (read from the XML, conditions ignored, a missing file skipped) and the Directory.Build files above
  them now count as inputs.
- `OutputAssemblyFullPath` says `X.exe` for an SDK Exe project whose output is `X.dll`, so 11
  projects were "output missing" forever. The check uses the evaluated `TargetPath`.
- A nested project's `bin`/`obj` (AspNetCore/Tests) made its parent's tree change on every build;
  every `bin`/`obj` directory is now skipped.

Multi-targeted projects are judged too (every TFM's `TargetPath` must exist). The up-front restore
is skipped when every project of the build is up to date. Result: 87/87 projects skipped, 7-9 s; touching, adding or removing one file rebuilds exactly that project.

### One MSBuild process for everything that rebuilds

An edit to a library low in the graph (Designer.Remote) rebuilds ~55 dependents. As ten `dotnet
build` processes side by side, each repeating evaluation and reference resolution, a project that
builds in 3-5 s alone took 15-45 s, and the edit took 166-173 s. `BuildEngine.BuildUpFrontAsync`
now builds every project that is not up to date, plus everything that depends on one of them, in
one `dotnet msbuild` of a generated traversal (`MinimalMSBuildEngine.BuildInOneProcessAsync`,
`-m`, under the solution's global.json directory, removed afterwards). Each project that built is
recorded with the up-to-date check, so the per-project scheduling that follows skips it.
`OD_ONE_PROCESS_TRACE=<file>` appends what each run saw (result lines, attributed errors).

How the traversal is shaped, and why - each point was a real failure first:

- Projects go in **dependency waves** computed from the build graph, each wave in parallel, with
  exactly the per-project global properties (`BuildingInsideVisualStudio=true` included). Letting
  MSBuild follow ProjectReferences instead built a project referenced with different global
  properties twice at once into the same output (MC1000 on ICSharpCode.Core.Presentation).
- `ContinueOnError="ErrorAndContinue"`, never `true`: `true` is WarnAndContinue, which turns the
  errors into warnings and `MSBuildLastTaskResult` into true. A project with a compile error was
  recorded as built and then skipped as up to date.
- After a wave with a failure, later waves do not start (they hold its dependents). Projects that
  did not run get no result and go to the per-project path, which skips dependents of a failure.
- Per-project results go to a file written by `WriteLinesToFile`, not `-getItem`, which switches
  the console log off. Result targets follow the waves through `DependsOnTargets`; a target run by
  `CallTarget` does not see properties its caller set, which silently disabled every result.
- Diagnostics are attributed to their project from the `[project::props]` suffix MSBuild appends
  and reported as they arrive, once per project/file/position/code (a multi-targeted project
  reports each once per TFM). A project that failed with an error of its own is not built again;
  restore errors (`NU*`, NETSDK1004/1005/1047) do not count, since the traversal restores nothing.
- Output lines are queued by the process's output threads and reported every 200 ms on the
  thread that started the build. Reporting a few hundred lines straight from the output threads
  hung the IDE for good: the UI thread blocked on a lock in LibreWPF's `Visual.GetDpi` during
  rendering (stack in the investigation notes; a LibreWPF issue worth reporting on its own).

Measured on OpenDevelop.Mvp: the Designer.Remote edit 166-173 s → 86 s, all 56 in one process; a
build with a compile error in Designer.Remote 80-123 s → 10-14 s, the error listed once; a no-op
build 3 s. `OD_ONE_PROCESS_BUILD=0` disables it.

The up-to-date answers are now computed once per build, off the UI thread and four projects at a
time (`BuildEngine.CheckUpToDateUpFrontAsync`), and kept per build until anything is built or
forgotten; the restore and one-process decisions and the per-project builds reuse them. Before,
each project's tree walk ran up to three times, sequentially on the UI thread: a no-op build of
OpenDevelop.Mvp took 9-11 s with the window frozen for ~3 s; it now takes 2.0 s.

For this the check compares a referenced project's output with the write time recorded after the
build, not with the build's start: inside one process the references are rebuilt during it. The
default parallel project count is now the core count (was `min(4, cores)`); measured alone it did
not help the per-project path, whose cost was contention, not the limit.

Batched reference resolution (one `dotnet msbuild` for all projects that miss the cache) must run
from each project's own global.json directory: the NuGet SDK resolver reads `msbuild-sdks` pins
from the entry project's directory, so a traversal in the temp directory resolved LibreWPF.Sdk to
the wrong version and brought NETSDK1047 back for 53 projects. Projects are grouped by their nearest
global.json; the traversal file is written to that directory's `.od` and removed. With every
project resolving for real, one batch costs ~32 s for 85 projects (an earlier 12.6 s figure was
inflated by the failing half), and the cold snapshot phase is ~36-40 s against ~48 s before.

Both Roslyn snapshot caches (the `.od/roslyn-tfm-cache` entries and the warm-start solution
snapshots) now reject an entry naming a document that no longer exists. A file created and deleted
around a cache write (the VSEditor tests' scratch files) otherwise stayed in the entry, Roslyn
failed reading it, and that project lost outline and folding for good.

### Idle CPU: a layout clip that was "new" on every frame (fixed in LibreWPF)

OpenDevelop sat at ~90-96% CPU while idle, even with no solution open: ~61 render callbacks a
second, each ~14 ms walking the whole visual tree (`WpfVisualInvalidationTracker`). It was a
feedback loop, not a requester: each frame's change detection found "changed" visuals and requested
the next frame. The same eight visuals every time - `Grid#PART_Indicator`, `ScrollContentPresenter`s,
`Image`s - all elements with a layout clip. LibreWPF's `FrameworkElement.TryGetPortableVisualLayoutState`
called `GetLayoutClip`, which builds a new `Geometry` on every call, and the tracker compares the
clip by reference. Fix (LibreWPF, `FrameworkElement.cs`): keep the clip computed after the last
arrange, as WPF's own `UIElement.ensureClip` does, and drop it at the start of `ArrangeCore`.

How it was found, for next time: opt-in traces showed `MediaContext.PostRender` and the animation
tick path were NOT involved (zero calls while idle); a stack on the render-scheduler request led to
`DetectVersionChanges`, and logging the changed sources named the elements.

Result on macOS: idle 90-96% -> 3-7% CPU (with or without a solution open); and since the UI
thread is free, OpenDevelop.Mvp's cold open (until every project is pushed to Roslyn) 101-108 s ->
42.6 s (push 47 s -> 9.3 s), warm cached push 63 s -> 25.5 s. Workbench, VSEditor, SolutionFolder,
TaskListPad, ILSpy and OpenLens integration tests pass on it (WpfGalleryDesignerTests skip on this
machine). Not yet in the feed: OpenDevelop gets it with the next LibreWPF publish; until then
`openavalon/dev-overlay.sh <bin> PresentationFramework` applies it to a local build.

### Pushing projects to the Roslyn host: read documents lazily

With the UI thread free, a warm open of OpenDevelop.Mvp still spent ~20 s pushing the 89 cached
snapshots (`roslyn-cached-projects-pushed` now reports the load-RPC total). Timing the host's
`LoadProjectsAsync` by phase showed where: 16.7 s of 20.9 s was `File.ReadAllTextAsync` of the
3,800 documents - ~4.4 ms a file even in parallel, on a machine with on-access malware scanning
(Microsoft Defender's processes were at 40-56% CPU throughout). Two traps on the way to the fix:

- Batching the adds into one `TryApplyChanges` changed nothing: the reads were the cost, not the
  per-document solution versions.
- Giving the documents a lazy `FileTextLoader` and adding them through `TryApplyChanges` only moved
  the reads: applying an added document makes the workspace read its text synchronously to pass it
  to `ApplyDocumentAdded`. `AdhocWorkspace.AddDocument(DocumentInfo)` keeps the loader unread.

Now each new document is added with a `FileTextLoader` through `AdhocWorkspace.AddDocument`, so a
file is read when Roslyn first needs it. Warm push 21.8 s -> 8.4 s (applying the documents: 14.7 s
-> 0.2 s). The first diagnostics request on a Base file afterwards took 9.9 s, including the lazy
reads of what it needed. Language-service (15), Workbench (28) and VSEditor (6) tests pass.

The host also keeps one `MetadataReference` per reference file (keyed by size and write time)
instead of creating one per project that references it.

### Batched project loads, and regression gates

Timing the warm push by phase after the lazy reads: of 7.5 s, the host's own work was 2.1 s
(creating projects 1.2 s, references 0.2 s, documents 0.7 s); the rest was one round trip per
project - 4.3 s of RPCs plus the IDE's side of each. `roslyn/projects/load` carries several
snapshots in one request; the host reconciles the graph once per request
(`CSharpVBLanguageService.LoadProjectsAsync`, which always took a list). The push sends the open
documents' projects as a first chunk of their own, then chunks of 16. `RecoveringRoslynTransport`
records a batch as the single loads it stands for, so a restarted host gets them replayed as before.
The protocol and argument interfaces have defaults that fall back to single loads, so other
implementers need nothing. Warm push 7.5 s -> 3.1 s; Base.Tests cover the method name and the
replay (`RecoveryTransport_ReplaysABatchedLoadAsSingleLoads`).

`PerformanceGateTests` pins the behaviour behind the headline numbers rather than wall-clock times:

- `IdleWindow_DoesNotKeepTheCpuBusy`: under 40% of a core while idle (measured 5%; 110-119% with
  the layout-clip bug). "Idle" starts when this open's own milestones (`roslyn-projects-pushed`,
  `project-watchers-started`) are in the timeline, polled with a 3-minute limit - a fixed delay
  would report a slow machine's still-running load as an idle-CPU regression. The CPU time comes
  from the app through `od.process.cpu`: the fixture's own process handle is `dotnet run`, whose
  child is the app, and measuring it passed against the unfixed build. Note that building the shell
  OR the integration-test project redeploys the package's PresentationFramework over a dev overlay.
- `SecondBuildOfAnUnchangedSolution_SkipsEveryProject`: no `project-built` or one-process build on
  the second build of SlnxFixture (fails with `OD_FAST_UP_TO_DATE=0`, as it should).

### One evaluation per single-target project

A gcdump after a warm open of OpenDevelop.Mvp: 628 MB GC heap, the largest share MSBuild's
(212,766 `ProjectItem`s, 247,540 `ProjectMetadata`, 97,210 `ReaderWriterLockSlim`s) - from 176
`Project` evaluations for 87 projects. The TFM-evaluation cache kept a second evaluation, with
`TargetFramework` as a global property, even for a single-target project asking for its own
framework. `MSBuildBasedProject.OpenConfiguration` now answers that from the active evaluation
(re-evaluated for item lists when the project tree changed, like the kept TFM evaluations).
Result: 94 evaluations, 113,837 items, GC heap 571 MB, working set 1,073 -> 927 MB.

The no-op build gate caught what that exposed: the fast up-to-date check recorded a project's inputs
from an evaluation made before the build, whose restore had just written `obj/*.nuget.g.props` and
`.targets` that the project imports, so the next build saw "input files added". `Succeeded` now
calls `MSBuildBasedProject.RefreshEvaluation()` before recording (~4 s over the 56 projects of the
Designer.Remote edit: 96 s against 86 s; the no-op build stays at 3 s).

### An edited project file reloads that project, not the solution

`ProjectChangeWatcher` reloaded the whole solution whenever an SDK-style project file changed outside
the IDE: every project re-evaluated and re-pushed to the language service, and every document
closed - with a save prompt for each modified one (`CloseAllSolutionViews(force: false)`), which
`OD_TEST_MODE` answers by discarding the edits. Now `IProjectService.ReloadProject` re-reads that
project in place (`MSBuildBasedProject.ReloadFromDisk`, previously compiled only under `HAS_UNO`),
rebuilds its items, refreshes Solution Explorer, and publishes `ProjectReloadedMessageEventArgs`; the
C# language service pushes just that project, chained behind any solution push. The solution
reload stays as the fallback: solution files, legacy projects, a failed reload - and an edit that
changes the project's set of language-service slices (a framework added to or removed from
`TargetFrameworks`, or single <-> multi-targeted), because the host can add and update slices but
not drop one, and a stale slice would keep its reference graph. A removed ProjectReference is fine
in place: each slice's reference list is replaced as a whole.
`ProjectReloadTests` covers it: after a new file and a touched .csproj, both edited documents stay
dirty with their text, and the language service resolves the new file's class and the unsaved one;
and a TargetFramework -> TargetFrameworks edit falls back to the solution reload.

Two smaller things found on the way, both kept:
- ILSpy's `SearchPane` subscribed to `CompositionTarget.Rendering` for its whole lifetime (forcing a
  frame per tick under WPF's rules); it now subscribes only while a search has results to drain.
  Not the idle cause - the pane is not created at startup - but a real one once it is.
- `CSharpVBLanguageService.AddDocumentCore` looked for an existing document by scanning every
  document of every project for each one added; it uses Roslyn's file-path index now. No measurable
  change in the push (the bottleneck is elsewhere), but it removes a (documents)^2 term.

**Still open, in order of expected gain** (refreshed 2026-10-08; measured on OpenDevelop.Mvp, macOS):

1. Done: warm push 7.5 s -> 3.1 s by batching (`roslyn/projects/load`, see below).
2. Done (2026-10-08): LibreWPF published to the local feed as 0.1.0-preview.65 with the RID-less
   SDK default and the layout-clip fix; the AvalonEdit and WpfDesigner submodules' global.json
   pins moved from preview.57 to preview.65. Reference resolution: 0 failures on a cold open
   (was 4 on every open); a warm open serves all 85 projects from the cache, so no MSBuild
   process starts. The idle-CPU gate passes without a dev overlay.
3. Graph scheduling inside the (already working) one-process build: today it builds dependency
   waves, and each wave waits for its slowest project; a dependent could instead start as soon as
   its own dependencies finish. An edit to Designer.Remote takes 86-96 s. Tried as a shortcut:
   a traversal listing the 56 projects as ProjectReferences, built with `msbuild -graph -m` and the
   IDE's global properties - it failed (MSB3030: dependents copied `ICSharpCode.Designer.Remote.dll`
   while the multi-targeted Designer.Remote was rebuilding it), so static graph builds do not order
   this repository safely as they are. Doing it properly means our own scheduler: per-project
   MSBuild requests issued from the IDE as each project's dependencies finish, inside one
   long-lived MSBuild node (e.g. the in-process BuildManager), which is a larger change.
4. Partly done: one MSBuild evaluation per single-target project instead of two (below). What is
   left of the ~570 MB GC heap after an open is not attributed further yet.
5. ReadyToRun for RoslynHost: checked, little to gain. The Roslyn assemblies it loads
   (Microsoft.CodeAnalysis, .CSharp, .VisualBasic, .Workspaces, .Features: ~35 MB) already ship
   ReadyToRun; what is left to precompile is the host's own small assemblies. One XAML server per
   runtime is still open.
6. Done: `PerformanceGateTests` (see below). The idle-CPU gate fails until OpenDevelop gets the
   LibreWPF layout-clip fix from the feed (or `openavalon/dev-overlay.sh <bin> PresentationFramework`
   after each shell build, which replaces the overlaid file with the package's).

Done, for the record: batched reference resolution; fast up-to-date check (no-op build ~150 s ->
2-3 s); one-process build; unsaved buffers re-sent only for the loaded project; idle CPU (LibreWPF
layout clip, 90% -> 3-7%); lazy document reads in the host.

Still open in step 2: making the cache committable (keys still contain absolute paths), and
covering the multi-target path's `TfmEvaluationCache`, which keeps its timestamp key.

## The five techniques, and where OpenDevelop stands

| C# Dev Kit 11 | OpenDevelop today (2026-10-08) |
|---|---|
| Persistent project cache: later loads come from the cache, and the cache can be committed | Done for this machine: reference-resolution cache (`.od/roslyn-reference-cache`, content-keyed), solution snapshots for a warm start (`.od/roslyn-solution-snapshots`), TFM-evaluation reuse. Not committable: keys hold absolute paths. |
| Active file first, rest asynchronous | Done: the open documents' projects (and what they reference) are pushed first; snapshots are built and pushed off the UI thread; documents are read lazily by the host. |
| Fast up-to-date check, build acceleration | Done: `FastUpToDateCheck`, up-front restore only when needed, everything stale built in one MSBuild process. |
| Six processes consolidated into one Native AOT process | Not pursued: OpenDevelop's children exist for isolation. Their start-up (ReadyToRun) is still open. |
| Compact in-memory project model | Not measured beyond the working set; see "Still open". |

The sections below are the original plan, kept for the reasoning behind each step.

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
