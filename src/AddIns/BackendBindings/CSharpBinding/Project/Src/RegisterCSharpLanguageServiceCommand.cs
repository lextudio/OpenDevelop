using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project;

namespace CSharpBinding
{
	public sealed class RegisterCSharpLanguageServiceCommand : AbstractCommand, IDisposable
	{
		ICSharpCode.SharpDevelop.LanguageServices.ILanguageService service;
		IDisposable registration;
		IDisposable solutionOpenedSubscription;
		IDisposable solutionClosedSubscription;
		IDisposable projectReloadedSubscription;
		IProjectService projectService;
		LanguageServiceRegistry registry;
		long solutionGeneration;
		Task solutionSync = Task.CompletedTask;

		public override void Run()
		{
			registry = SD.GetRequiredService<LanguageServiceRegistry>();
			projectService = SD.GetRequiredService<IProjectService>();
			// The IDE never owns a Roslyn workspace. The application project (and test projects that
			// host this add-in) deploy RoslynHost/ through a project reference; failing this lookup is
			// a packaging error, not a reason to silently instantiate an in-process fallback.
			var path = RoslynHostProcessTransport.TryResolveDefaultHostPath()
				?? throw new InvalidOperationException("Roslyn host was not deployed. Rebuild the OpenDevelop application.");
			var protocol = new RemoteRoslynLanguageProtocol(new RecoveringRoslynTransport(() => new RoslynHostProcessTransport(path)));
			service = new RemoteLanguageService(protocol, async (document, token) => {
				var project = projectService.FindProjectContainingFile(FileName.Create(document.FileName));
				if (project != null)
					foreach (var snapshot in LanguageServiceProjectSnapshotFactory.FromProjectAllTargetFrameworks(project))
						await ((RemoteLanguageService)service).LoadProjectAsync(snapshot, token);
			});
			registration = registry.RegisterExtension(".cs", service);
			solutionOpenedSubscription = MessageBus<SolutionOpenedMessageEventArgs>.Subscribe(OnSolutionOpened);
			solutionClosedSubscription = MessageBus<SolutionClosedMessageEventArgs>.Subscribe(OnSolutionClosed);
			projectReloadedSubscription = MessageBus<ProjectReloadedMessageEventArgs>.Subscribe(OnProjectReloaded);
			if (projectService.CurrentSolution != null)
				QueueSolution(projectService.CurrentSolution);
		}

		void OnSolutionOpened(object sender, SolutionOpenedMessageEventArgs e) =>
			QueueSolution(e.Solution);

		// A project re-read in place (its file changed outside the IDE): push just that project,
		// chained behind any solution push still running so the two cannot interleave.
		void OnProjectReloaded(object sender, ProjectReloadedMessageEventArgs e)
		{
			if (!(service is RemoteLanguageService remote))
				return;
			var generation = Volatile.Read(ref solutionGeneration);
			var project = e.Project;
			solutionSync = PushReloadedProjectAsync(solutionSync, remote, project, generation);
		}

		async Task PushReloadedProjectAsync(Task previous, RemoteLanguageService remote, IProject project, long generation)
		{
			try { await previous; } catch { }
			if (generation != Volatile.Read(ref solutionGeneration))
				return;
			try {
				var snapshots = await Task.Run(() => LanguageServiceProjectSnapshotFactory.FromProjectAllTargetFrameworks(project));
				await Task.Run(() => remote.LoadProjectsAsync(snapshots, CancellationToken.None));
			} catch (Exception ex) {
				LoggingService.Warn("Unable to push the reloaded project " + project.Name + " to the Roslyn host: " + ex.Message);
			}
		}

		void QueueSolution(ISolution solution)
		{
			var generation = Interlocked.Increment(ref solutionGeneration);
			solutionSync = PushSolutionAsync(solutionSync, solution, generation);
		}

