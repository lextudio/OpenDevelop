using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class BuildOptionsStopAfterFailureTests : IDisposable
{
	public BuildOptionsStopAfterFailureTests() => SD.InitializeForUnitTests();

	public void Dispose() => SD.TearDownForUnitTests();

	[Fact]
	public void StopSolutionBuildAfterFirstFailure_DefaultsToFalse()
	{
		Assert.False(BuildOptions.StopSolutionBuildAfterFirstFailure);
	}

	[Fact]
	public void StopSolutionBuildAfterFirstFailure_RoundTripsThroughPropertyService()
	{
		BuildOptions.StopSolutionBuildAfterFirstFailure = true;
		Assert.True(BuildOptions.StopSolutionBuildAfterFirstFailure);
		Assert.True(PropertyService.Get("SharpDevelop.StopSolutionBuildAfterFirstFailure", false));

		BuildOptions.StopSolutionBuildAfterFirstFailure = false;
		Assert.False(BuildOptions.StopSolutionBuildAfterFirstFailure);
	}

	[Theory]
	[InlineData(false, false, false)]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(true, true, true)]
	public void ShouldStop_RequiresBothTheSettingAndAPriorFailure(bool stopAfterFirstFailure, bool buildHasFailed, bool expected)
	{
		Assert.Equal(expected, SolutionBuildFailurePolicy.ShouldStop(stopAfterFirstFailure, buildHasFailed));
	}
}
