using ICSharpCode.PackageManagement;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ProjectTargetFrameworksTests
{
	[Fact]
	public void Parse_TargetFrameworks_ReturnsEachMoniker()
	{
		var frameworks = ProjectTargetFrameworks.Parse("net8.0; net48 ;", "net6.0");
		Assert.Equal(new[] { "net8.0", "net48" }, frameworks.Select(f => f.GetShortFolderName()).ToArray());
	}

	[Fact]
	public void Parse_NoTargetFrameworks_UsesTheSingleTargetFramework()
	{
		Assert.Equal(new[] { "net8.0" }, ProjectTargetFrameworks.Parse("", "net8.0").Select(f => f.GetShortFolderName()).ToArray());
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData("", "")]
	[InlineData("  ", "bogus-framework-name!")]
	public void Parse_NothingUsable_ReturnsAnEmptyList(string? frameworks, string? framework)
	{
		Assert.Empty(ProjectTargetFrameworks.Parse(frameworks, framework));
	}
}