		async Task PushSolutionAsync(Task previous, ISolution solution, long generation)
		{
			await previous;
			if (generation != Volatile.Read(ref solutionGeneration))
				return;
			if (solution == null || !registry.TryGetProtocol(".cs", out var protocol))
				return;
			try {
				// Task.Run, because building the snapshots is seconds of blocking work and this
				// method would otherwise do all of it on the UI thread.
				//
				// PushSolutionAsync is async, but `await previous` completes synchronously whenever
				// the previous push has already finished - which it has, on the first solution open
				// of a session - so everything up to the first real await runs inline on whatever
				// thread called QueueSolution. That caller is SolutionOpened, raised from
				// SDProjectService.OpenSolutionInternal on the dispatcher. FromSolution then runs
				// MSBuild's ResolveReferences target in a child dotnet process, once per project
				// (MinimalMSBuildEngine.RunResolveReferences), and blocks reading its output.
				//
				// Measured on tests/fixtures/DebugTestApp: ~9s of a completely frozen window per
				// project, long enough that macOS filed a spin report and the app looked like it
				// had crashed. A project whose reference resolution fails is the slow case, so the
				// freeze is worst exactly when there is least to show for it. See
				// doc/technotes/msbuild.md.
				//
				// Only the snapshot construction moves off the dispatcher; there is deliberately no
				// ConfigureAwait(false), so the loop below resumes on the UI thread as before.
				// Warm start: push what the last open of this solution produced right away, so the
				// language service works before the fresh snapshots (tens of seconds on a large
				// solution) exist. Only when the cached project set is exactly the current one: the
				// host has no "remove project", and closing its workspace would drop unsaved buffers.
				// The fresh snapshots below then replace whichever cached ones differ.
				PerfTimeline.Mark(PerfTimeline.SolutionOpen, "roslyn-push-started");
				Dictionary<string, string> warm = null;
				if (Environment.GetEnvironmentVariable("OD_WARM_START") != "0") {
					var currentKeys = LanguageServiceProjectSnapshotFactory.CurrentSnapshotKeys(solution);
					var cached = await Task.Run(() => LanguageServiceProjectSnapshotFactory.TryLoadSolutionSnapshots(solution));
					if (cached != null && cached.Count > 0
					    && currentKeys.SetEquals(cached.Select(LanguageServiceProjectSnapshotFactory.SnapshotKey))) {
						var orderedCache = PrioritizeOpenDocuments(cached, out var cachedPriority);
						if (!await PushSnapshotsAsync(protocol, orderedCache, cachedPriority, generation, "roslyn-cached-open-documents-ready", "roslyn-cached-projects-pushed"))
							return;
						warm = cached.ToDictionary(LanguageServiceProjectSnapshotFactory.SnapshotKey, SerializeSnapshot);
					} else {
						PerfTimeline.Mark(PerfTimeline.SolutionOpen, "roslyn-warm-start-skipped",
							cached == null ? "no cache" : "project set changed");
					}
				}

				// Built only after the cached push: building them alongside it (four
				// ResolveReferences children against the host loading metadata) made the cached push
				// itself twice as slow (20.6 s -> 40.6 s on OpenDevelop.Mvp), and being usable early
				// is the point of the warm start; verifying it can come later.
				PerfTimeline.Mark(PerfTimeline.SolutionOpen, "roslyn-snapshots-started");
				var snapshots = await Task.Run(() => LanguageServiceProjectSnapshotFactory.FromSolution(solution));
				PerfTimeline.Mark(PerfTimeline.SolutionOpen, "roslyn-snapshots-built",
					snapshots.Count + " snapshots, " + LanguageServiceProjectSnapshotFactory.ReferenceCacheStatistics);
				var fresh = snapshots;
				_ = Task.Run(() => LanguageServiceProjectSnapshotFactory.SaveSolutionSnapshots(solution, fresh));
				if (warm != null)
					snapshots = snapshots
						.Where(snapshot => !warm.TryGetValue(LanguageServiceProjectSnapshotFactory.SnapshotKey(snapshot), out var json) || json != SerializeSnapshot(snapshot))
						.ToList();
				// Active document first (C# Dev Kit's "the active file is ready at once"): the
				// projects of open documents, the active one leading, and everything they reference
				// go first; the rest follow in solution order. Read on the UI thread, which this
				// continuation is on.
				snapshots = PrioritizeOpenDocuments(snapshots, out var priorityCount);
				await PushSnapshotsAsync(protocol, snapshots, priorityCount, generation, "roslyn-open-documents-ready", "roslyn-projects-pushed",
					warm != null ? snapshots.Count + " changed since the cache" : null);
			} catch (Exception ex) {
				LoggingService.Warn("Unable to synchronise the Roslyn host project graph: " + ex.Message);
			}
		}

		static string SerializeSnapshot(LanguageServiceProjectSnapshot snapshot) =>
			System.Text.Json.JsonSerializer.Serialize(snapshot);

		/// <summary>Pushes <paramref name="snapshots"/> to the host in order; false if a newer
		/// solution open superseded this one meanwhile.</summary>
		const int PushChunkSize = 16;

