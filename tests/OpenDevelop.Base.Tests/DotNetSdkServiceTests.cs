using System.Runtime.InteropServices;
using ICSharpCode.SharpDevelop.Project.Sdk;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class DotNetSdkServiceTests
{
	[Fact]
	public void TryDescribeCustomRoot_ReportsMissingDotnetExecutable()
	{
		using var root = new TemporaryDirectory();

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.False(valid);
		Assert.Null(sdk);
		Assert.Contains("dotnet executable", error);
	}

	[Fact]
	public void TryDescribeCustomRoot_ReportsMissingSdkDirectory()
	{
		using var root = new TemporaryDirectory();
		File.WriteAllText(System.IO.Path.Combine(root.Path, DotnetExecutableName), "");

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.False(valid);
		Assert.Null(sdk);
		Assert.Contains("sdk folder", error);
	}

	[Fact]
	public void TryDescribeCustomRoot_ReturnsHighestStableSdk()
	{
		using var root = new TemporaryDirectory();
		File.WriteAllText(System.IO.Path.Combine(root.Path, DotnetExecutableName), "");
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "9.0.100"));
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "10.0.200-preview.1"));
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "10.0.201"));

		bool valid = DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error);

		Assert.True(valid, error);
		Assert.NotNull(sdk);
		Assert.Equal("10.0.201", sdk.HighestSdkVersion);
		Assert.Equal(DotNetSdkOrigin.Custom, sdk.Origin);
	}

	[Fact]
	public void DescribeAvailability_IsHealthy_WithAnInProcessSdk10OrNewerAndNoPreference()
	{
		var sdk = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, string.Empty);

		Assert.True(availability.IsHealthy);
		Assert.Equal("10.0.401", availability.SupportedSdkVersion);
		Assert.Same(sdk, availability.InProcessSdk);
		Assert.False(availability.PreferredRootSet);
	}

	[Fact]
	public void DescribeAvailability_AcceptsSdk11_WhenNoSdk10IsInstalled()
	{
		var sdk = Sdk("C:\\dotnet", "11.0.100", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, string.Empty);

		Assert.NotNull(availability.SupportedSdkRoot);
		Assert.Equal("11.0.100", availability.SupportedSdkVersion);
		Assert.True(availability.IsHealthy);
	}

	[Fact]
	public void DescribeAvailability_RejectsLegacySdk9()
	{
		var sdk = Sdk("C:\\dotnet", "9.0.203", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, string.Empty);

		Assert.Null(availability.SupportedSdkRoot);
		Assert.Null(availability.SupportedSdkVersion);
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void ResolveEffectiveSdk_IgnoresAnUnsupportedPreferredSdk9()
	{
		var sdk9 = Sdk("C:\\dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var sdk10 = Sdk("C:\\dotnet10", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);

		var effective = DotNetSdkService.ResolveEffectiveSdk(new[] { sdk9, sdk10 }, sdk9.RootPath);

		Assert.Same(sdk10, effective);
	}

	[Fact]
	public void ResolveEffectiveSdk_HonorsASupportedPreferredSdk()
	{
		var sdk10 = Sdk("C:\\dotnet10", "10.0.103", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var sdk11 = Sdk("C:\\dotnet11", "11.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);

		var effective = DotNetSdkService.ResolveEffectiveSdk(new[] { sdk10, sdk11 }, sdk10.RootPath);

		Assert.Same(sdk10, effective);
	}

	[Fact]
	public void ResolveEffectiveSdkForInProcessHosting_NeverFallsBackToSdk9()
	{
		var sdk9 = Sdk("C:\\dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);

		var effective = DotNetSdkService.ResolveEffectiveSdkForInProcessHosting(new[] { sdk9 }, sdk9.RootPath);

		Assert.Null(effective);
	}

	[Fact]
	public void DescribeAvailability_ReportsAPreferredRootThatIsGone()
	{
		var sdk = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, "D:\\moved-away");

		Assert.True(availability.PreferredRootSet);
		Assert.True(availability.PreferredRootMissing);
		Assert.Null(availability.PreferredSdk);
		// The rest of the machine is fine, but a preference that no longer resolves is still not
		// healthy: the user asked for an SDK the IDE cannot find.
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void DescribeAvailability_MatchesThePreferredRoot_IgnoringATrailingSeparator()
	{
		var sdk = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, "C:\\dotnet\\");

		Assert.True(availability.PreferredRootSet);
		Assert.False(availability.PreferredRootMissing);
		Assert.Same(sdk, availability.PreferredSdk);
		Assert.True(availability.IsHealthy);
	}

	[Fact]
	public void DescribeAvailability_ReportsNoInProcessSdk_WhenEvaluationAlreadyFailed()
	{
		var sdk = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(
			new[] { sdk }, string.Empty, noCompatibleInProcessSdkFound: true);

		Assert.Null(availability.InProcessSdk);
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void DescribeAvailability_ReportsNoInProcessSdk_WhenNothingMatchesThisProcessArchitecture()
	{
		// A different architecture is the one thing discovery cannot paper over: the in-process
		// NuGet SDK resolver is a ReadyToRun image built for its own SDK's architecture.
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var sdk = Sdk("C:\\dotnet", "10.0.401", other);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk }, string.Empty);

		Assert.Null(availability.InProcessSdk);
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void ResolveEffectiveSdk_ReturnsNull_WhenOnlyUnsupportedSdksExist()
	{
		var sdk9 = Sdk("C:\\dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);

		Assert.Null(DotNetSdkService.ResolveEffectiveSdk(new[] { sdk9 }, string.Empty));
		Assert.Null(DotNetSdkService.ResolveEffectiveSdk(new[] { sdk9 }, sdk9.RootPath));
	}

	[Fact]
	public void ResolveEffectiveSdk_UsesASupportedNonSystemSdk_BesideAnUnsupportedSystemSdk()
	{
		var system9 = Sdk("C:\\dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var bundled10 = Sdk("C:\\bundle", "10.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.Bundled);

		Assert.Same(bundled10, DotNetSdkService.ResolveEffectiveSdk(new[] { system9, bundled10 }, string.Empty));
		Assert.True(DotNetSdkService.DescribeAvailability(new[] { system9, bundled10 }, string.Empty).IsHealthy);
	}

	[Fact]
	public void ResolveEffectiveSdk_PrefersASupportedSystemSdk_OverABundledOne()
	{
		var system10 = Sdk("C:\\dotnet", "10.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var bundled11 = Sdk("C:\\bundle", "11.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.Bundled);

		Assert.Same(system10, DotNetSdkService.ResolveEffectiveSdk(new[] { bundled11, system10 }, string.Empty));
	}

	[Fact]
	public void DescribeAvailability_ReportsTheHighestSupportedSdkVersion()
	{
		var sdk = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture,
			versions: new[] { "9.0.300", "10.0.100", "10.0.401" });

		Assert.Equal("10.0.401", DotNetSdkService.DescribeAvailability(new[] { sdk }, string.Empty).SupportedSdkVersion);
	}

	[Fact]
	public void DescribeAvailability_FlagsAPreferredRootThatHoldsOnlyAnUnsupportedSdk()
	{
		var sdk9 = Sdk("C:\\dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture);
		var sdk10 = Sdk("C:\\dotnet10", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { sdk9, sdk10 }, sdk9.RootPath);

		Assert.True(availability.PreferredRootUnsupported);
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void DescribeAvailability_FlagsAPreferredRootWithASupportedSdkOfAnotherArchitecture()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var foreign10 = Sdk("C:\\dotnet-foreign", "10.0.401", other);
		var native10 = Sdk("C:\\dotnet-native", "10.0.401", RuntimeInformation.ProcessArchitecture);

		var availability = DotNetSdkService.DescribeAvailability(new[] { foreign10, native10 }, foreign10.RootPath);

		Assert.True(availability.PreferredRootUnsupported);
		Assert.False(availability.IsHealthy);
	}

	[Fact]
	public void SameRoot_IgnoresTrailingSeparatorsAndDotSegments()
	{
		// Built from a real temp path so the assertions mean the same on every OS ('\' is not a
		// separator outside Windows).
		string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "od-sameroot", "dotnet");
		string withDots = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "od-sameroot", "x", "..", "dotnet");

		Assert.True(DotNetSdkService.SameRoot(root + System.IO.Path.DirectorySeparatorChar, root));
		Assert.True(DotNetSdkService.SameRoot(root, withDots));
		Assert.False(DotNetSdkService.SameRoot(root, root + "2"));
		if (OperatingSystem.IsWindows())
			Assert.True(DotNetSdkService.SameRoot(root.Replace('\\', '/'), root));
	}

	[Theory]
	[InlineData("net11.0", 11)]
	[InlineData("net11.0-windows", 11)]
	[InlineData("NET10.0", 10)]
	[InlineData("netcoreapp3.1", 3)]
	[InlineData("net48", null)]
	[InlineData("netstandard2.0", null)]
	[InlineData("", null)]
	public void TargetFrameworkMajor_ReadsTheDotNetMajorVersion(string tfm, int? expected)
	{
		Assert.Equal(expected, DotNetSdkService.TargetFrameworkMajor(tfm));
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_KeepsTheEffectiveSdk_WhenItIsNewEnough()
	{
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System,
			previews: new[] { "11.0.100-rc.1.26425.128" });

		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10 }, string.Empty, new[] { "net10.0" });

		Assert.True(choice!.IsDefault);
		Assert.Equal("10.0.401", choice.Version);
		Assert.False(choice.IsPreview);
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_UsesAPreviewSdk_OnlyWhenNoStableOneSupportsTheFramework()
	{
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var dotnetup11 = Sdk("C:\\dotnetup", "11.0.100-rc.1.26425.128", RuntimeInformation.ProcessArchitecture,
			versions: Array.Empty<string>(), previews: new[] { "11.0.100-preview.7.1", "11.0.100-rc.1.26425.128" });

		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, dotnetup11 }, string.Empty, new[] { "net11.0" });

		Assert.False(choice!.IsDefault);
		Assert.True(choice.IsPreview);
		Assert.Same(dotnetup11, choice.Sdk);
		Assert.Equal("11.0.100-rc.1.26425.128", choice.Version);
		// ...and a preview-only root still never becomes the IDE's own SDK.
		Assert.Same(sdk10, DotNetSdkService.ResolveEffectiveSdk(new[] { sdk10, dotnetup11 }, string.Empty));
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_PrefersAStableSdkOfThatMajor_OverAPreview()
	{
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System,
			previews: new[] { "11.0.100-rc.1.26425.128" });
		var custom11 = Sdk("C:\\sdk11", "11.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.Custom);

		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, custom11 }, string.Empty, new[] { "net10.0", "net11.0" });

		Assert.Same(custom11, choice!.Sdk);
		Assert.Equal("11.0.100", choice.Version);
		Assert.False(choice.IsPreview);
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_KeepsTheEffectiveSdk_WhenNothingSupportsTheFramework()
	{
		// The build then reports NETSDK1045 itself, naming the missing SDK.
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);

		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10 }, string.Empty, new[] { "net11.0" });

		Assert.True(choice!.IsDefault);
		Assert.Same(sdk10, choice.Sdk);
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_ReturnsNull_WithoutAnySupportedSdk_NeverFallingBackToAnOlderOrPreviewOne()
	{
		// SDK 9 plus a preview SDK 11: no .NET 10+ stable SDK, so no choice at all (issue #19's rule).
		var sdk9 = Sdk("C:\\dotnet", "9.0.305", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System,
			previews: new[] { "11.0.100-rc.2.26504.105" });

		Assert.Null(DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk9 }, string.Empty, new[] { "net9.0" }));
		Assert.Null(DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk9 }, string.Empty, new[] { "net11.0" }));
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_KeepsTheEffectiveSdk_ForAFrameworkNoSdkVersionGates()
	{
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System,
			previews: new[] { "11.0.100-rc.2.26504.105" });

		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10 }, string.Empty, new[] { "net48" });

		Assert.True(choice!.IsDefault);
	}

	[Fact]
	public void ResolveSdkForTargetFrameworks_PrefersThePreferredRoot_ThenASystemOne()
	{
		var sdk10 = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var custom11 = Sdk("C:\\custom11", "11.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.Custom);
		var system11 = Sdk("C:\\system11", "11.0.100", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var all = new[] { sdk10, custom11, system11 };

		Assert.Same(system11, DotNetSdkService.ResolveSdkForTargetFrameworks(all, string.Empty, new[] { "net11.0" })!.Sdk);
		Assert.Same(custom11, DotNetSdkService.ResolveSdkForTargetFrameworks(all, custom11.RootPath, new[] { "net11.0" })!.Sdk);

		var previewA = Sdk("C:\\previewA", "11.0.100-rc.2.1", RuntimeInformation.ProcessArchitecture, versions: Array.Empty<string>(), previews: new[] { "11.0.100-rc.2.1" });
		var previewB = Sdk("C:\\previewB", "11.0.100-rc.1.1", RuntimeInformation.ProcessArchitecture, versions: Array.Empty<string>(), previews: new[] { "11.0.100-rc.1.1" });
		Assert.Same(previewA, DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, previewA, previewB }, string.Empty, new[] { "net11.0" })!.Sdk);
		Assert.Same(previewB, DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, previewA, previewB }, previewB.RootPath, new[] { "net11.0" })!.Sdk);

		// Same order for pre-releases: a System root beats a Custom one with a newer pre-release,
		// unless the Custom one is the preferred root.
		var systemPreview = Sdk("C:\\systemPreview", "11.0.100-rc.1.1", RuntimeInformation.ProcessArchitecture,
			versions: Array.Empty<string>(), origin: DotNetSdkOrigin.System, previews: new[] { "11.0.100-rc.1.1" });
		Assert.Same(systemPreview, DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, previewA, systemPreview }, string.Empty, new[] { "net11.0" })!.Sdk);
		Assert.Same(previewA, DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { sdk10, previewA, systemPreview }, previewA.RootPath, new[] { "net11.0" })!.Sdk);
	}

	[Theory]
	[InlineData("11.0.100-preview.7.1", "11.0.100-preview.10.1")]
	[InlineData("11.0.100-preview.7.1", "11.0.100-rc.1.1")]
	[InlineData("11.0.100-rc.1.26425.128", "11.0.100-rc.2.26504.105")]
	[InlineData("11.0.100-rc.2.1", "11.0.101-preview.1.1")]
	public void PrereleaseVersionComparer_OrdersReleasesChronologically(string older, string newer)
	{
		Assert.True(DotNetSdkService.PrereleaseVersionComparer.Instance.Compare(older, newer) < 0);
		Assert.True(DotNetSdkService.PrereleaseVersionComparer.Instance.Compare(newer, older) > 0);
	}

	[Fact]
	public void TryDescribeCustomRoot_AcceptsARootThatHoldsOnlyAPreviewSdk()
	{
		using var root = new TemporaryDirectory();
		File.WriteAllText(System.IO.Path.Combine(root.Path, DotnetExecutableName), "");
		Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "11.0.100-rc.1.26425.128"));

		Assert.True(DotNetSdkService.TryDescribeCustomRoot(root.Path, out var sdk, out string? error), error);
		Assert.Empty(sdk.InstalledSdkVersions);
		Assert.Equal(new[] { "11.0.100-rc.1.26425.128" }, sdk.PreviewSdkVersions);
		Assert.Equal("11.0.100-rc.1.26425.128", sdk.HighestSdkVersion);
	}

	[Theory]
	[InlineData("10.0.401", 10)]
	[InlineData("11.0.100-preview.7", 11)]
	[InlineData("9.0.316", 9)]
	[InlineData("", -1)]
	[InlineData("garbage", -1)]
	public void SdkMajorVersion_ReadsTheMajorVersion(string version, int expected)
	{
		Assert.Equal(expected, DotNetSdkService.SdkMajorVersion(version));
	}

	static DotNetSdkInfo Sdk(string rootPath, string highest, Architecture architecture,
		IEnumerable<string>? versions = null, DotNetSdkOrigin origin = DotNetSdkOrigin.Custom, IEnumerable<string>? previews = null)
		=> new DotNetSdkInfo {
			Label = rootPath,
			RootPath = rootPath,
			DotnetExecutablePath = System.IO.Path.Combine(rootPath, "dotnet"),
			InstalledSdkVersions = versions?.ToList() ?? new List<string> { highest },
			PreviewSdkVersions = previews?.ToList() ?? new List<string>(),
			HighestSdkVersion = highest,
			Origin = origin,
			Architecture = architecture
		};

	static string DotnetExecutableName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

	sealed class TemporaryDirectory : IDisposable
	{
		public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("opendevelop-sdk-test-").FullName;
		public string Path { get; }
		public void Dispose() => Directory.Delete(Path, recursive: true);
	}
}
