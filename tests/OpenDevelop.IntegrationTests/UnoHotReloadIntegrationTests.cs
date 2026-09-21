using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>Verifies a saved XAML edit reaches the live Uno application, not merely DevServer logs.</summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class UnoHotReloadIntegrationTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture _app;
	readonly string _workDir = Path.Combine(ResolveDirectoryLinks(Path.GetTempPath()), "UnoHotReloadIntegration-" + Guid.NewGuid().ToString("N"));

	static string ResolveDirectoryLinks(string path)
	{
		// MSBuild normalizes macOS /var to /private/var for AdditionalFiles, while
		// Uno's watcher matches document paths literally. Use one physical path
		// throughout the isolated build, workspace and editor session.
		var resolved = Path.GetPathRoot(Path.GetFullPath(path))!;
		foreach (var segment in Path.GetFullPath(path).Substring(resolved.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
			var directory = new DirectoryInfo(Path.Combine(resolved, segment));
			resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
		}
		return resolved;
	}

	public UnoHotReloadIntegrationTests(OpenDevelopAppFixture app)
	{
		_app = app;
		var fixtureDirectory = Path.GetDirectoryName(app.UnoHotReloadFixturePath)!;
		CopyDirectory(fixtureDirectory, _workDir);
		File.Copy(FindRepositoryGlobalJson(fixtureDirectory), Path.Combine(_workDir, "global.json"));
	}

	[Fact]
	public async Task SavedXamlEdit_UpdatesLiveUnoApplicationProbe()
	{
		if (!OperatingSystem.IsMacOS())
			return;

		var solution = Path.Combine(_workDir, "UnoHotReloadFixture.slnx");
		var xaml = Path.Combine(_workDir, "MainPage.xaml");
		await RebuildCopiedFixtureAsync();
		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		var configuration = await _app.InvokeAsync("od.solution.set-configuration", "Debug", "Any CPU");
		Assert.True(configuration.GetProperty("success").GetBoolean(), configuration.ToString());
		Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());

		var port = AllocateLoopbackPort();
		await File.WriteAllTextAsync(Path.Combine(_workDir, "bin", "Debug", "net10.0-desktop", "od-hot-reload-probe-port.txt"), port.ToString(System.Globalization.CultureInfo.InvariantCulture));
		var buildPreference = await _app.InvokeAsync("od.build.set-on-execute", "DoNotBuild");
		Assert.True(buildPreference.GetProperty("success").GetBoolean(), buildPreference.ToString());
		try {
			await InvokeShortcutAsync("F5");
			await WaitForDebuggerAsync(running: true, TimeSpan.FromSeconds(45));

			JsonElement before;
			try {
				before = await WaitForProbeAsync(port, "Before hot reload", TimeSpan.FromSeconds(45));
			} catch (TimeoutException ex) {
				var status = await _app.InvokeAsync("od.debug.service-info");
				var output = await _app.InvokeAsync("od.debug.output");
				throw new Xunit.Sdk.XunitException($"{ex.Message}{Environment.NewLine}Debugger status: {status}{Environment.NewLine}Debugger output: {output}");
			}
			Assert.Equal("Before hot reload", before.GetProperty("text").GetString());

			// The DevServer accepts connections long before it can hot reload: it still has to
			// load the whole Roslyn workspace, and only then starts watching the project. An edit
			// saved before that is never seen, and the failure looks identical to a hot reload
			// that did nothing. Wait for it to actually be watching.
			await WaitForHotReloadReadyAsync(TimeSpan.FromMinutes(3));

			var replace = await _app.InvokeAsync("od.file.replace-text", xaml, "Before hot reload", "After hot reload");
			Assert.True(replace.GetProperty("success").GetBoolean(), replace.ToString());
			Assert.Contains("Before hot reload", await File.ReadAllTextAsync(xaml));
			await InvokeShortcutAsync("Ctrl+S");
			await WaitForFileTextAsync(xaml, "After hot reload", TimeSpan.FromSeconds(10));

			JsonElement after;
			try {
				after = await WaitForProbeAsync(port, "After hot reload", TimeSpan.FromSeconds(60));
			} catch (TimeoutException ex) {
				var output = await _app.InvokeAsync("od.debug.output");
				throw new Xunit.Sdk.XunitException($"{ex.Message}{Environment.NewLine}Debugger output:{Environment.NewLine}{output}");
			}
			Assert.Equal("After hot reload", after.GetProperty("text").GetString());
			Assert.Equal(before.GetProperty("processId").GetInt32(), after.GetProperty("processId").GetInt32());
			Assert.Equal(before.GetProperty("sessionToken").GetString(), after.GetProperty("sessionToken").GetString());
			await InvokeShortcutAsync("Shift+F5");
			await WaitForDebuggerAsync(running: false, TimeSpan.FromSeconds(30));
		} finally {
			await _app.InvokeAsync("od.build.set-on-execute", buildPreference.GetProperty("previous").GetString()!);
			var status = await _app.InvokeAsync("od.debug.service-info");
			if (status.GetProperty("isDebugging").GetBoolean())
				await _app.InvokeAsync("od.debug.stop");
		}
	}

	/// <summary>
	/// Runs the command the given keyboard shortcut is bound to, through the same command object
	/// the key binding uses, so this journey still covers build-before-run, Hot Reload adapter
	/// selection, save and debugger lifecycle.
	/// It deliberately does NOT inject an OS keystroke. On this host that is not a reliable
	/// substitute: macOS delivers an ordinary keyDown only to its key window, and a
	/// DevFlow-activated window very often is not key (od.activate reports isActive=false), so
	/// the keystroke is dropped with no event and no error - measured by logging every
	/// WpfWorkbench.OnPreviewKeyDown, where a modifier keydown arrives but the letter or function
	/// key that follows it does not. A literal Ctrl+S is worse still: macOS consumes it as a
	/// system shortcut (it opens Siri), so it never reaches any application at all. Whether the
	/// physical keys are wired to these commands is covered by the bindings themselves.
	/// </summary>
	async Task InvokeShortcutAsync(string gesture)
	{
		var result = await _app.InvokeAsync("od.workbench.invoke-shortcut", gesture);
		Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
	}

	async Task WaitForHotReloadReadyAsync(TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		JsonElement status = default;
		while (DateTime.UtcNow < deadline) {
			status = await _app.InvokeAsync("od.hot-reload.status");
			if (status.TryGetProperty("watching", out var watching) && watching.GetBoolean())
				return;
			await Task.Delay(1000);
		}
		throw new Xunit.Sdk.XunitException($"The Uno DevServer was not watching for changes within {timeout}: {status}");
	}

	async Task WaitForDebuggerAsync(bool running, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		JsonElement status = default;
		while (DateTime.UtcNow < deadline) {
			status = await _app.InvokeAsync("od.debug.service-info");
			if (status.GetProperty("isDebugging").GetBoolean() == running
				&& status.GetProperty("isProcessRunning").GetBoolean() == running)
				return;
			await Task.Delay(250);
		}
		throw new Xunit.Sdk.XunitException($"Debugger did not become running={running} within {timeout}: {status}");
	}

	static int AllocateLoopbackPort()
	{
		using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
	}

	static async Task<JsonElement> WaitForProbeAsync(int port, string expected, TimeSpan timeout)
	{
		// The test runner can inherit a corporate/system HTTP proxy. The Uno agent is
		// intentionally loopback-only, so proxying this request turns a live local
		// listener into a misleading connection failure.
		using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
		var deadline = DateTime.UtcNow + timeout;
		string? last = null;
		while (DateTime.UtcNow < deadline) {
			try {
				using var request = new StringContent("{\"args\":[]}", Encoding.UTF8, "application/json");
				using var response = await client.PostAsync($"http://127.0.0.1:{port}/api/v1/invoke/actions/uno-hot-reload.probe", request);
				var responseText = await response.Content.ReadAsStringAsync();
				if (response.IsSuccessStatusCode) {
					using var envelope = JsonDocument.Parse(responseText);
					var raw = envelope.RootElement.GetProperty("returnValue").GetString();
					if (!string.IsNullOrWhiteSpace(raw)) {
						using var probe = JsonDocument.Parse(raw);
						if (probe.RootElement.TryGetProperty("text", out var text)) {
							last = text.GetString();
							if (last == expected) return probe.RootElement.Clone();
						}
					}
				} else {
					last = $"HTTP {(int)response.StatusCode}: {responseText}";
				}
			} catch (HttpRequestException ex) { last = ex.Message; }
			await Task.Delay(250);
		}
		throw new TimeoutException($"Uno DevFlow probe did not report '{expected}' within {timeout}; last value was '{last ?? "unavailable"}'.");
	}

	static async Task WaitForFileTextAsync(string path, string expected, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline) {
			if (File.Exists(path) && (await File.ReadAllTextAsync(path)).Contains(expected, StringComparison.Ordinal))
				return;
			await Task.Delay(250);
		}
		throw new TimeoutException($"DevServer did not report '{expected}' within {timeout}.");
	}

	async Task RebuildCopiedFixtureAsync()
	{
		var project = Path.Combine(_workDir, "UnoHotReloadFixture.csproj");
		using var process = Process.Start(new ProcessStartInfo("dotnet") {
			WorkingDirectory = _workDir,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			// The copied obj/ lock assets may refer to a previous Uno SDK. Restore in
			// the isolated directory before rebuilding so RemoteControl embeds this
			// copy's project path and a matching Uno dependency graph.
			ArgumentList = { "build", project, "-c", "Debug", "-t:Rebuild", "-v:q" }
		})!;
		var output = await process.StandardOutput.ReadToEndAsync();
		var errors = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, $"Could not rebuild copied Uno fixture:{Environment.NewLine}{output}{Environment.NewLine}{errors}");
	}

	public ValueTask DisposeAsync()
	{
		if (Directory.Exists(_workDir))
			Directory.Delete(_workDir, recursive: true);
		return ValueTask.CompletedTask;
	}

	static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(directory.Replace(source, destination));
		foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
			File.Copy(file, file.Replace(source, destination), overwrite: true);
	}

	static string FindRepositoryGlobalJson(string directory)
	{
		for (var current = directory; current is not null; current = Path.GetDirectoryName(current)) {
			var candidate = Path.Combine(current, "global.json");
			if (File.Exists(candidate)) return candidate;
		}
		throw new FileNotFoundException("Could not locate the repository global.json for the Uno Hot Reload fixture.");
	}
}
