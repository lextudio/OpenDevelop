using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.NuGet
{
	/// <summary>
	/// Outcome of <see cref="NuGetPackageConflictResolutionService.ResolveAsync"/>: either a
	/// resolved, compatible set of package versions (direct + transitive) to apply, or a clear,
	/// user-facing description of why no compatible set could be found.
	/// </summary>
	public sealed class NuGetPackageConflictResolutionResult
	{
		public NuGetPackageConflictResolutionResult(
			bool succeeded,
			IReadOnlyList<NuGetResolvedPackage> resolvedPackages,
			IReadOnlyList<string> conflicts,
			string message)
		{
			Succeeded = succeeded;
			ResolvedPackages = resolvedPackages;
			Conflicts = conflicts;
			Message = message ?? string.Empty;
		}

		/// <summary>True when a mutually compatible version for every package (direct + transitive) was found.</summary>
		public bool Succeeded { get; }

		/// <summary>
		/// The full resolved closure (only populated when <see cref="Succeeded"/>): every package,
		/// direct or transitive, with the version the resolver settled on.
		/// </summary>
		public IReadOnlyList<NuGetResolvedPackage> ResolvedPackages { get; }

		/// <summary>Human-readable conflict descriptions (only populated when resolution failed).</summary>
		public IReadOnlyList<string> Conflicts { get; }

		/// <summary>One-line summary suitable for a status bar / dialog message, always populated.</summary>
		public string Message { get; }
	}

	/// <summary>A single package identity in a resolved closure, flagging whether it changed an already-installed version.</summary>
	public sealed class NuGetResolvedPackage
	{
		public NuGetResolvedPackage(string id, string resolvedVersion, string previouslyInstalledVersion)
		{
			Id = id;
			ResolvedVersion = resolvedVersion;
			PreviouslyInstalledVersion = previouslyInstalledVersion;
		}

		public string Id { get; }
		public string ResolvedVersion { get; }

		/// <summary>Null/empty when the package was not previously installed (i.e. it is a new transitive/direct addition).</summary>
		public string PreviouslyInstalledVersion { get; }

		/// <summary>True when the resolver had to move this package away from its previously installed, pinned version.</summary>
		public bool ChangedFromInstalled =>
			!string.IsNullOrEmpty(PreviouslyInstalledVersion) &&
			!string.Equals(PreviouslyInstalledVersion, ResolvedVersion, System.StringComparison.OrdinalIgnoreCase);
	}
}
