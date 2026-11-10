using System;
using System.Diagnostics;
using System.IO;

using ICSharpCode.SharpDevelop.Services;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Run Without Debugging starts the program with Process.Start, as SharpDevelop did, instead of a
/// DAP noDebug session that put the IDE into the debugging state. A project's start info names its
/// managed .dll, so the launch must pick something executable, and it must keep the start info's
/// environment (the launch profile's variables).
/// </summary>
public sealed class StartWithoutDebuggingInfoTests : IDisposable
{
	readonly string _directory;

	public StartWithoutDebuggingInfoTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "od-start-nodebug-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
	}

	public void Dispose()
	{
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	string Touch(string name)
	{
		string path = Path.Combine(_directory, name);
		File.WriteAllBytes(path, Array.Empty<byte>());
		return path;
	}

	[Fact]
	public void ManagedDll_RunsTheApphostBesideIt_WithTheEnvironmentKept()
	{
		string dll = Touch("App.dll");
		string apphost = Touch(OperatingSystem.IsWindows() ? "App.exe" : "App");
		var psi = new ProcessStartInfo(dll) { Arguments = "--x" };
		psi.Environment["DYLD_LIBRARY_PATH"] = "/opt/homebrew/lib";

		var result = WindowsDebugger.CreateStartWithoutDebuggingInfo(psi);

		Assert.Equal(apphost, result.FileName);
		Assert.Equal("--x", result.Arguments);
		Assert.False(result.UseShellExecute, "the environment only reaches the program without shell execute");
		Assert.Equal("/opt/homebrew/lib", result.Environment["DYLD_LIBRARY_PATH"]);
	}

	[Fact]
	public void ManagedDllWithoutApphost_RunsUnderTheProjectSdkHost()
	{
		string dll = Touch("App.dll");
		var psi = new ProcessStartInfo(dll) { Arguments = "--x" };
		psi.Environment["DOTNET_ROOT"] = "/sdk11";

		var result = WindowsDebugger.CreateStartWithoutDebuggingInfo(psi);

		Assert.Equal(Path.Combine("/sdk11", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), result.FileName);
		Assert.Equal("\"" + dll + "\" --x", result.Arguments);
		Assert.False(result.UseShellExecute);
	}

	[Fact]
	public void NativeProgram_IsStartedAsIs()
	{
		string exe = Touch("Tool.bin");
		var result = WindowsDebugger.CreateStartWithoutDebuggingInfo(new ProcessStartInfo(exe));
		Assert.Equal(exe, result.FileName);
		Assert.False(result.UseShellExecute);
	}

	[Fact]
	public void StartUrl_GoesToTheShell()
	{
		var result = WindowsDebugger.CreateStartWithoutDebuggingInfo(new ProcessStartInfo("http://localhost:5000"));
		Assert.Equal("http://localhost:5000", result.FileName);
		Assert.True(result.UseShellExecute, "a URL opens in the browser through the shell");
	}
}
