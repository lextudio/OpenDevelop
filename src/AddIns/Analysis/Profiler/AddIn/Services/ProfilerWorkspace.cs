using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using OpenDevelop.Profiler.Sessions;

namespace OpenDevelop.Profiler.AddIn.Services;

/// <summary>Where captures live and what the open solution says about "my code".</summary>
internal static class ProfilerWorkspace
{
	/// <summary>
	/// <c>.od/profiling</c> beside the open solution, or under the user's settings folder when
	/// no solution is open (attaching to a process from an empty IDE). Nothing is added to the solution
	/// or its ignore files.
	/// </summary>
	public static ProfilingSessionStore OpenStore()
	{
		var solution = ProjectService.OpenSolution;
		var root = ProfilerSettings.StoreFolder is { Length: > 0 } custom
			? custom
			: solution is null
			? Path.Combine(PropertyService.ConfigDirectory.ToString(), "profiling")
			: Path.Combine(solution.Directory.ToString(), ".od", "profiling");
		if (solution is not null && string.IsNullOrEmpty(ProfilerSettings.StoreFolder))
			MigrateLegacyStore(solution.Directory.ToString(), root);
		return new ProfilingSessionStore(root);
	}

	/// <summary>
	/// Captures used to live in <c>.opendevelop/profiling</c>, a second per-solution folder next to the
	/// <c>.od</c> one every other cache uses. Move an existing one across so earlier captures stay listed.
	/// Skipped when the new folder already exists, so nothing is ever merged or overwritten.
	/// </summary>
	static void MigrateLegacyStore(string solutionDirectory, string root)
	{
		var legacyParent = Path.Combine(solutionDirectory, ".opendevelop");
		var legacy = Path.Combine(legacyParent, "profiling");
		if (!Directory.Exists(legacy) || Directory.Exists(root))
			return;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(root)!);
			Directory.Move(legacy, root);
			if (!Directory.EnumerateFileSystemEntries(legacyParent).Any())
				Directory.Delete(legacyParent);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A capture open in a viewer, say: the old folder stays where it is and the next open tries again.
		}
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
