using ICSharpCode.Core;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// A release tag may be <c>vMAJOR.MINOR.PATCH-suffix</c> and the package manifest's Identity version must equal the tag
/// (doc/technotes/addin-github-releases.md), so the manifest reader has to accept the suffix. It is only the Identity: in a
/// Dependency or Conflict a '-' still separates a version range.
/// </summary>
public class AddInManifestVersionTests
{
	static AddIn Load(string manifest) =>
		AddIn.Load(new AddInTreeImpl(null), new StringReader("<AddIn name='Test.Versions'><Manifest>" + manifest + "</Manifest></AddIn>"));

	[Theory]
	[InlineData("1.2.3", "1.2.3")]
	[InlineData("0.1.1-rc1", "0.1.1")]
	[InlineData("2.0.0-beta.2", "2.0.0")]
	[InlineData("1.0-preview", "1.0")]
	public void IdentityVersion_KeepsTheNumericPartAndDropsAPreReleaseSuffix(string written, string expected)
	{
		var addIn = Load("<Identity name='Test.Versions' version='" + written + "'/>");

		Assert.Equal(new Version(expected), addIn.Version);
	}

	[Theory]
	[InlineData("0.1.1-rc1", "0.1.1-rc1")]
	[InlineData("1.2.3", "1.2.3")]
	[InlineData("1.0.0-rc.1+build.5", "1.0.0-rc.1+build.5")]
	[InlineData("1.2.3+meta", "1.2.3+meta")]
	[InlineData("1.0-preview", null)] // not a Semantic Version: only the numeric Version is known
	[InlineData("1.2.3.4", null)]
	[InlineData("5.9", null)]
	public void IdentityVersion_KeepsTheFullSemanticVersionWhenItIsOne(string written, string expected)
	{
		var addIn = Load("<Identity name='Test.Versions' version='" + written + "'/>");

		Assert.Equal(expected, addIn.SemanticVersion?.ToFullString());
	}

	[Fact]
	public void IdentityVersion_AWrongNumericPartIsStillRejected()
	{
		Assert.ThrowsAny<Exception>(() => Load("<Identity name='Test.Versions' version='x.y-rc1'/>"));
		Assert.ThrowsAny<Exception>(() => Load("<Identity name='Test.Versions' version='-rc1'/>"));
	}

	[Fact]
	public void DependencyVersion_StillReadsADashAsARange()
	{
		var addIn = Load("<Identity name='Test.Versions' version='0.1.1-rc1'/><Dependency addin='SharpDevelop' version='5.9-5.10'/>");

		var dependency = Assert.Single(addIn.Manifest.Dependencies);
		Assert.Equal(new Version(5, 9), dependency.MinimumVersion);
		Assert.Equal(new Version(5, 10), dependency.MaximumVersion);
	}
}
