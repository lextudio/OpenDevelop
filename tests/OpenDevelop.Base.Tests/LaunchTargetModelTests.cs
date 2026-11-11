using System;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The launch-target drop-down lists launchSettings.json profiles linked to the project's target
/// frameworks, as Visual Studio does for Uno: a profile that belongs to no framework of the project
/// is hidden, and so is a framework that has no profile, which gets an "add a profile" entry instead.
/// </summary>
public sealed class LaunchTargetModelTests : IDisposable
{
	static readonly string[] Mobile = { "net10.0-ios", "net10.0-android", "net10.0-desktop" };
	readonly string _directory;

	public LaunchTargetModelTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "od-launchtarget-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_directory, "Properties"));
	}

	public void Dispose()
	{
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	System.Collections.Generic.IReadOnlyList<LaunchSettingsProfile> Profiles(string json)
	{
		File.WriteAllText(LaunchSettingsProfile.GetPath(_directory), json);
		return LaunchSettingsProfile.LoadAll(_directory);
	}

	const string Uno = """
		{ "profiles": {
		  "App (Desktop)": { "commandName": "Project", "compatibleTargetFramework": "desktop" },
		  "App (Desktop WSL2)": { "commandName": "WSL2", "compatibleTargetFramework": "desktop" },
		  "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" },
		  "App (Android)": { "commandName": "Project", "compatibleTargetFramework": "android" }
		} }
		""";

	[Fact]
	public void EachProfileOfAFramework_IsAnEntryThatSelectsThatFramework_AndWsl2IsNotListed()
	{
		var entries = LaunchTargetModel.Build("App", Mobile, Profiles(Uno), "net10.0-android", null);

		Assert.Equal(new[] { "App (Desktop)", "App (iOS)", "App (Android)" }, entries.Select(e => e.Label));
		Assert.Equal(new[] { "net10.0-desktop", "net10.0-ios", "net10.0-android" }, entries.Select(e => e.TargetFramework));
		Assert.All(entries, e => Assert.Equal(LaunchTargetEntryKind.Profile, e.Kind));
		Assert.Equal("App (Android)", entries.Single(e => e.IsSelected).Label);
	}

	[Fact]
	public void AFrameworkWithoutAProfile_IsHidden_AndOffersAddingOne()
	{
		var profiles = Profiles("""{ "profiles": { "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" } } }""");

		var entries = LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-ios", null);

		Assert.Equal(new[] { "App (iOS)", "net10.0-android", "net10.0-desktop" }, entries.Select(e => e.Label));
		var add = entries.Where(e => e.Kind == LaunchTargetEntryKind.AddProfile).ToList();
		Assert.Equal(new[] { "App (Android)", "App (Desktop)" }, add.Select(e => e.ProfileName));
		Assert.Equal(new[] { "android", "desktop" }, add.Select(e => e.Platform));
		Assert.Equal(new[] { "net10.0-android", "net10.0-desktop" }, add.Select(e => e.TargetFramework));
		Assert.DoesNotContain(add, e => e.IsSelected);
	}

	[Fact]
	public void AProfileOfAFrameworkTheProjectLacks_IsHidden()
	{
		var entries = LaunchTargetModel.Build("App", new[] { "net10.0-desktop" }, Profiles(Uno), "net10.0-desktop", null);

		Assert.Equal(new[] { "App (Desktop)" }, entries.Select(e => e.Label));
		Assert.True(entries.Single().IsSelected);
	}

	[Fact]
	public void TheStoredProfileIsSelected_WhenItFitsTheActiveFramework_ElseTheFirstThatDoes()
	{
		var profiles = Profiles("""
			{ "profiles": {
			  "First": { "commandName": "Project", "compatibleTargetFramework": "ios" },
			  "Second": { "commandName": "Project", "compatibleTargetFramework": "ios" },
			  "Other": { "commandName": "Project", "compatibleTargetFramework": "android" }
			} }
			""");

		Assert.Equal("Second", LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-ios", "Second").Single(e => e.IsSelected).Label);
		Assert.Equal("First", LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-ios", "Other").Single(e => e.IsSelected).Label);
		Assert.Equal("First", LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-ios", "Gone").Single(e => e.IsSelected).Label);
		Assert.DoesNotContain(LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-desktop", null), e => e.IsSelected);
	}

	[Fact]
	public void SelectProfile_IsTheEntryBuildSelects_EvenWhenAForeignProfileComesFirstInTheFile()
	{
		// Android's profile is listed first, the active framework is iOS and nothing is stored: what F5 applies
		// must be the iOS profile the drop-down shows, not the first profile of the file.
		var profiles = Profiles("""
			{ "profiles": {
			  "App (Android)": { "commandName": "Project", "compatibleTargetFramework": "android" },
			  "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" },
			  "App (iOS) 2": { "commandName": "Project", "compatibleTargetFramework": "ios" }
			} }
			""");

		Assert.Equal("App (iOS)", LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-ios", null)!.Name);
		Assert.Equal("App (iOS) 2", LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-ios", "App (iOS) 2")!.Name);
		Assert.Equal("App (iOS)", LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-ios", "App (Android)")!.Name);
		Assert.Equal("App (Android)", LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-android", null)!.Name);
		foreach (string active in new[] { "net10.0-ios", "net10.0-android", "net10.0-desktop" }) {
			var shown = LaunchTargetModel.Build("App", Mobile, profiles, active, null).SingleOrDefault(e => e.IsSelected)?.ProfileName;
			Assert.Equal(shown, LaunchTargetModel.SelectProfile(Mobile, profiles, active, null)?.Name);
		}
	}

	[Fact]
	public void SelectProfile_IsNullWhenNoProfileFitsTheActiveFramework_SoAForeignPlatformsProfileIsNotApplied()
	{
		var profiles = Profiles("""{ "profiles": { "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios" } } }""");

		Assert.Null(LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-desktop", null));
		Assert.Null(LaunchTargetModel.SelectProfile(Mobile, profiles, "net10.0-desktop", "App (iOS)"));
	}

	[Fact]
	public void AProfileThatFitsEveryFramework_IsOneEntryLeavingTheActiveFrameworkAlone()
	{
		var profiles = Profiles("""{ "profiles": { "Any": { "commandName": "Project" } } }""");

		var entries = LaunchTargetModel.Build("App", Mobile, profiles, "net10.0-ios", null);

		var any = entries.Single(e => e.Label == "Any");
		Assert.Null(any.TargetFramework);
		Assert.True(any.IsSelected);
	}

	[Fact]
	public void PlatformlessFrameworks_OfferNothingToAdd()
	{
		var entries = LaunchTargetModel.Build("App", new[] { "net8.0", "net10.0" }, Array.Empty<LaunchSettingsProfile>(), "net8.0", null);

		Assert.Empty(entries);
	}

	[Theory]
	[InlineData("""{ "profiles": {} }""", new[] { "net10.0-desktop" }, false)]
	[InlineData("""{ "profiles": {} }""", new[] { "net8.0", "net10.0" }, true)]
	[InlineData("""{ "profiles": { "P": { "commandName": "Project", "compatibleTargetFramework": "desktop" } } }""", new[] { "net10.0-desktop" }, true)]
	[InlineData("""{ "profiles": { "P": { "commandName": "Project" } } }""", new[] { "net10.0" }, false)]
	public void TheDropDownShowsOnlyForMultiTargetOrPlatformProfileProjects(string json, string[] tfms, bool expected)
	{
		Assert.Equal(expected, LaunchTargetModel.ShouldShow(tfms, Profiles(json)));
	}

	[Theory]
	[InlineData("App", "ios", new string[0], "App (iOS)")]
	[InlineData("App", "desktop", new[] { "App (Desktop)" }, "App (Desktop) (2)")]
	[InlineData("App", "desktop", new[] { "App (Desktop)", "App (Desktop) (2)" }, "App (Desktop) (3)")]
	[InlineData("", "android", new string[0], "App (Android)")]
	[InlineData("App", "tizen", new string[0], "App (tizen)")]
	public void SuggestedNamesFollowUnosProjectPlatformPattern(string project, string platform, string[] existing, string expected)
	{
		Assert.Equal(expected, LaunchTargetModel.SuggestProfileName(project, platform, existing));
	}

	[Fact]
	public void DeviceEntriesCarryThePlatformAndTheStoredDeviceId()
	{
		var entries = LaunchTargetModel.BuildDevices("ios", new[] {
			new LaunchTargetDevice(null, "Automatic", true),
			new LaunchTargetDevice("UDID-1", "iPhone 16", false)
		});

		Assert.Collection(entries,
			e => { Assert.Equal(LaunchTargetEntryKind.Device, e.Kind); Assert.Null(e.DeviceId); Assert.True(e.IsSelected); Assert.Equal("ios", e.Platform); },
			e => { Assert.Equal("UDID-1", e.DeviceId); Assert.Equal("iPhone 16", e.Label); Assert.False(e.IsSelected); });
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void NoPlatformMeansNoDeviceEntries(string platform)
	{
		Assert.Empty(LaunchTargetModel.BuildDevices(platform, new[] { new LaunchTargetDevice(null, "Automatic", true) }));
	}

	[Fact]
	public void ChoosingADeviceWithoutASourceForItsPlatformIsARefusal()
	{
		LaunchTargetDeviceSources.All = Array.Empty<ILaunchTargetDeviceSource>();
		try {
			var project = System.Reflection.DispatchProxy.Create<IProject, NoMembersProject>();
			var entry = new LaunchTargetEntry(LaunchTargetEntryKind.Device, "Pixel", null, null, "android", false, "emulator-5554");

			var ex = Assert.Throws<LaunchSettingsEditException>(() => LaunchTargetService.Apply(project, entry));

			Assert.Contains("android", ex.Message);
		} finally {
			LaunchTargetDeviceSources.All = null; // back to the add-in tree
		}
	}

	/// <summary>An IProject whose members are never reached: the refusal comes before any is read.</summary>
	public class NoMembersProject : System.Reflection.DispatchProxy
	{
		protected override object Invoke(System.Reflection.MethodInfo targetMethod, object[] args)
			=> throw new InvalidOperationException("The project must not be touched: " + targetMethod.Name);
	}
}
