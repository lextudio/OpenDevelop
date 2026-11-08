using ICSharpCode.PackageManagement;
using Xunit;

namespace OpenDevelop.Base.Tests;

// The pure parts of the missing-package light bulb: which package ids an unresolved qualified name can
// stand for, how the name is read around a diagnostic, and which of several versions a solution "uses".
public sealed class MissingPackageNamesTests
{
	[Fact]
	public void PrefixesAreTriedLongestFirst()
	{
		Assert.Equal(
			new[] { "System.Text.Json", "System.Text", "System" },
			MissingPackageContextActionProvider.Prefixes("System.Text.Json"));
	}

	[Fact]
	public void ASingleSegmentIsItsOwnPrefix()
	{
		Assert.Equal(new[] { "Serilog" }, MissingPackageContextActionProvider.Prefixes("Serilog"));
	}

	[Theory]
	[InlineData("using Newtonsoft.Json;", 6, 6, "Newtonsoft.Json")]   // CS0246 position: start of the name
	[InlineData("using Newtonsoft.Json;", 17, 6, "Newtonsoft.Json")]  // CS0234 position: only the last segment
	[InlineData("using Newtonsoft.Json;", 10, 6, "Newtonsoft.Json")]  // anywhere inside
	[InlineData("var x = new Foo.Bar.Baz();", 20, 12, "Foo.Bar.Baz")]
	[InlineData("Serilog.Log.Information();", 0, 0, "Serilog.Log.Information")]
	public void TheQualifiedNameAroundAPositionIsRead(string text, int position, int expectedStart, string expectedName)
	{
		var (start, name) = MissingPackageContextActionProvider.ReadQualifiedName(text, position);

		Assert.Equal(expectedName, name);
		Assert.Equal(expectedStart, start);
	}

	[Fact]
	public void SurroundingDotsAreNotPartOfTheName()
	{
		var (start, name) = MissingPackageContextActionProvider.ReadQualifiedName("x = .Foo.", 5);

		Assert.Equal("Foo", name);
		Assert.Equal(5, start);
	}

	[Theory]
	[InlineData("13.0.3", "9.0.1", "13.0.3")]        // 13 > 9: a numeric, not a text, comparison
	[InlineData("1.0.0", "1.0.0-beta.1", "1.0.0")]   // a release beats its prerelease
	[InlineData("2.0.0-beta.2", "2.0.0-beta.10", "2.0.0-beta.10")]
	[InlineData("[1.0,2.0)", "1.5.0", "1.5.0")]      // a range does not parse as a version; a plain version wins
	public void TheHighestVersionWins(string first, string second, string expected)
	{
		Assert.Equal(expected, SolutionPackages.HighestVersion(new[] { first, second }));
		Assert.Equal(expected, SolutionPackages.HighestVersion(new[] { second, first }));
	}

	[Fact]
	public void WhenNothingParsesTheFirstVersionIsKept()
	{
		Assert.Equal("1.*", SolutionPackages.HighestVersion(new[] { "1.*", "[2.0,3.0)" }));
	}
}
