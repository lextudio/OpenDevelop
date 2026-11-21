using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AndroidSdkManager;
using ICSharpCode.ILSpyX.TreeView;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// sdkmanager reports one flat list ordered by package id, which is not a usable tree order: "platforms;android-9"
/// sorts after "platforms;android-34" as text, and the Tools families would be interleaved with bare rows. These tests
/// pin the explicit orderings both tabs use - API levels newest first, tool families in a fixed order, newest version
/// first inside a group - and that no package is dropped or duplicated between the tabs.
/// </summary>
public sealed class SdkPackageTreeBuilderTests
{
	static SdkPackage Pkg(string id, string installed = null, string available = null)
	{
		return new SdkPackage {
			Id = id,
			DisplayName = id,
			InstalledVersion = installed ?? string.Empty,
			AvailableVersion = available ?? string.Empty,
			IsInstalled = installed != null,
			HasUpdate = installed != null && available != null,
		};
	}

	static List<string> Labels(IEnumerable<SharpTreeNode> nodes)
	{
		return nodes.Select(node => node.Text as string).ToList();
	}

	[Fact]
	public void PlatformsRoot_ListsApiLevelsNewestFirst()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-9"), Pkg("platforms;android-10"), Pkg("platforms;android-34"),
		});

		Assert.Equal(
			new[] { "Android 14.0 (API 34)", "API Level 10", "API Level 9" },
			Labels(root.Children));
	}

	[Fact]
	public void PlatformsRoot_OrdersDottedLevelsNumerically()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-37.0"), Pkg("platforms;android-37.2"), Pkg("platforms;android-36.1"),
		});

		// "37.0" and "37" are the same level, so it is shown as "37"; the newest minor version comes first.
		Assert.Equal(
			new[] { "API Level 37.2", "API Level 37", "API Level 36.1" },
			Labels(root.Children));
	}

	[Fact]
	public void PlatformsRoot_MergesAddOnsIntoTheirApiLevelByGoogleNumber()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-15"), Pkg("add-ons;addon-google_apis-google-15"),
		});

		var group = Assert.Single(root.Children);
		Assert.Equal("API Level 15", group.Text as string);
		Assert.Equal(2, group.Children.Count);
		// The platform itself comes before its add-ons.
		Assert.Equal("platforms;android-15", ((SdkPackageTreeNode)group.Children[0]).Package.Id);
		Assert.Equal("add-ons;addon-google_apis-google-15", ((SdkPackageTreeNode)group.Children[1]).Package.Id);
	}

	[Fact]
	public void PlatformsRoot_PutsPlatformsBeforeSourcesAndSystemImages()
	{
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("system-images;android-33;google_apis;arm64-v8a"),
			Pkg("sources;android-33"),
			Pkg("platforms;android-33"),
		});

		var group = Assert.Single(root.Children);
		Assert.Equal(
			new[] { "platforms;android-33", "sources;android-33", "system-images;android-33;google_apis;arm64-v8a" },
			group.Children.Select(node => ((SdkPackageTreeNode)node).Package.Id));
	}

	[Fact]
	public void PlatformsRoot_ListsPreviewChannelsAboveReleasedLevels()
	{
		// Current sdkmanager ships android-CANARY / android-canary-<date> packages, which carry no numeric level.
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("platforms;android-CANARY"), Pkg("platforms;android-canary-20260909"),
			Pkg("system-images;android-canary-20260909;google_apis;arm64-v8a"),
		});

		Assert.Equal(
			new[] { "Preview (canary-20260909)", "Preview (CANARY)", "Android 14.0 (API 34)" },
			Labels(root.Children));
		// The canary system images join the same preview group as the canary platform.
		Assert.Equal(2, root.Children[0].Children.Count);
	}

	[Fact]
	public void PlatformsRoot_KeepsUnclassifiablePlatformPackagesInAnOtherGroup()
	{
		// Nothing guarantees that every future platform-ish id carries a level the three regexes know.
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("add-ons;vendor-addon-1"),
		});

		Assert.Equal(new[] { "Android 14.0 (API 34)", "Other" }, Labels(root.Children));
		Assert.Single(root.Children[1].Children);
	}

	[Fact]
	public void GroupCheckState_FollowsTheLeaves_InsteadOfStartingIndeterminate()
	{
		// SharpTreeNode never aggregates on its own, so without an explicit pass every group would stay null and the
		// checkbox would draw the indeterminate box - the solid square seen in both themes.
		var none = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("sources;android-34"),
		});
		Assert.False(none.Children[0].IsChecked);

		var some = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34", installed: "2"), Pkg("sources;android-34"),
		});
		Assert.Null(some.Children[0].IsChecked);

		var all = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34", installed: "2"), Pkg("sources;android-34", installed: "1"),
		});
		Assert.True(all.Children[0].IsChecked);
	}

	[Fact]
	public void GroupState_FollowsLeafToggles_AndAGroupToggleCascadesToTheLeaves()
	{
		// The build-time pass is not enough: when a user ticks a leaf, the group must recompute, and ticking a group must
		// reach its leaves. This also proves the recompute cannot recurse forever (it would overflow the stack here).
		var root = SdkPackageTreeBuilder.BuildPlatformsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("sources;android-34"),
		});
		var group = root.Children[0];
		var leaves = group.Children.Cast<SdkPackageTreeNode>().ToList();
		Assert.False(group.IsChecked);

		leaves[0].IsChecked = true;
		Assert.Null(group.IsChecked);

		leaves[1].IsChecked = true;
		Assert.True(group.IsChecked);

		group.IsChecked = false;
		Assert.All(leaves, leaf => Assert.False(leaf.IsChecked));
		Assert.False(group.IsChecked);
	}

	[Fact]
	public void EmptyGroup_IsUnchecked_RatherThanIndeterminate()
	{
		var group = new SdkGroupNode("empty");
		group.RefreshCheckState();
		Assert.False(group.IsChecked);
	}

	[Fact]
	public void ToolsGroupCheckState_FollowsTheLeaves()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("build-tools;34.0.0", installed: "34.0.0"), Pkg("build-tools;35.0.0"),
		});
		Assert.Null(root.Children[0].IsChecked);
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
	public void ToolsRoot_ListsKnownFamiliesInAFixedOrder_ThenUnknownFamilies()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("ndk;27.2.12479018"),
			Pkg("emulator"),
			Pkg("extras;google;usb_driver"),
			Pkg("skiaparser;1"),
			Pkg("build-tools;34.0.0"),
			Pkg("platform-tools"),
			Pkg("cmdline-tools;13.0"),
			Pkg("vendor-tools;2.0"),
		});

		Assert.Equal(
			new[] {
				"Android SDK Build-Tools",
				"Android SDK Platform-Tools",
				"Android SDK Command-line Tools",
				"Android Emulator",
				"NDK (Side by side)",
				"Skia parser",
				"Extras",
				// An unknown family still gets its own group, after the known ones and in name order.
				"vendor-tools",
			},
			Labels(root.Children));
	}

	[Fact]
	public void ToolsRoot_SortsEachFamilyNewestVersionFirst()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("build-tools;34.0.0"), Pkg("build-tools;9.0.0"), Pkg("build-tools;36.0.1"),
		});

		var group = Assert.Single(root.Children);
		Assert.Equal(
			new[] { "build-tools;36.0.1", "build-tools;34.0.0", "build-tools;9.0.0" },
			group.Children.Select(node => ((SdkPackageTreeNode)node).Package.Id));
	}

	[Fact]
	public void ToolsRoot_UsesTheAvailableVersionForAnUpdatedPackage()
	{
		// An installed package whose update is newer must sort by the newer version.
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("build-tools;34.0.0", installed: "34.0.0"),
			Pkg("build-tools;35.0.0", installed: "34.0.0", available: "36.0.0"),
		});

		var group = Assert.Single(root.Children);
		Assert.Equal("build-tools;35.0.0", ((SdkPackageTreeNode)group.Children[0]).Package.Id);
	}

	[Fact]
	public void ToolsRoot_SortsAReleaseAboveItsPrereleases()
	{
		// sdkmanager reports prereleases as "37.0.0 rc2" / "37.0.0-rc2"; the plain release is the newest.
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("build-tools;37.0.0-rc2", available: "37.0.0 rc2"),
			Pkg("build-tools;37.0.0", available: "37.0.0"),
			Pkg("build-tools;37.0.0-rc1", available: "37.0.0 rc1"),
		});

		var group = Assert.Single(root.Children);
		Assert.Equal(
			new[] { "build-tools;37.0.0", "build-tools;37.0.0-rc2", "build-tools;37.0.0-rc1" },
			group.Children.Select(node => ((SdkPackageTreeNode)node).Package.Id));
	}

	[Fact]
	public void ToolsRoot_NeverLeavesPlatformPackages()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] {
			Pkg("platforms;android-34"), Pkg("system-images;android-34;google_apis;arm64-v8a"),
			Pkg("emulator"),
		});

		Assert.Equal(new[] { "Android Emulator" }, Labels(root.Children));
	}

	[Fact]
	public void ToolsRoot_WithOnlyPlatformPackages_IsEmpty()
	{
		var root = SdkPackageTreeBuilder.BuildToolsRoot(new[] { Pkg("platforms;android-34") });
		Assert.Empty(root.Children);
	}
}
