using System;
using System.Diagnostics;
using System.IO;

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
}
