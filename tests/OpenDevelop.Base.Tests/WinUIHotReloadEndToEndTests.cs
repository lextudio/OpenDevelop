using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.WinUIXamlDesigner.HotReload;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Hot-reloads a real running WinUI 3 application through OpenDevelop's own adapter and session,
/// once per process architecture, because the agent must load the native TAP matching the
/// application and each of x86, x64 and ARM64 can fail on its own. As in the WPF test, the result
/// is verified by asking the running application what it shows, not by trusting the apply result.
/// </summary>
public sealed class WinUIHotReloadEndToEndTests
{
	[Theory]
	[InlineData("x64")]
	[InlineData("x86")]
	[InlineData("ARM64")]
	public async Task AppliedXaml_ChangesTheLiveApplication(string platform)
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("WinUI 3 applications only run on Windows.");
		if (platform == "ARM64" && RuntimeInformation.OSArchitecture != Architecture.Arm64)
			Assert.Skip("An ARM64 application cannot run on this machine.");

		var fixture = LocateFixtureExecutable(platform);
		Assert.True(fixture != null && File.Exists(fixture),
			$"The {platform} WinUI Hot Reload fixture must be built before this test (OpenDevelop.Base.Tests builds it).");
		var agent = LocateAgentForTest();
		Assert.True(File.Exists(agent), $"The WinUI Hot Reload agent must be built before this test; expected '{agent}'.");
		Environment.SetEnvironmentVariable("OD_WINUI_HOTRELOAD_AGENT", agent);

