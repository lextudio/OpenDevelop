// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Evaluates a solution's MSBuild projects in parallel before the loaders construct them one by one
	/// on the UI thread (see <see cref="MSBuildInternals.PreEvaluateProjects"/>). Profiling Aspire.slnx
	/// (471 projects) showed ~92% of the frozen open spent in MSBuild evaluation, all on one core.
	/// </summary>
	static class SolutionPreEvaluation
	{
		/// <summary>
		/// Call after every <see cref="ProjectInformation.ActiveProjectConfiguration"/> is set and before the
		/// projects are loaded; dispose the result after the last one is loaded. Null when pre-evaluation is
		/// off (OD_PARALLEL_EVALUATION=0) or the solution has no MSBuild collection.
		/// </summary>
		public static IDisposable Begin(ISolution solution, IEnumerable<ProjectLoadInformation> projects, CancellationToken cancellationToken)
		{
			if (!ProjectPreEvaluation.Enabled || solution is not IMSBuildSolution msbuildSolution)
				return null;
			var requests = projects.Where(IsEvaluatedByMSBuildBasedProject).Select(CreateRequest).ToList();
			var watch = Stopwatch.StartNew();
			var preEvaluation = ProjectPreEvaluation.Run(msbuildSolution.MSBuildProjectCollection, requests, cancellationToken);
			PerfTimeline.Mark(PerfTimeline.SolutionOpen, "projects-pre-evaluated",
				requests.Count + " projects in " + watch.ElapsedMilliseconds + "ms, " + ProjectPreEvaluation.DegreeOfParallelism + " concurrent");
			return preEvaluation;
		}

		/// <summary>
		/// Language projects (.csproj, .vbproj, .fsproj, ...) evaluate through MSBuildBasedProject; .proj/.build
		/// traversal files are loaded by MSBuildFileProject, which does not.
		/// </summary>
		static bool IsEvaluatedByMSBuildBasedProject(ProjectLoadInformation info)
		{
			var config = info.ActiveProjectConfiguration;
			if (info.FileName == null || config.Configuration == null || config.Platform == null)
				return false;
			string extension = Path.GetExtension(info.FileName);
			return extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
				&& !extension.Equals(".proj", StringComparison.OrdinalIgnoreCase)
				&& File.Exists(info.FileName);
		}

		static ProjectPreEvaluation.Request CreateRequest(ProjectLoadInformation info)
		{
			var config = info.ActiveProjectConfiguration;
			return new ProjectPreEvaluation.Request(
				info.FileName,
				MSBuildBasedProject.CreateEvaluationGlobalProperties(config.Configuration, config.Platform));
		}
	}
}
