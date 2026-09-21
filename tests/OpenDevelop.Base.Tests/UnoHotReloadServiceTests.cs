using System.Diagnostics;
using ICSharpCode.SharpDevelop.Project.HotReload;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class UnoHotReloadServiceTests
{
	[Fact]
	public void CanConfigureLaunch_WithoutStartupProject_ReportsTheExplicitCommandPrecondition()
	{
		var supported = UnoHotReloadService.CanConfigureLaunch(null!, out var diagnostic);

		Assert.False(supported);
		Assert.Equal("No startup project is selected.", diagnostic);
	}

	[Fact]
	public void TryConfigureLaunch_WithoutProject_DoesNotChangeANormalLaunch()
	{
		var startInfo = new ProcessStartInfo("sample") { UseShellExecute = false };
		startInfo.Environment["EXISTING_TEST_VALUE"] = "preserved";

		var configured = UnoHotReloadService.TryConfigureLaunch(null!, startInfo);

		Assert.False(configured);
		Assert.Equal("preserved", startInfo.Environment["EXISTING_TEST_VALUE"]);
		Assert.False(startInfo.Environment.ContainsKey("UNO_DEV_SERVER_HOST"));
		Assert.False(startInfo.Environment.ContainsKey("UNO_DEV_SERVER_PORT"));
		Assert.False(startInfo.Environment.ContainsKey("DOTNET_MODIFIABLE_ASSEMBLIES"));
	}
}
