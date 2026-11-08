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

	[Fact]
	public void UnoSdkVersionMismatch_IsExplainedBeforeTheRawDiagnostic()
	{
		var text = DesignerBuildCoordinator.DescribeBuildFailure(Results(
			new BuildError("a.csproj", 1, 1, "UNOB0005", "The version of Uno.WinUI must match the version of the Uno.Sdk found in global.json.")));
		Assert.Equal("Build returned Error. Uno SDK/Uno.WinUI version mismatch (UNOB0005): align the Uno.Sdk and Uno.WinUI versions, restore the project, then reopen the designer. UNOB0005: The version of Uno.WinUI must match the version of the Uno.Sdk found in global.json.", text);
	}

	[Fact]
	public void TaskHostArchitectureMismatch_IsExplainedBeforeTheRawDiagnostic()
	{
		var text = DesignerBuildCoordinator.DescribeBuildFailure(Results(
			new BuildError("a.csproj", 1, 1, "MSB4216", "Could not run task because MSBuild could not create or connect to a task host with runtime NET and architecture arm64.")));
		Assert.Equal("Build returned Error. .NET SDK task-host failure (MSB4216): this project build could not start the task host for its requested architecture. Select the intended installed SDK in Tools > Options > .NET SDK (or clear the selection to use this session's startup SDK), then rebuild the project. MSB4216: Could not run task because MSBuild could not create or connect to a task host with runtime NET and architecture arm64.", text);
	}
}
