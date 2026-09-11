using System;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
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
			projectService.SolutionOpened += OnSolutionOpened;
			projectService.SolutionClosed += OnSolutionClosed;
			if (projectService.CurrentSolution != null)
				QueueSolution(projectService.CurrentSolution);
		}

		void OnSolutionOpened(object sender, SolutionEventArgs e) =>
			QueueSolution(e.Solution);

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
				var snapshots = await Task.Run(() => LanguageServiceProjectSnapshotFactory.FromSolution(solution));
				foreach (var snapshot in snapshots) {
					if (generation != Volatile.Read(ref solutionGeneration))
						return;
					if (service is RemoteLanguageService remote)
						await remote.LoadProjectAsync(snapshot, CancellationToken.None);
					else
						await protocol.RoslynProjectLoadAsync(snapshot, CancellationToken.None);
				}
			} catch (Exception ex) {
				LoggingService.Warn("Unable to synchronise the Roslyn host project graph: " + ex.Message);
			}
		}

		void OnSolutionClosed(object sender, SolutionEventArgs e)
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
			if (projectService != null) {
				projectService.SolutionOpened -= OnSolutionOpened;
				projectService.SolutionClosed -= OnSolutionClosed;
			}
			registration?.Dispose();
			(service as IDisposable)?.Dispose();
		}
	}
}
