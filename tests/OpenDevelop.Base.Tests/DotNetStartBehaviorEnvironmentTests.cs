using System;
using System.Collections.Generic;
using System.IO;

using ICSharpCode.SharpDevelop.Project;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// A launched application must not inherit the IDE's own .NET host environment.
///
/// build/common.psm1's Set-DotNetEnv exports DOTNET_ROOT and a set of MSBuild* variables so that
/// SharpDevelop's IN-PROCESS MSBuild hosting resolves the right SDK. ProcessStartInfo inherits the
/// parent environment, so those leaked into every app the IDE started. On a machine with both an
/// ARM64 and an x64 .NET installed that is not merely untidy, it is fatal: OpenDevelop runs ARM64,
/// so DOTNET_ROOT points at the ARM64 install, and an x64 apphost sent there dies before Main with
///
///     Failed to load [...\host\fxr\...\hostfxr.dll], HRESULT: 0x800700C1
///       - Ensure the library matches the current process architecture: x64
///
/// printed to the IDE's stdout, where no user sees it. Hot Reload reported a successful build and
/// simply never showed a window.
///
/// These assertions are architecture-independent on purpose: the variables must be absent whatever
/// machine the suite runs on, because an apphost that resolves the runtime its normal way always
/// picks the install matching its own bitness.
/// </summary>
public sealed class DotNetStartBehaviorEnvironmentTests : IDisposable
{
	static readonly string[] MustNotLeak =
	{
		"DOTNET_ROOT",
		"DOTNET_HOST_PATH",
		"MSBuildSDKsPath",
		"MSBuildExtensionsPath",
		"MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET",
		"MSBUILD_NUGET_PATH",
		"MSBuildEnableWorkloadResolver",
	};

	readonly Dictionary<string, string?> _saved = new();
	readonly string _directory;
	readonly string _program;

	public DotNetStartBehaviorEnvironmentTests()
	{
		foreach (var name in MustNotLeak) {
			_saved[name] = Environment.GetEnvironmentVariable(name);
			// A value the IDE would plausibly have set, so the test fails loudly if the variable
			// is merely inherited rather than removed.
			Environment.SetEnvironmentVariable(name, "/opendevelop/test/" + name);
		}

		_directory = Path.Combine(Path.GetTempPath(), "od-startinfo-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
		_program = Path.Combine(_directory, "FakeApp.exe");
		File.WriteAllBytes(_program, Array.Empty<byte>());
	}

	public void Dispose()
	{
		foreach (var pair in _saved)
			Environment.SetEnvironmentVariable(pair.Key, pair.Value);
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	[Fact]
	public void CreateStartInfo_DoesNotPropagateTheIdeHostEnvironment()
	{
		var psi = DotNetStartBehavior.CreateStartInfo(_program, _directory, string.Empty, string.Empty);

		foreach (var name in MustNotLeak) {
			Assert.False(psi.Environment.ContainsKey(name),
				$"{name} must not reach the launched application; it is the IDE's own MSBuild/host setting.");
		}
	}

	[Fact]
	public void CreateStartInfo_LeavesPathAlone()
	{
		// PATH is deliberately NOT scrubbed: an application may depend on it, and its ordering only
		// matters to a child that shells out to `dotnet` itself. Pinning this keeps a future
		// "clean the environment" change from quietly breaking launched apps.
		var psi = DotNetStartBehavior.CreateStartInfo(_program, _directory, string.Empty, string.Empty);

		Assert.True(psi.Environment.ContainsKey("PATH") || psi.Environment.ContainsKey("Path"),
			"PATH must still be inherited by the launched application.");
	}
}
