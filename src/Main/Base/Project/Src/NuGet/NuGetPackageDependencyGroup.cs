using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed record NuGetPackageDependencyGroup(
		string TargetFramework,
		IReadOnlyList<NuGetPackageDependencyItem> Dependencies);
}