		var adapter = new WinUIApplicationHotReloadAdapter();
		var startInfo = new ProcessStartInfo(fixture) { UseShellExecute = false };
		var session = await adapter.StartAsync(
			new HotReloadLaunchContext(project: null, withDebugger: false), startInfo, CancellationToken.None);
		await using (session) {
			Assert.Equal(agent, startInfo.Environment["DOTNET_STARTUP_HOOKS"]);
			Assert.Equal("1", startInfo.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
			var pipeName = startInfo.Environment["WINUI_HOTRELOAD_PIPE"];
			Assert.False(string.IsNullOrWhiteSpace(pipeName));

			using var app = Process.Start(startInfo);
			Assert.NotNull(app);
			try {
				var ready = await session.WaitForReadyAsync(TimeSpan.FromSeconds(90), CancellationToken.None);
				Assert.True(ready, $"The WinUI agent never became ready (state {session.State}, application exited: {app.HasExited}). "
					+ $"Agent log: {ReadLog(session)}");

				var xamlPath = Path.Combine(FixtureDirectory(), "MainWindow.xaml");
				var original = await File.ReadAllTextAsync(xamlPath);
				Assert.Equal("Before", await QueryAgentAsync(pipeName, "Probe.Text"));

				// A property edit is applied in place, without restarting the application.
				var edited = original.Replace("Text=\"Before\"", "Text=\"Hot reloaded\"");
				Assert.NotEqual(original, edited);
				var result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, original, edited, 1, HotReloadChangeKind.Property), CancellationToken.None);
				Assert.True(result.IsSuccess, $"Apply failed: {result.Message}. Agent log: {ReadLog(session)}");
				Assert.Equal("Hot reloaded", await QueryAgentAsync(pipeName, "Probe.Text"));

				// A second edit on top of the first still maps to the same live element.
				var editedAgain = edited.Replace("Text=\"Hot reloaded\"", "Text=\"Again\"");
				result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, edited, editedAgain, 2, HotReloadChangeKind.Property), CancellationToken.None);
				Assert.True(result.IsSuccess, $"Second apply failed: {result.Message}");
				Assert.Equal("Again", await QueryAgentAsync(pipeName, "Probe.Text"));

				// A new element: the StackPanel's children are rebuilt from the markup.
				var structural = editedAgain.Replace("</StackPanel>", "<TextBlock x:Name=\"Added\" Text=\"New\" /></StackPanel>");
				result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, editedAgain, structural, 3, HotReloadChangeKind.Subtree), CancellationToken.None);
				Assert.True(result.IsSuccess, $"Subtree apply failed: {result.Message}. Agent log: {ReadLog(session)}");
				Assert.Equal("New", await QueryAgentAsync(pipeName, "Added.Text"));
				Assert.Equal("Again", await QueryAgentAsync(pipeName, "Probe.Text"));

				// An edit inside the rebuilt region: those elements have no XAML source info any more,
				// so the agent has to find them through its own record of the region.
				var insideRegion = structural.Replace("Text=\"New\"", "Text=\"Newer\"");
				result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, structural, insideRegion, 4, HotReloadChangeKind.Property), CancellationToken.None);
				Assert.True(result.IsSuccess, $"Apply inside the rebuilt region failed: {result.Message}");
				Assert.Equal("Newer", await QueryAgentAsync(pipeName, "Added.Text"));

				// An event handler needs generated code: refused, and the running UI is left as it was.
				var withEvent = insideRegion.Replace("</StackPanel>", "<Button Content=\"Go\" Click=\"OnGo\" /></StackPanel>");
				result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, insideRegion, withEvent, 5, HotReloadChangeKind.Subtree), CancellationToken.None);
				Assert.Equal(HotReloadOutcome.RestartRequired, result.Outcome);
				Assert.Equal("Newer", await QueryAgentAsync(pipeName, "Added.Text"));

				Assert.False(app.HasExited);
			} finally {
				if (!app.HasExited) {
					app.Kill(entireProcessTree: true);
					app.WaitForExit(10_000);
				}
			}
		}
	}

	static string ReadLog(IHotReloadSession session)
	{
		var diagnostics = session.GetDiagnostics();
		return diagnostics.TryGetValue("agentLog", out var log) && File.Exists(log) ? File.ReadAllText(log) : "(none)";
	}

	/// <summary>Reads a value straight from the agent, independently of the session under test.</summary>
	static async Task<string> QueryAgentAsync(string pipeName, string query)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		Exception last = null;
		while (DateTime.UtcNow < deadline) {
			try {
				using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				await pipe.ConnectAsync(2000);
				var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
				await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = "query", query }));
				var reader = new StreamReader(pipe, new UTF8Encoding(false));
				var line = await reader.ReadLineAsync();
				if (string.IsNullOrWhiteSpace(line))
					throw new IOException("empty response");
				using var document = JsonDocument.Parse(line);
				var root = document.RootElement;
				if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
					return value.GetString();
				throw new InvalidOperationException($"The agent could not answer '{query}': {line}");
			} catch (Exception ex) when (ex is IOException || ex is TimeoutException) {
				last = ex;
				await Task.Delay(200);
			}
		}
		throw new TimeoutException($"The agent did not answer '{query}' in time.", last);
	}

	static string CurrentConfiguration() =>
		Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));

	static string RepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory != null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
			directory = Path.GetDirectoryName(directory);
		Assert.NotNull(directory);
		return directory;
	}

	static string LocateAgentForTest() =>
		Path.Combine(RepositoryRoot(), "src", "Main", "HotReload", "WinUIHotReload.Agent",
			"bin", CurrentConfiguration(), "net8.0-windows", "WinUIHotReload.Agent.dll");

	static string FixtureDirectory() => Path.Combine(RepositoryRoot(), "tests", "fixtures", "WinUIHotReloadFixture");

	/// <summary>bin/&lt;Platform&gt;/&lt;Configuration&gt;/&lt;tfm&gt;/&lt;rid&gt;/ - the TFM carries a Windows SDK version, so search for it.</summary>
	static string LocateFixtureExecutable(string platform)
	{
		var configurationDirectory = Path.Combine(FixtureDirectory(), "bin", platform, CurrentConfiguration());
		if (!Directory.Exists(configurationDirectory))
			return null;
		return Directory.EnumerateFiles(configurationDirectory, "WinUIHotReloadFixture.exe", SearchOption.AllDirectories).FirstOrDefault();
	}
}
