using ICSharpCode.SharpDevelop.Project;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>What a designer says when its build fails: the cause, not just the result code.</summary>
public sealed class DesignerBuildFailureTests
{
	static BuildResults Results(params BuildError[] errors)
	{
		var results = new BuildResults { Result = BuildResultCode.Error };
		foreach (var error in errors)
			results.Add(error);
		return results;
	}

	[Fact]
	public void NoReportedErrors_KeepsTheBareResultCode()
		=> Assert.Equal("Build returned Error.", DesignerBuildCoordinator.DescribeBuildFailure(Results()));

	[Fact]
	public void TheFirstErrorsAreIncluded_WithTheirCodes()
	{
		var text = DesignerBuildCoordinator.DescribeBuildFailure(Results(
			new BuildError("a.csproj", 1, 1, "NU1301", "The local source '/x' doesn't exist. ")));
		Assert.Equal("Build returned Error. NU1301: The local source '/x' doesn't exist.", text);
	}

	[Fact]
	public void WarningsAndMessagesAreIgnored_AndLongListsAreCut()
	{
		var warning = new BuildError("a.cs", 1, 1, "CS0168", "unused") { IsWarning = true };
		var message = new BuildError("a.cs", 1, 1, "", "info") { IsMessage = true };
		var errors = Enumerable.Range(1, 5).Select(i => new BuildError("a.cs", i, 1, "CS" + i, "bad " + i)).ToArray();
		var text = DesignerBuildCoordinator.DescribeBuildFailure(Results(errors.Prepend(message).Prepend(warning).ToArray()));
		Assert.Equal("Build returned Error. CS1: bad 1 | CS2: bad 2 | CS3: bad 3 (+2 more; see the Build output)", text);
	}
}
