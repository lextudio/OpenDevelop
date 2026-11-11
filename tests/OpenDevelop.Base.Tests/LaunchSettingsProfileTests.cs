using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Starting a project from the IDE applies its launchSettings.json profile the way `dotnet run`
/// does. Without it tests/fixtures/GtkDesignerFixture, whose profile sets DYLD_LIBRARY_PATH to
/// Homebrew's lib directory, died on macOS with DllNotFoundException for libgtk-4.1.dylib when
/// started with Run Without Debugging, and no window appeared, while `dotnet run` worked.
/// </summary>
public sealed class LaunchSettingsProfileTests : IDisposable
{
	readonly string _directory;

	public LaunchSettingsProfileTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "od-launchsettings-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_directory, "Properties"));
	}

	public void Dispose()
	{
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	void WriteSettings(string json) => File.WriteAllText(LaunchSettingsProfile.GetPath(_directory), json);

	[Fact]
	public void NoLaunchSettings_NoProfile()
	{
		Assert.Null(LaunchSettingsProfile.Load(_directory));
	}

	[Fact]
	public void PicksTheFirstProjectProfile_AndAppliesItsEnvironment()
	{
		WriteSettings("""
			{
			  // comments are allowed, as dotnet run allows them
			  "profiles": {
			    "IIS Express": { "commandName": "IISExpress" },
			    "GtkDesignerFixture": {
			      "commandName": "Project",
			      "environmentVariables": { "DYLD_LIBRARY_PATH": "/opt/homebrew/lib:/usr/local/lib" },
			    },
			    "Other": { "commandName": "Project", "environmentVariables": { "DYLD_LIBRARY_PATH": "/wrong" } }
			  }
			}
			""");
		var profile = LaunchSettingsProfile.Load(_directory);
		Assert.NotNull(profile);
		Assert.Equal("GtkDesignerFixture", profile!.Name);

		var psi = new ProcessStartInfo("app") { WorkingDirectory = _directory, Arguments = "" };
		profile.ApplyTo(psi, _directory, argumentsConfigured: false, workingDirectoryConfigured: false);
		Assert.Equal("/opt/homebrew/lib:/usr/local/lib", psi.Environment["DYLD_LIBRARY_PATH"]);
		Assert.Equal("GtkDesignerFixture", psi.Environment["DOTNET_LAUNCH_PROFILE"]);
	}

	[Fact]
	public void ProfileArgumentsAndWorkingDirectory_ApplyOnlyWhenTheProjectOptionsGiveNone()
	{
		Directory.CreateDirectory(Path.Combine(_directory, "work"));
		WriteSettings("""
			{ "profiles": { "App": {
			  "commandName": "Project",
			  "commandLineArgs": "--from-profile \"two words\"",
			  "workingDirectory": "work",
			  "applicationUrl": "http://localhost:5123"
			} } }
			""");
		var profile = LaunchSettingsProfile.Load(_directory)!;

		var unset = new ProcessStartInfo("app") { WorkingDirectory = _directory };
		profile.ApplyTo(unset, _directory, argumentsConfigured: false, workingDirectoryConfigured: false);
		Assert.Equal("--from-profile \"two words\"", unset.Arguments);
		Assert.Equal(Path.Combine(_directory, "work"), unset.WorkingDirectory);
		Assert.Equal("http://localhost:5123", unset.Environment["ASPNETCORE_URLS"]);

		var configured = new ProcessStartInfo("app") { WorkingDirectory = "/configured", Arguments = "--mine" };
		profile.ApplyTo(configured, _directory, argumentsConfigured: true, workingDirectoryConfigured: true);
		Assert.Equal("--mine", configured.Arguments);
		Assert.Equal("/configured", configured.WorkingDirectory);
	}

	[Fact]
	public void EnvironmentReferencesAreExpanded_AndAnExplicitAspNetCoreUrlsWins()
	{
		Environment.SetEnvironmentVariable("OD_LAUNCH_SETTINGS_TEST_ROOT", "/expanded");
		try {
			WriteSettings("""
				{ "profiles": { "App": {
				  "commandName": "Project",
				  "applicationUrl": "http://localhost:5000",
				  "environmentVariables": { "LIB": "%OD_LAUNCH_SETTINGS_TEST_ROOT%/lib", "ASPNETCORE_URLS": "http://localhost:6000" }
				} } }
				""");
			var psi = new ProcessStartInfo("app");
			LaunchSettingsProfile.Load(_directory)!.ApplyTo(psi, _directory, false, false);
			Assert.Equal("/expanded/lib", psi.Environment["LIB"]);
			Assert.Equal("http://localhost:6000", psi.Environment["ASPNETCORE_URLS"]);
		} finally {
			Environment.SetEnvironmentVariable("OD_LAUNCH_SETTINGS_TEST_ROOT", null);
		}
	}

	[Fact]
	public void NoProjectProfile_NoProfile_AndInvalidJsonIsAStartError()
	{
		WriteSettings("""{ "profiles": { "IIS Express": { "commandName": "IISExpress" } } }""");
		Assert.Null(LaunchSettingsProfile.Load(_directory));

		WriteSettings("{ not json");
		Assert.Throws<ProjectStartException>(() => LaunchSettingsProfile.Load(_directory));
	}

	const string ThreeProfiles = """
		{ "profiles": {
		  "App (Desktop)": { "commandName": "Project", "compatibleTargetFramework": "desktop",
		    "environmentVariables": { "WHICH": "desktop" } },
		  "App (Desktop WSL2)": { "commandName": "WSL2", "compatibleTargetFramework": "desktop" },
		  "App (iOS)": { "commandName": "Project", "compatibleTargetFramework": "ios",
		    "environmentVariables": { "WHICH": "ios" } },
		  "Any": { "commandName": "Project" }
		} }
		""";

	[Fact]
	public void LoadAll_ListsTheProjectProfilesInFileOrder_AndSkipsOtherCommands()
	{
		WriteSettings(ThreeProfiles);

		var profiles = LaunchSettingsProfile.LoadAll(_directory);

		Assert.Equal(new[] { "App (Desktop)", "App (iOS)", "Any" }, profiles.Select(p => p.Name));
		Assert.Equal(new[] { "desktop", "ios", "" }, profiles.Select(p => p.CompatibleTargetFramework));
		Assert.Empty(LaunchSettingsProfile.LoadAll(Path.Combine(_directory, "missing")));
	}

	[Fact]
	public void LoadByName_PicksThatProfile_AndAStaleNameFallsBackToDotnetRunsPick()
	{
		WriteSettings(ThreeProfiles);

		Assert.Equal("App (iOS)", LaunchSettingsProfile.Load(_directory, "App (iOS)")!.Name);
		Assert.Equal("App (Desktop)", LaunchSettingsProfile.Load(_directory, "Renamed Away")!.Name);
		Assert.Equal("App (Desktop)", LaunchSettingsProfile.Load(_directory, "")!.Name);
		Assert.Equal("App (Desktop)", LaunchSettingsProfile.Load(_directory, "App (Desktop WSL2)")!.Name);
	}

	[Fact]
	public void TheSelectedProfileFromThePreferences_IsTheOneApplied()
	{
		WriteSettings(ThreeProfiles);
		var preferences = new ICSharpCode.Core.Properties();
		preferences.Set(LaunchSettingsProfile.SelectedProfilePreferenceKey, "App (iOS)");

		var psi = new ProcessStartInfo("app");
		LaunchSettingsProfile.LoadSelected(_directory, preferences)!.ApplyTo(psi, _directory, false, false);

		Assert.Equal("ios", psi.Environment["WHICH"]);
		Assert.Equal("App (iOS)", psi.Environment["DOTNET_LAUNCH_PROFILE"]);
	}

	[Theory]
	[InlineData("desktop", "net10.0-desktop", true)]
	[InlineData("DESKTOP", "net10.0-desktop", true)]
	[InlineData("desktop", "net10.0-ios", false)]
	[InlineData("ios", "net11.0-ios26.0", true)]
	[InlineData("android", "net10.0-android35.0", true)]
	[InlineData("windows", "net10.0-windows10.0.19041.0", true)]
	[InlineData("desktop", "net10.0", false)]
	[InlineData("", "net10.0-ios", true)]
	[InlineData("", "net10.0", true)]
	public void ACompatibleTargetFramework_NamesThePlatformOfTheFramework(string compatible, string tfm, bool expected)
	{
		string json = compatible.Length == 0
			? """{ "profiles": { "P": { "commandName": "Project" } } }"""
			: "{ \"profiles\": { \"P\": { \"commandName\": \"Project\", \"compatibleTargetFramework\": \"" + compatible + "\" } } }";
		WriteSettings(json);

		Assert.Equal(expected, LaunchSettingsProfile.Load(_directory)!.IsCompatibleWith(tfm));
	}
}
