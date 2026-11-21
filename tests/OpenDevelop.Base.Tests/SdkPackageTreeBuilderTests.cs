using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AndroidSdkManager;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The two tabs are built by these two methods. An empty Tools tab is what a grouping mistake looks like, because
/// BuildToolsRoot puts every non-platform package somewhere: known families under a named group, anything else at the
/// root. These assertions pin that, and the platform grouping (one node per API level, newest first).
/// </summary>
public sealed class SdkPackageTreeBuilderTests
{
	static SdkPackage Pkg(string id, bool installed = false)
	{
		return new SdkPackage { Id = id, DisplayName = id, IsInstalled = installed };
	}

	[Fact]
	public void PlatformsRoot_GroupsByApiLevel_NewestFirst()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("platforms;android-10"), Pkg("platforms;android-34"),
		});

		Assert.Equal(2, root.Children.Count);
		Assert.Equal("Android 14.0 (API 34)", root.Children[0].Text as string);
		Assert.Equal("API Level 10", root.Children[1].Text as string);
	}

	[Fact]
	public void PlatformsRoot_KeepsNonPlatformPackagesOut()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("build-tools;34.0.0"), Pkg("emulator"), Pkg("platforms;android-34"),
		});

		Assert.Single(root.Children);
	}

	[Fact]
	public void ToolsRoot_PutsEveryNonPlatformPackageSomewhere()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("build-tools;34.0.0", installed: true),
			Pkg("cmdline-tools;13.0", installed: true),
			Pkg("ndk;27.2.12479018", installed: true),
			Pkg("emulator", installed: true),
			Pkg("platform-tools", installed: true),
			Pkg("platforms;android-34"),
		});

		var texts = root.Children.Select(c => c.Text as string).ToList();
		Assert.Contains("Android SDK Build-Tools", texts);
		Assert.Contains("Android SDK Command-line Tools", texts);
		Assert.Contains("NDK (Side by side)", texts);
		// Families without a named group are added at the root so the tab is never empty for a real SDK.
		Assert.Contains("emulator", texts);
		Assert.Contains("platform-tools", texts);
		// Platform packages belong to the other tab.
		Assert.DoesNotContain("platforms;android-34", texts);

		var buildTools = root.Children.Single(c => (c.Text as string) == "Android SDK Build-Tools");
		Assert.Single(buildTools.Children);
	}

	[Fact]
	public void ToolsRoot_WithOnlyPlatformPackages_IsEmpty()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] { Pkg("platforms;android-34") });
		Assert.Empty(root.Children);
	}
}
