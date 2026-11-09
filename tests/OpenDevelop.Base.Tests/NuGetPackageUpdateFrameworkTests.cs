using ICSharpCode.SharpDevelop.NuGet;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class NuGetPackageUpdateFrameworkTests
{
	static NuGetFramework Net8 => NuGetFramework.Parse("net8.0");

	static NuGet.Protocol.Core.Types.IPackageSearchMetadata Metadata(params string[] dependencyFrameworks)
	{
		var groups = dependencyFrameworks.Select(f => new PackageDependencyGroup(NuGetFramework.Parse(f), Array.Empty<PackageDependency>()));
		return new PackageSearchMetadataBuilder.ClonedPackageSearchMetadata {
			Identity = new PackageIdentity("Pkg", NuGetVersion.Parse("2.0.0")),
			DependencySets = groups.ToList(),
		};
	}

	[Fact]
	public void SupportsFramework_ADependencyGroupFits_ReturnsTrue()
		=> Assert.True(NuGetPackageUpdateService.SupportsFramework(Metadata("netstandard2.0"), Net8));

	[Fact]
	public void SupportsFramework_NoDependencyGroupFits_ReturnsFalse()
		=> Assert.False(NuGetPackageUpdateService.SupportsFramework(Metadata("net48", "net462"), Net8));

	[Fact]
	public void SupportsFramework_NoGroupsDeclared_KeepsThePackage()
		=> Assert.True(NuGetPackageUpdateService.SupportsFramework(Metadata(), Net8));

	[Fact]
	public void SupportsFramework_UnknownProjectFramework_KeepsThePackage()
		=> Assert.True(NuGetPackageUpdateService.SupportsFramework(Metadata("net48"), null));

	[Fact]
	public void SupportsFrameworks_MultiTargeting_RequiresEveryFramework()
	{
		var frameworks = new[] { Net8, NuGetFramework.Parse("net48") };
		Assert.True(NuGetPackageUpdateService.SupportsFrameworks(Metadata("netstandard2.0"), frameworks));
		Assert.False(NuGetPackageUpdateService.SupportsFrameworks(Metadata("net8.0"), frameworks));
		Assert.True(NuGetPackageUpdateService.SupportsFrameworks(Metadata("net48"), null));
	}
}
