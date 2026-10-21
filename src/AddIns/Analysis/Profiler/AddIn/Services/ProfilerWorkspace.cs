using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using OpenDevelop.Profiler.Sessions;

namespace OpenDevelop.Profiler.AddIn.Services;

/// <summary>Where captures live and what the open solution says about "my code".</summary>
internal static class ProfilerWorkspace
{
	/// <summary>
	/// <c>.opendevelop/profiling</c> beside the open solution, or under the user's settings folder when
	/// no solution is open (attaching to a process from an empty IDE). Nothing is added to the solution
	/// or its ignore files.
	/// </summary>
	public static ProfilingSessionStore OpenStore()
	{
		var solution = ProjectService.OpenSolution;
		var root = solution is null
			? Path.Combine(PropertyService.ConfigDirectory.ToString(), "profiling")
			: Path.Combine(solution.Directory.ToString(), ".opendevelop", "profiling");
		return new ProfilingSessionStore(root);
	}

	/// <summary>Assembly names of the open solution's projects, for Just My Code.</summary>
	public static IReadOnlyList<string> SolutionAssemblyNames()
	{
		var solution = ProjectService.OpenSolution;
		if (solution is null)
			return [];
		return solution.Projects
			.Select(project => project.AssemblyName)
			.Where(name => !string.IsNullOrEmpty(name))
			.ToList();
	}
}
