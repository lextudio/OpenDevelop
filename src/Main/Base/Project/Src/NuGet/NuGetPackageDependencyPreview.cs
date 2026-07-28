using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class NuGetPackageDependencyPreview
	{
		public NuGetPackageDependencyPreview(
			string packageId,
			string version,
			string sourceName,
			IReadOnlyList<NuGetPackageDependencyGroup> dependencyGroups)
		{
			PackageId = packageId;
			Version = version;
			SourceName = sourceName;
			DependencyGroups = dependencyGroups ?? new NuGetPackageDependencyGroup[0];
		}

		public string PackageId { get; }
		public string Version { get; }
		public string SourceName { get; }
		public IReadOnlyList<NuGetPackageDependencyGroup> DependencyGroups { get; }
		public bool HasDependencies => DependencyGroups.Any(group => group.Dependencies.Count > 0);
	}
}
