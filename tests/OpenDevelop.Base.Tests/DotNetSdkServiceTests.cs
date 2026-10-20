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
	public void DescribeChoice_NamesTheSdkTheReasonAndEveryDiscoveredRoot()
	{
		var old = Sdk("/home/u/.dotnet", "10.0.201", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var current = Sdk("/opt/dotnet", "10.0.302", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(new[] { old, current }, "", new[] { "net10.0" });

		string text = DotNetSdkService.DescribeChoice(choice, "", new[] { old, current });

		Assert.Contains("10.0.302 at /opt/dotnet", text);
		Assert.Contains("no SDK is selected in Options", text);
		Assert.Contains("/home/u/.dotnet (System, 10.0.201)", text);
		Assert.Contains("/opt/dotnet (System, 10.0.302)", text);
	}

	[Fact]
	public void DescribeChoice_SaysWhenTheSelectionIsHonouredOrUnusable()
	{
		var sdk9 = Sdk("/dotnet9", "9.0.203", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var sdk10 = Sdk("/dotnet10", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var discovered = new[] { sdk9, sdk10 };

		var honoured = DotNetSdkService.ResolveSdkForTargetFrameworks(discovered, sdk10.RootPath, new[] { "net10.0" });
		var ignored = DotNetSdkService.ResolveSdkForTargetFrameworks(discovered, sdk9.RootPath, new[] { "net10.0" });

		Assert.Contains("selected in Tools > Options", DotNetSdkService.DescribeChoice(honoured, sdk10.RootPath, discovered));
		Assert.Contains("is not usable", DotNetSdkService.DescribeChoice(ignored, sdk9.RootPath, discovered));
	}

	[Fact]
	public void DescribeChoice_SaysWhenTheProjectNeedsANewerSdkThanTheDefault()
	{
		var sdk10 = Sdk("/dotnet10", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var sdk11 = Sdk("/dotnetup", "11.0.100-rc.1.26425.128", RuntimeInformation.ProcessArchitecture,
			versions: Array.Empty<string>(), previews: new[] { "11.0.100-rc.1.26425.128" });
		var discovered = new[] { sdk10, sdk11 };
		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(discovered, "", new[] { "net11.0" });

		Assert.False(choice.IsDefault);
		Assert.Contains("targets a newer .NET", DotNetSdkService.DescribeChoice(choice, "", discovered));
	}

	[Fact]
	public void DescribeChoice_ReportsAChoiceWithoutAnSdk()
	{
		Assert.Contains("No .NET SDK", DotNetSdkService.DescribeChoice(new DotNetSdkService.SdkChoice(), "", Array.Empty<DotNetSdkInfo>()));
	}

	[Fact]
	public void SdkDiagnosticResources_MatchEnglishPlaceholdersAndFormat()
	{
		string root = ProductCopyrightTests.RepositoryRoot();
		string[] files = { "StringResources.resx", "StringResources.zh-Hans.resx", "StringResources.zh-Hant.resx" };
		// Every key the service looks up, read from its source so a new diagnostic is covered automatically.
		string[] keys = System.Text.RegularExpressions.Regex
			.Matches(File.ReadAllText(Path.Combine(root, "src", "Main", "Base", "Project", "Src", "Project", "Sdk", "DotNetSdkService.cs")),
				"\"(Build\\.Sdk\\.\\w+)\"")
			.Select(m => m.Groups[1].Value).Distinct().ToArray();
		Assert.True(keys.Length >= 8, "expected the Build.Sdk.* keys in DotNetSdkService.cs");
		// Read through an XML parser: that is the string the resource loader hands the runtime, with
		// every entity (named and numeric) already resolved.
		string Value(string file, string key)
		{
			var doc = System.Xml.Linq.XDocument.Load(Path.Combine(root, "data", "resources", file));
			string? value = doc.Root!.Elements("data")
				.FirstOrDefault(d => (string?)d.Attribute("name") == key)?.Element("value")?.Value;
			Assert.True(value != null, key + " missing from " + file);
			return value!;
		}
		// Proves the read went through the parser rather than the raw XML text: the numeric-entity
		// case depends on exactly this property.
		Assert.Contains(">", Value(files[1], "Build.Sdk.ReasonSelected"));
		Assert.DoesNotContain("&gt;", Value(files[1], "Build.Sdk.ReasonSelected"));
		string Placeholders(string v) => string.Concat(System.Text.RegularExpressions.Regex.Matches(v, @"\{\d+\}").Select(x => x.Value).OrderBy(x => x));
		foreach (string key in keys)
			foreach (string file in files) {
				string value = Value(file, key);
				// Localized falls back to English on a FormatException; a broken brace should not reach it.
				string.Format(value, "a", "b", "c", "d");
				if (file != files[0])
					Assert.Equal(Placeholders(Value(files[0], key)), Placeholders(Value(file, key)));
			}
	}

	[Fact]
	public void DescribeChoice_ReportsAMissingChoice()
	{
		Assert.Contains("No .NET SDK", DotNetSdkService.DescribeChoice(null, "", Array.Empty<DotNetSdkInfo>()));
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
	public void ResolveEffectiveSdk_PrefersTheProcessArchitecture_EvenWhenALowerVersion()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var native10 = Sdk("C:\\dotnet", "10.0.103", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var foreign10 = Sdk("C:\\dotnet\\other", "10.0.401", other, origin: DotNetSdkOrigin.System);

		// Both orders: the result must not depend on discovery order.
		Assert.Same(native10, DotNetSdkService.ResolveEffectiveSdk(new[] { native10, foreign10 }, string.Empty));
		Assert.Same(native10, DotNetSdkService.ResolveEffectiveSdk(new[] { foreign10, native10 }, string.Empty));
	}

	[Fact]
	public void ResolveEffectiveSdk_FallsBackToTheHighestVersion_WhenNoSdkMatchesTheProcessArchitecture()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var older = Sdk("C:\\dotnet-a", "10.0.103", other, origin: DotNetSdkOrigin.System);
		var newer = Sdk("C:\\dotnet-b", "10.0.401", other, origin: DotNetSdkOrigin.System);

		Assert.Same(newer, DotNetSdkService.ResolveEffectiveSdk(new[] { older, newer }, string.Empty));
	}

	[Fact]
	public void ResolveEffectiveSdk_StillPrefersASystemSdk_OverAnArchitectureMatchingBundledOne()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var foreignSystem = Sdk("C:\\dotnet\\other", "10.0.401", other, origin: DotNetSdkOrigin.System);
		var nativeBundled = Sdk("C:\\bundle", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.Bundled);

		Assert.Same(foreignSystem, DotNetSdkService.ResolveEffectiveSdk(new[] { nativeBundled, foreignSystem }, string.Empty));
	}

	[Fact]
	public void ResolveEffectiveSdk_ByVersionOnly_WhenTheArchitectureIsUnknown()
	{
		var older = Sdk("/dotnet-a", "10.0.103", null, origin: DotNetSdkOrigin.System);
		var newer = Sdk("/dotnet-b", "10.0.401", null, origin: DotNetSdkOrigin.System);

		Assert.Same(newer, DotNetSdkService.ResolveEffectiveSdk(new[] { older, newer }, string.Empty));
		Assert.Same(newer, DotNetSdkService.ResolveEffectiveSdk(new[] { newer, older }, string.Empty));
	}

	[Fact]
	public void ResolveEffectiveSdk_HonoursAPreferredRootOfAnotherArchitecture()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var native = Sdk("C:\\dotnet", "10.0.401", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var foreign = Sdk("C:\\dotnet\\other", "10.0.103", other, origin: DotNetSdkOrigin.System);

		Assert.Same(foreign, DotNetSdkService.ResolveEffectiveSdk(new[] { native, foreign }, foreign.RootPath));
	}

	[Fact]
	public void DescribeChoice_DoesNotClaimTheHighestVersion_WhenTheArchitectureDecidedTheChoice()
	{
		var other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64;
		var native = Sdk("C:\\dotnet", "10.0.103", RuntimeInformation.ProcessArchitecture, origin: DotNetSdkOrigin.System);
		var foreign = Sdk("C:\\dotnet\\other", "10.0.401", other, origin: DotNetSdkOrigin.System);
		var discovered = new[] { native, foreign };
		var choice = DotNetSdkService.ResolveSdkForTargetFrameworks(discovered, "", new[] { "net10.0" });

		string text = DotNetSdkService.DescribeChoice(choice, "", discovered);

		Assert.Contains("10.0.103 at C:\\dotnet", text);
		Assert.Contains("architecture", text);
		Assert.DoesNotContain("the highest supported system SDK", text);
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

	static DotNetSdkInfo Sdk(string rootPath, string highest, Architecture? architecture,
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
