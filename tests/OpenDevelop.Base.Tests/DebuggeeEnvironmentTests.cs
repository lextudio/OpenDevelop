using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Services;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// What the DAP start hands the debuggee as its launch environment. It used to forward only a
/// whitelist of Hot Reload variables, so a launchSettings.json profile variable (DYLD_LIBRARY_PATH
/// for tests/fixtures/GtkDesignerFixture on macOS) was dropped when debugging.
/// </summary>
public sealed class DebuggeeEnvironmentTests : IDisposable
{
	const string Inherited = "OD_DEBUGGEE_ENV_TEST_INHERITED";
	readonly string _directory;

	public DebuggeeEnvironmentTests()
	{
		Environment.SetEnvironmentVariable(Inherited, "from-the-ide");
		_directory = Path.Combine(Path.GetTempPath(), "od-debuggee-env-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_directory, "Properties"));
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(Inherited, null);
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	[Fact]
	public void ForwardsProfileAndHotReloadVariables_NotInheritedOnesOrDotnetRoot()
	{
		File.WriteAllText(LaunchSettingsProfile.GetPath(_directory), """
			{ "profiles": { "App": { "commandName": "Project",
			  "environmentVariables": { "DYLD_LIBRARY_PATH": "/opt/homebrew/lib" } } } }
			""");
		var psi = new ProcessStartInfo("app");
		LaunchSettingsProfile.Load(_directory)!.ApplyTo(psi, _directory, false, false);
		psi.Environment["DOTNET_STARTUP_HOOKS"] = "/hook.dll";
		psi.Environment["DOTNET_ROOT"] = "/sdk11";

		var forwarded = WindowsDebugger.GetExplicitEnvironment(psi).ToDictionary(v => v.Key, v => v.Value);

		Assert.Equal("/opt/homebrew/lib", forwarded["DYLD_LIBRARY_PATH"]);
		Assert.Equal("App", forwarded["DOTNET_LAUNCH_PROFILE"]);
		Assert.Equal("/hook.dll", forwarded["DOTNET_STARTUP_HOOKS"]);
		Assert.False(forwarded.ContainsKey(Inherited), "an inherited IDE variable is not repeated");
		Assert.False(forwarded.ContainsKey("DOTNET_ROOT"), "DOTNET_ROOT selects the runtime host instead");

		psi.Environment[Inherited] = "changed";
		Assert.Equal("changed", WindowsDebugger.GetExplicitEnvironment(psi).Single(v => v.Key == Inherited).Value);
	}
}
