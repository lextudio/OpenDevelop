using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.SharpDevelop.Project.HotReload.Wpf;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Hot-reloads a real running WPF application and asserts the change reached its live visual tree.
/// This drives OpenDevelop's own adapter and session - the adapter configures the launch, the
/// session decides readiness and performs the apply - so it covers the product code rather than
/// just the agent. Verification deliberately does NOT go through the session: it asks the running
/// application what it currently displays, because an apply that reports success while the UI is
/// unchanged is exactly the failure this test exists to catch.
/// </summary>
public sealed class WpfHotReloadEndToEndTests
{
	[Fact]
	public async Task AppliedXaml_ChangesTheLiveApplication()
	{
		var fixture = LocateFixtureExecutable();
		Assert.True(File.Exists(fixture),
			$"The WPF Hot Reload fixture must be built before this test; expected '{fixture}'.");
		// The adapter looks for the agent next to its host, which in a test run is the test
		// assembly rather than the IDE, so point it at the one this project just built.
		Environment.SetEnvironmentVariable("OD_WPF_HOTRELOAD_AGENT", LocateAgentForTest());
		// The adapter only ever configures a launch that somebody else performs, so the test plays
		// the part of the project launch path here.
		var adapter = new WpfApplicationHotReloadAdapter();
		var startInfo = new ProcessStartInfo(fixture) { UseShellExecute = false };
		var session = await adapter.StartAsync(
			new HotReloadLaunchContext(project: null, withDebugger: false), startInfo, CancellationToken.None);
		await using (session) {
			// Everything the agent needs must have been put on the launch by the adapter.
			var injectedAgent = startInfo.Environment["DOTNET_STARTUP_HOOKS"];
			Assert.EndsWith("WpfHotReload.Agent.dll", injectedAgent);
			Assert.True(File.Exists(injectedAgent),
				$"The adapter injected an agent that is not on disk: '{injectedAgent}'. "
				+ "It is deployed by src/Main/HotReload/WpfHotReload.Agent.");
			Assert.Equal("1", startInfo.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"]);
			var pipeName = startInfo.Environment["WPF_HOTRELOAD_PIPE"];
			Assert.False(string.IsNullOrWhiteSpace(pipeName));

			using var app = Process.Start(startInfo);
			Assert.NotNull(app);
			try {
				var ready = await session.WaitForReadyAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
				Assert.True(ready,
					$"The agent in the launched WPF application never became ready (state {session.State}). "
					+ $"Application exited: {app.HasExited}.");
				Assert.Equal(HotReloadSessionState.Ready, session.State);

				// PaneTitle is declared in the shared vscode-wpf sample, which the fixture links.
				// The edit is only ever sent over the pipe - the sample on disk is never written to.
				var xamlPath = Path.Combine(RepositoryRoot(), "externals", "vscode-wpf", "sample", "net6.0",
					"SamplePane.xaml");
				var originalXaml = await File.ReadAllTextAsync(xamlPath);
				Assert.Equal("Sample pane", await QueryAgentAsync(pipeName, "PaneTitle.Text"));

				var updatedXaml = originalXaml.Replace("Text=\"Sample pane\"", "Text=\"Hot reloaded\"");
				Assert.NotEqual(originalXaml, updatedXaml);
				var result = await session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, originalXaml, updatedXaml, 1, HotReloadChangeKind.Property),
					CancellationToken.None);

				Assert.True(result.IsSuccess, $"Apply failed: {result.Message}");
				Assert.Equal(HotReloadOutcome.Applied, result.Outcome);
				// The point of the whole exercise: the running application shows the new text, and
				// it is the same process - nothing was rebuilt or restarted.
				Assert.Equal("Hot reloaded", await QueryAgentAsync(pipeName, "PaneTitle.Text"));
				Assert.False(app.HasExited);
			} finally {
				if (!app.HasExited) {
					app.Kill(entireProcessTree: true);
					app.WaitForExit(10_000);
				}
			}
		}
	}

	/// <summary>
	/// Reads a value straight from the agent, independently of the session under test.
	/// The agent serves one request per connection and then recreates its listener, so on Unix -
	/// where the endpoint is a socket file that briefly stops existing - a failed connect means
	/// "retry", not "the agent is gone".
	/// </summary>
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
				return document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
					? value.GetString()
					: null;
			} catch (Exception ex) when (ex is IOException || ex is TimeoutException) {
				last = ex;
				await Task.Delay(200);
			}
		}
		throw new TimeoutException($"The agent did not answer '{query}' in time.", last);
	}

	static string LocateAgentForTest()
	{
		var configuration = CurrentConfiguration();
		return Path.Combine(RepositoryRoot(), "src", "Main", "HotReload", "WpfHotReload.Agent",
			"bin", configuration, "net10.0-windows", "WpfHotReload.Agent.dll");
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

	static string LocateFixtureDirectory() =>
		Path.Combine(RepositoryRoot(), "tests", "fixtures", "WpfHotReloadFixture");

	static string LocateFixtureExecutable()
	{
		var executable = Path.Combine(LocateFixtureDirectory(), "bin", CurrentConfiguration(), "net10.0-windows", "WpfHotReloadFixture");
		return OperatingSystem.IsWindows() ? executable + ".exe" : executable;
	}

}