		async Task<bool> PushSnapshotsAsync(IRoslynLanguageProtocol protocol, IReadOnlyList<LanguageServiceProjectSnapshot> snapshots,
			int priorityCount, long generation, string priorityMark, string doneMark, string detail = null)
		{
			var remote = service as RemoteLanguageService;
			remote?.ResetLoadTimings();
			int pushedCount = 0;
			var pushTimes = new List<(string Project, long Milliseconds)>();
			// The pushes run on the thread pool: resuming each of them on the UI thread queued every
			// project behind whatever the dispatcher was doing right after a solution open
			// (doc/technotes/fast-mode.md). Nothing here touches the UI; RemoteLanguageService
			// serializes its own updates under a lock.
			// In chunks, one roslyn/projects/load each: one round trip per project was most of a warm
			// push (89 projects: 4.3 s of RPCs for 2.1 s of host work, plus the IDE's side of each
			// trip). The open documents' projects form the first chunk of their own, so they are
			// usable before the rest arrive.
			var chunks = new List<IReadOnlyList<LanguageServiceProjectSnapshot>>();
			if (priorityCount > 0)
				chunks.Add(snapshots.Take(priorityCount).ToList());
			foreach (var chunk in snapshots.Skip(priorityCount).Chunk(PushChunkSize))
				chunks.Add(chunk);
			await Task.Run(async () => {
				foreach (var chunk in chunks) {
					if (generation != Volatile.Read(ref solutionGeneration))
						return;
					var pushing = System.Diagnostics.Stopwatch.StartNew();
					if (remote != null)
						await remote.LoadProjectsAsync(chunk, CancellationToken.None).ConfigureAwait(false);
					else
						await protocol.RoslynProjectsLoadAsync(chunk, CancellationToken.None).ConfigureAwait(false);
					pushTimes.Add((System.IO.Path.GetFileNameWithoutExtension(chunk[0].ProjectFileName) + (chunk.Count > 1 ? " +" + (chunk.Count - 1) : ""), pushing.ElapsedMilliseconds));
					pushedCount += chunk.Count;
					if (priorityCount > 0 && pushedCount == priorityCount)
						PerfTimeline.Mark(PerfTimeline.SolutionOpen, priorityMark, priorityCount + " snapshot(s)");
				}
			});
			if (generation != Volatile.Read(ref solutionGeneration))
				return false;
			var timings = remote?.GetLoadTimings() ?? (WaitTicks: 0L, RpcTicks: 0L);
			PerfTimeline.Mark(PerfTimeline.SolutionOpen, doneMark,
				(detail != null ? detail + "; " : "") + snapshots.Count + " pushed (load rpc "
				+ (timings.RpcTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) + "ms, waiting "
				+ (timings.WaitTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) + "ms), slowest: "
				+ string.Join(", ", pushTimes.OrderByDescending(t => t.Milliseconds).Take(5).Select(t => t.Project + " " + t.Milliseconds + "ms")));
			return true;
		}

		IReadOnlyList<LanguageServiceProjectSnapshot> PrioritizeOpenDocuments(IReadOnlyList<LanguageServiceProjectSnapshot> snapshots, out int priorityCount)
		{
			priorityCount = 0;
			var workbench = SD.Services.GetService(typeof(ICSharpCode.SharpDevelop.Workbench.IWorkbench)) as ICSharpCode.SharpDevelop.Workbench.IWorkbench;
			if (workbench == null)
				return snapshots;
			var openFiles = new List<string>();
			if (workbench.ActiveViewContent?.PrimaryFileName != null)
				openFiles.Add(workbench.ActiveViewContent.PrimaryFileName.ToString());
			openFiles.AddRange(workbench.ViewContentCollection
				.Select(view => view.PrimaryFileName?.ToString())
				.Where(fileName => fileName != null));
			var roots = openFiles
				.Select(fileName => projectService.FindProjectContainingFile(FileName.Create(fileName))?.FileName.ToString())
				.Where(projectFile => projectFile != null)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (roots.Count == 0)
				return snapshots;

			// Everything the open documents' projects reference, transitively: they cannot compile
			// without it.
			var byProject = snapshots.ToLookup(snapshot => snapshot.ProjectFileName, StringComparer.OrdinalIgnoreCase);
			var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var pending = new Queue<string>(roots);
			while (pending.Count > 0) {
				var projectFile = pending.Dequeue();
				if (rank.ContainsKey(projectFile))
					continue;
				rank[projectFile] = rank.Count;
				foreach (var reference in byProject[projectFile].SelectMany(snapshot => snapshot.ProjectReferenceFileNames))
					pending.Enqueue(reference);
			}
			var prioritized = snapshots
				.Where(snapshot => rank.ContainsKey(snapshot.ProjectFileName))
				.OrderBy(snapshot => rank[snapshot.ProjectFileName])
				.ToList();
			priorityCount = prioritized.Count;
			return prioritized.Concat(snapshots.Where(snapshot => !rank.ContainsKey(snapshot.ProjectFileName))).ToList();
		}

		void OnSolutionClosed(object sender, SolutionClosedMessageEventArgs e)
		{
			Interlocked.Increment(ref solutionGeneration);
			solutionSync = CloseSolutionAsync(solutionSync, e.Solution?.Directory.ToString());
		}

		async Task CloseSolutionAsync(Task previous, string solutionDirectory)
		{
			// An already-issued project load must finish before closing. The next solution load
			// awaits this task in turn, so a delayed close cannot clear its new workspace.
			await previous;
			if (!registry.TryGetProtocol(".cs", out var protocol))
				return;
			try {
				if (service is RemoteLanguageService remote)
					await remote.CloseSolutionAsync(CancellationToken.None);
				else
					await protocol.RoslynSolutionClosedAsync(CancellationToken.None);
			} catch (Exception ex) {
				LoggingService.Warn("Unable to clear the Roslyn host workspace: " + ex.Message);
			}
		}

		public void Dispose()
		{
			Interlocked.Increment(ref solutionGeneration);
			solutionOpenedSubscription?.Dispose();
			solutionClosedSubscription?.Dispose();
			projectReloadedSubscription?.Dispose();
			registration?.Dispose();
			(service as IDisposable)?.Dispose();
		}
	}
}
