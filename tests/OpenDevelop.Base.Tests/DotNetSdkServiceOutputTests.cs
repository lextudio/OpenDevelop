using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project.Sdk;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Output-pad description reads the IDE's SDK settings, so it needs the property service. Kept
/// apart from <see cref="DotNetSdkServiceTests"/> so that class's tests keep running without one.
/// </summary>
public sealed class DotNetSdkServiceOutputTests : IDisposable
{
	public DotNetSdkServiceOutputTests() => SD.InitializeForUnitTests();

	public void Dispose() => SD.TearDownForUnitTests();

	[Fact]
	public void DescribeChoiceForOutput_PrintsTheListItWasGiven()
	{
		var sdk = new DotNetSdkInfo {
			Label = "/opt/given",
			RootPath = "/opt/given",
			DotnetExecutablePath = "/opt/given/dotnet",
			InstalledSdkVersions = new List<string> { "10.0.302" },
			PreviewSdkVersions = new List<string>(),
			HighestSdkVersion = "10.0.302",
			Origin = DotNetSdkOrigin.System,
			Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
		};
		var choice = new DotNetSdkService.SdkChoice { Sdk = sdk, Version = "10.0.302", IsDefault = true };

		string text = DotNetSdkService.DescribeChoiceForOutput(choice, new[] { sdk });

		Assert.Contains("/opt/given (System, 10.0.302)", text);
		Assert.DoesNotContain("Could not describe", text);
	}
}
