using System;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Roslyn;
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
			if (Environment.GetEnvironmentVariable("OD_ROSLYN_HOST") == "1") {
				var path = RoslynHostProcessTransport.TryResolveDefaultHostPath()
					?? throw new InvalidOperationException("Roslyn host was not deployed. Rebuild the OpenDevelop application.");
				var protocol = new RemoteRoslynLanguageProtocol(new RecoveringRoslynTransport(() => new RoslynHostProcessTransport(path)));
				service = new RemoteLanguageService(protocol, async (document, token) => {
					var project = projectService.FindProjectContainingFile(FileName.Create(document.FileName));
					if (project != null)
						foreach (var snapshot in LanguageServiceProjectSnapshotFactory.FromProjectAllTargetFrameworks(project))
							await ((RemoteLanguageService)service).LoadProjectAsync(snapshot, token);
				});
			} else {
				service = new CSharpVBLanguageService(fileName => {
					var project = projectService.FindProjectContainingFile(FileName.Create(fileName));
					return project == null ? Array.Empty<LanguageServiceProjectSnapshot>()
						: LanguageServiceProjectSnapshotFactory.FromProjectAllTargetFrameworks(project);
				});
			}
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
				foreach (var snapshot in LanguageServiceProjectSnapshotFactory.FromSolution(solution)) {
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
				else if (service is CSharpVBLanguageService local)
					await local.CloseSolutionAsync(solutionDirectory, CancellationToken.None);
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
