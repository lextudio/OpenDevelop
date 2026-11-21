using System.Linq;
using ICSharpCode.AndroidSdkManager;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Platforms and Tools trees are built only from SdkManagerOutputParser output, and the parser understands the
/// pipe-delimited tables that plain `sdkmanager --list` prints. Current cmdline-tools switch `--verbose` to a
/// block-per-package format that has no pipes, so AndroidSdkManagerService must keep asking for `--list`; an empty
/// package list is the visible symptom when that flag regresses.
/// </summary>
public sealed class SdkManagerOutputParserTests
{
	const string ListTable = """
Loading package information...
Installed packages:
  Path                                  | Version | Description                    | Location
  -------                               | ------- | -------                        | -------
  build-tools;34.0.0                    | 34.0.0  | Android SDK Build-Tools 34     | build-tools/34.0.0
  platforms;android-36                  | 2       | Android SDK Platform 36        | platforms/android-36
Available Packages:
  Path                                  | Version | Description
  -------                               | ------- | -------
  platforms;android-10                  | 2       | Android SDK Platform 10
  build-tools;34.0.0                    | 35.0.0  | Android SDK Build-Tools 34
Available Updates:
  ID                                    | Installed | Available
  -------                               | -------   | -------
  build-tools;34.0.0                    | 34.0.0    | 35.0.0
""";

	const string VerboseBlocks = """
Installed packages:
--------------------------------------
build-tools;34.0.0
    Description:        Android SDK Build-Tools 34
    Version:            34.0.0
    Installed Location: /Users/someone/Library/Android/sdk/build-tools/34.0.0
""";

	[Fact]
	public void ListTable_YieldsInstalledAvailableAndUpdatedPackages()
	{
		var packages = SdkManagerOutputParser.Parse(ListTable);

		Assert.Equal(3, packages.Count);

		var buildTools = packages.Single(p => p.Id == "build-tools;34.0.0");
		Assert.True(buildTools.IsInstalled);
		Assert.Equal("34.0.0", buildTools.InstalledVersion);
		Assert.True(buildTools.HasUpdate);
		Assert.Equal("35.0.0", buildTools.AvailableVersion);

		var platform36 = packages.Single(p => p.Id == "platforms;android-36");
		Assert.True(platform36.IsInstalled);
		Assert.False(platform36.HasUpdate);

		var platform10 = packages.Single(p => p.Id == "platforms;android-10");
		Assert.False(platform10.IsInstalled);
		Assert.Equal("2", platform10.AvailableVersion);
	}

	[Fact]
	public void VerboseBlocks_ParseToNothing_WhichIsWhyTheServiceAsksForPlainList()
	{
		// Documents the reason for `--list` in AndroidSdkManagerService.ListPackagesAsync: the verbose block format has
		// no pipe-delimited rows, so the parser drops everything and both trees render empty.
		Assert.Empty(SdkManagerOutputParser.Parse(VerboseBlocks));
	}

	[Fact]
	public void EmptyOrNullOutput_YieldsNoPackages()
	{
		Assert.Empty(SdkManagerOutputParser.Parse(string.Empty));
		Assert.Empty(SdkManagerOutputParser.Parse(null));
	}
}
