using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using NuGet.Versioning;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Add-in versions follow Semantic Versioning 2.0.0 (https://semver.org/spec/v2.0.0.html) through NuGet's <see cref="SemanticVersion"/>
/// and <see cref="VersionComparer.Default"/>; nothing here is our own parser. The first two tests pin the behaviour of that library that the
/// release logic relies on (a package upgrade that changed it would show here); the rest test the logic built on it.
/// Known differences of NuGet from the specification, not relied on: pre-release labels compare case-insensitively (the specification:
/// ASCII, so "alpha" and "Alpha" differ) and numeric identifiers that do not fit an integer compare wrongly.
/// </summary>
public class SemVerTests
{
	// Its own add-in tree: AddInTestSupport.LoadAddIn needs the global SD.AddInTree service, which other test classes set up.
	static AddIn LoadAddIn(string manifestText) => AddIn.Load(new AddInTreeImpl(null), new StringReader(manifestText));

	[Theory]
	[InlineData("1.2")]
	[InlineData("1.2.3.4")]
	[InlineData("v1.2.3")]
	[InlineData(" 1.2.3")]
	[InlineData("01.2.3")]
	[InlineData("1.2.3-")]
	[InlineData("1.2.3-01")]
	[InlineData("1.2.3-a..b")]
	[InlineData("1.2.3-a_b")]
	[InlineData("1.2.3+")]
	[InlineData("2147483648.0.0")]
	[InlineData("")]
	public void SemanticVersion_RejectsWhatIsNotAStrictSemanticVersion(string text)
	{
		Assert.False(SemanticVersion.TryParse(text, out _));
	}

	/// <summary>The ordering the specification gives (§11): each entry is lower than the next.</summary>
	[Fact]
	public void VersionComparer_FollowsTheSpecificationPrecedenceExample()
	{
		string[] ascending =
		{
			"1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
			"2.0.0", "2.1.0", "2.1.1",
		};
		for (int i = 0; i < ascending.Length; i++) {
			for (int j = 0; j < ascending.Length; j++) {
				Assert.Equal(i.CompareTo(j), Math.Sign(VersionComparer.Default.Compare(SemanticVersion.Parse(ascending[i]), SemanticVersion.Parse(ascending[j]))));
			}
		}
		Assert.Equal(0, VersionComparer.Default.Compare(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"))); // build metadata is ignored
		Assert.Equal(-1, Math.Sign(VersionComparer.Default.Compare(SemanticVersion.Parse("0.1.1-rc1"), SemanticVersion.Parse("0.1.1"))));
		Assert.Equal(-1, Math.Sign(VersionComparer.Default.Compare(SemanticVersion.Parse("1.9.0"), SemanticVersion.Parse("1.10.0")))); // numeric, not textual
	}

	// ---- the release logic built on it ----------------------------------------------------------------------

	[Fact]
	public void Latest_RanksAReleaseAbovePreReleasesOfTheSameVersion_AndPreReleasesInPrecedenceOrder()
	{
		var releases = new[] {
			AddInTestSupport.Release(tag: "v1.0.0-rc.2"), AddInTestSupport.Release(tag: "v1.0.0-rc.10"),
			AddInTestSupport.Release(tag: "v1.0.0-beta.11"), AddInTestSupport.Release(tag: "v0.9.0"),
		};

		Assert.Equal("v1.0.0-rc.10", ReleaseVersioning.Latest(releases, includePrereleases: true).Tag);
		Assert.Equal("v0.9.0", ReleaseVersioning.Latest(releases, includePrereleases: false).Tag);

		var withFinal = releases.Append(AddInTestSupport.Release(tag: "v1.0.0"));
		Assert.Equal("v1.0.0", ReleaseVersioning.Latest(withFinal, includePrereleases: true).Tag);
	}

	[Fact]
	public void IsNewer_OffersTheFinalReleaseToAnInstalledPreRelease_ButNotTheOtherWayRound()
	{
		var rc = LoadAddIn("<AddIn name='A'><Manifest><Identity name='A' version='0.1.1-rc1'/></Manifest></AddIn>");
		var final = LoadAddIn("<AddIn name='A'><Manifest><Identity name='A' version='0.1.1'/></Manifest></AddIn>");

		Assert.True(ReleaseVersioning.IsNewer(rc, AddInTestSupport.Release(tag: "v0.1.1")));
		Assert.True(ReleaseVersioning.IsNewer(rc, AddInTestSupport.Release(tag: "v0.1.1-rc2")));
		Assert.False(ReleaseVersioning.IsNewer(rc, AddInTestSupport.Release(tag: "v0.1.1-rc1")));
		Assert.False(ReleaseVersioning.IsNewer(final, AddInTestSupport.Release(tag: "v0.1.1-rc2")));
		Assert.False(ReleaseVersioning.IsNewer(final, AddInTestSupport.Release(tag: "v0.1.1")));
		Assert.True(ReleaseVersioning.IsNewer(final, AddInTestSupport.Release(tag: "v0.1.2-rc1")));
	}

	[Fact]
	public void IsNewer_AnInstalledVersionThatIsNotASemanticVersionCountsByItsNumericParts()
	{
		var fourParts = LoadAddIn("<AddIn name='A'><Manifest><Identity name='A' version='1.2.3.4'/></Manifest></AddIn>");

		Assert.Null(fourParts.SemanticVersion);
		Assert.True(ReleaseVersioning.IsNewer(fourParts, AddInTestSupport.Release(tag: "v1.2.4")));
		Assert.False(ReleaseVersioning.IsNewer(fourParts, AddInTestSupport.Release(tag: "v1.2.3")));
	}

	[Fact]
	public void AddInRelease_VersionIsTheNumericPartOfTheTag()
	{
		var release = AddInTestSupport.Release(tag: "v1.20.300-rc.1+build.5");

		Assert.Equal(new Version(1, 20, 300), release.Version);
		Assert.True(release.IsPrerelease);
		Assert.Equal(new[] { "rc", "1" }, release.SemanticVersion.ReleaseLabels);
	}
}
