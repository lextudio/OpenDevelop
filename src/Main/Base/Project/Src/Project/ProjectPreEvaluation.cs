// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// The active-configuration MSBuild evaluations of a solution's projects, made concurrently before the
	/// projects are constructed one by one (doc/technotes/fast-mode.md).
	///
	/// Constructing a project evaluates it (<see cref="MSBuildBasedProject"/>), and evaluation was ~92% of a
	/// solution load. <see cref="Run"/> evaluates all of them in parallel in the solution's
	/// <see cref="ProjectCollection"/>; when a project is then constructed it <see cref="Take"/>s its
	/// evaluation instead of making one, provided it was made from the same XML with exactly the same global
	/// properties. A project that fails to evaluate here is left to its constructor, which reports the error
	/// as before. Dispose the instance once the projects are constructed: it unloads every unused evaluation.
	///
	/// Concurrency: the evaluations run without <see cref="MSBuildInternals.SolutionProjectCollectionLock"/>.
	/// MSBuild synchronizes concurrent evaluation of distinct projects in one <see cref="ProjectCollection"/>
	/// itself (its loaded-project list and ProjectRootElementCache are internally locked, and the shared
	/// EvaluationContext is built for concurrent use); the global lock serializes SharpDevelop's own
	/// operations on a live solution. The collection here belongs to a solution that is still being built, so
	/// nothing else uses it, and the concurrent phase only evaluates: unloading waits until it has finished.
	/// </summary>
	public sealed class ProjectPreEvaluation : IDisposable
	{
		/// <summary>One project to evaluate: its file and the global properties of its active configuration
		/// (<see cref="MSBuildBasedProject.CreateEvaluationGlobalProperties"/>).</summary>
		public readonly record struct Request(string FileName, IReadOnlyDictionary<string, string> GlobalProperties);

		sealed record Evaluation(Microsoft.Build.Evaluation.Project Project, IReadOnlyDictionary<string, string> GlobalProperties, DateTime StartedUtc);

		/// <summary><c>OD_PARALLEL_EVALUATION=0</c> turns pre-evaluation off.</summary>
		public static bool Enabled => Environment.GetEnvironmentVariable("OD_PARALLEL_EVALUATION") != "0";

		/// <summary>
		/// Concurrent evaluations: <c>OD_PARALLEL_EVALUATION_DOP</c> when set, else one per logical processor but
		/// one, which stays free for the UI thread (all of them starved it into multi-second input stalls on a
		/// 2-core/4-thread machine, for no measurable throughput gain - evaluation does not scale past the
		/// physical cores).
		/// </summary>
		public static int DegreeOfParallelism =>
			int.TryParse(Environment.GetEnvironmentVariable("OD_PARALLEL_EVALUATION_DOP"), out int dop) && dop > 0
				? dop : Math.Max(1, Environment.ProcessorCount - 1);

		static readonly StringComparer PathComparer =
			RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

		/// <summary>The pre-evaluation in progress for each collection (one per solution being loaded).</summary>
		static readonly ConditionalWeakTable<ProjectCollection, ProjectPreEvaluation> active = new ConditionalWeakTable<ProjectCollection, ProjectPreEvaluation>();

		readonly ProjectCollection projectCollection;
		readonly ConcurrentDictionary<string, Evaluation> evaluations = new ConcurrentDictionary<string, Evaluation>(PathComparer);
		int reused, mismatched;

		ProjectPreEvaluation(ProjectCollection projectCollection)
		{
			this.projectCollection = projectCollection;
		}

		/// <summary>
		/// Evaluates <paramref name="requests"/> concurrently in <paramref name="projectCollection"/> and keeps
		/// the results for <see cref="Take"/> until the returned instance is disposed.
		/// </summary>
		/// <param name="progress">Optional: receives the fraction of projects evaluated and the most recent one's name.</param>
		public static ProjectPreEvaluation Run(ProjectCollection projectCollection, IReadOnlyList<Request> requests, IProgressMonitor progress, CancellationToken cancellationToken)
		{
			var preEvaluation = new ProjectPreEvaluation(projectCollection);
			active.AddOrUpdate(projectCollection, preEvaluation);
			try {
				preEvaluation.EvaluateAll(requests, progress, cancellationToken);
			} catch {
				preEvaluation.Dispose(); // cancelled: unload what was evaluated so far
				throw;
			}
			return preEvaluation;
		}

		/// <summary>
		/// Returns, and forgets, the pre-evaluated project for <paramref name="rootElement"/> when it was made from
		/// that very XML with exactly <paramref name="globalProperties"/>; otherwise null, and the caller evaluates
		/// as usual. <paramref name="evaluationStartedUtc"/> becomes the time the pre-evaluation began, so a file
		/// added since then still counts as newer than the evaluation.
		/// </summary>
		internal static Microsoft.Build.Evaluation.Project Take(ProjectCollection projectCollection, ProjectRootElement rootElement, IDictionary<string, string> globalProperties, ref DateTime evaluationStartedUtc)
		{
			return active.TryGetValue(projectCollection, out var preEvaluation)
				? preEvaluation.TakeCore(rootElement, globalProperties, ref evaluationStartedUtc)
				: null;
		}

		void EvaluateAll(IReadOnlyList<Request> requests, IProgressMonitor progress, CancellationToken cancellationToken)
		{
			if (requests.Count == 0)
				return;
			int evaluated = 0;
			var progressGate = new object(); // the workers report concurrently; a monitor's TaskName is not thread-safe
			void ReportEvaluated(Request request)
			{
				if (progress == null)
					return;
				lock (progressGate) {
					evaluated++;
					progress.Progress = (double)evaluated / requests.Count;
					progress.TaskName = "Evaluating projects " + evaluated + "/" + requests.Count + ": "
						+ System.IO.Path.GetFileNameWithoutExtension(request.FileName);
				}
			}
			MSBuildInternals.InitializeMSBuildEnvironment();
			var context = MSBuildInternals.SharedEvaluationContext;
			var options = new ParallelOptions { MaxDegreeOfParallelism = DegreeOfParallelism, CancellationToken = cancellationToken };
			var duplicates = new ConcurrentBag<Microsoft.Build.Evaluation.Project>();
			try {
				EvaluateConcurrently();
			} finally {
				// Unloading takes SolutionProjectCollectionLock; do it after the concurrent phase, which only evaluates.
				foreach (var duplicate in duplicates)
					MSBuildInternals.UnloadProject(projectCollection, duplicate);
			}

			void EvaluateConcurrently() => Parallel.ForEach(requests, options, request => {
				try {
					var startedUtc = DateTime.UtcNow;
					var rootElement = ProjectRootElement.Open(request.FileName, projectCollection);
					var project = Microsoft.Build.Evaluation.Project.FromProjectRootElement(rootElement, new ProjectOptions {
						GlobalProperties = request.GlobalProperties.ToDictionary(kv => kv.Key, kv => kv.Value, MSBuildInternals.PropertyNameComparer),
						ToolsVersion = MSBuildInternals.ResolveSupportedToolsVersion(projectCollection, rootElement),
						ProjectCollection = projectCollection,
						EvaluationContext = context
					});
					if (!evaluations.TryAdd(rootElement.FullPath, new Evaluation(project, request.GlobalProperties, startedUtc)))
						duplicates.Add(project); // listed twice: keep the first
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					// Missing file, invalid XML, unresolvable SDK, ...: the project's constructor reports it.
					LoggingService.Debug("Pre-evaluation skipped " + request.FileName + ": " + ex.Message);
				}
				ReportEvaluated(request);
			});
		}

		Microsoft.Build.Evaluation.Project TakeCore(ProjectRootElement rootElement, IDictionary<string, string> globalProperties, ref DateTime evaluationStartedUtc)
		{
			if (!evaluations.TryRemove(rootElement.FullPath, out var evaluation))
				return null;
			bool matches = evaluation.Project.Xml == rootElement
				&& evaluation.GlobalProperties.Count == globalProperties.Count
				&& evaluation.GlobalProperties.All(kv => globalProperties.TryGetValue(kv.Key, out var value) && value == kv.Value);
			if (!matches) {
				Interlocked.Increment(ref mismatched);
				MSBuildInternals.UnloadProject(projectCollection, evaluation.Project);
				return null;
			}
			Interlocked.Increment(ref reused);
			evaluation.Project.ReevaluateIfNecessary(); // a no-op unless the XML was edited in memory since
			evaluationStartedUtc = evaluation.StartedUtc;
			return evaluation.Project;
		}

		public void Dispose()
		{
			if (active.TryGetValue(projectCollection, out var registered) && registered == this)
				active.Remove(projectCollection);
			int unused = 0;
			foreach (var path in evaluations.Keys.ToList()) {
				if (evaluations.TryRemove(path, out var evaluation)) {
					MSBuildInternals.UnloadProject(projectCollection, evaluation.Project);
					unused++;
				}
			}
			LoggingService.Info("Parallel pre-evaluation: " + reused + " reused, " + mismatched + " mismatched, " + unused + " unused");
		}
	}
}
