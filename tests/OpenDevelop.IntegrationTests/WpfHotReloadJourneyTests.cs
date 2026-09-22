using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Hot Reload journey as a user performs it, through the IDE: open a WPF solution, run the
/// Start Hot Reload command, edit the XAML in the editor, run Apply, and confirm the running
/// application changed.
/// This is deliberately separate from the adapter-level coverage in OpenDevelop.Base.Tests. That
/// test drives the adapter and session directly and launches the application itself, so it proves
/// the transport but not the toolbar command, adapter selection from a real project, launch
/// configuration through the project's own launch path, or the session the workbench then owns -
/// which is exactly the gap this closes.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class WpfHotReloadJourneyTests
{
	readonly OpenDevelopAppFixture _app;

	public WpfHotReloadJourneyTests(OpenDevelopAppFixture app) => _app = app;

	[Fact]
	public async Task StartHotReload_ThenApply_UpdatesTheRunningApplication()
	{
		var solution = _app.WpfHotReloadFixturePath;
		// The fixture is only an SDK wrapper; the XAML it links lives in vscode-wpf's own sample,
		// which is what the agent's built-in queries are named after.
		var xaml = Path.Combine(RepositoryRoot(), "externals", "vscode-wpf", "sample", "net6.0", "SamplePane.xaml");
		Assert.True(File.Exists(xaml), $"The linked sample XAML must exist: {xaml}");

		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// The adapter has to claim this project on its own, from the real project system.
		var supported = await _app.InvokeAsync("od.hot-reload.status");
		Assert.True(supported.ValueKind != JsonValueKind.Undefined, supported.ToString());

		Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());

		var originalXaml = await File.ReadAllTextAsync(xaml);
		try {
			var started = await _app.InvokeAsync("od.hot-reload.start-command");
			Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());

			var session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(90));
			Assert.Equal("WPF", session.GetProperty("framework").GetString());
			// A WPF session pushes edits, so the workbench must offer a real Apply rather than Save.
			Assert.True(session.GetProperty("canApplyFromIde").GetBoolean());
			Assert.Equal("Apply", session.GetProperty("applyAction").GetString());
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));

			Assert.Equal("Sample pane", await QueryAgentAsync(endpoint!, "PaneTitle.Text"));

			// Edit in the editor and leave the buffer dirty: a WPF session applies buffer text, so
			// the running application must change without the file being saved first.
			var replaced = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Text=\"Sample pane\"", "Text=\"Hot reloaded from the IDE\"");
			Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());
			Assert.Contains("Sample pane", await File.ReadAllTextAsync(xaml));

			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			await WaitForAgentValueAsync(endpoint!, "PaneTitle.Text", "Hot reloaded from the IDE",
				TimeSpan.FromSeconds(30));

			// Untouched parts of the tree keep their values: the apply replaced the pane's content
			// without resetting the rest of the running UI.
			Assert.Equal("Nested control for hot reload smoke tests.",
				await QueryAgentAsync(endpoint!, "PaneBody.Text"));
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await _app.InvokeAsync("od.hot-reload.stop-command");
			var status = await _app.InvokeAsync("od.debug.service-info");
			if (status.TryGetProperty("isDebugging", out var debugging) && debugging.GetBoolean())
				await _app.InvokeAsync("od.debug.stop");
			await _app.InvokeAsync("od.stop-project");
		}
	}

	/// <summary>
	/// A full apply must survive a DynamicResource on a DependencyProperty.
	///
	/// The agent parses the incoming XAML itself with XamlXmlReader/XamlObjectWriter. With a plain
	/// XamlSchemaContext the writer hands markup extensions CLR property members, so
	/// IProvideValueTarget.TargetProperty is not a DependencyProperty and DynamicResourceExtension
	/// throws "A 'DynamicResourceExtension' cannot be set on the 'X' property of type 'Y'" - which
	/// aborted the whole parse. The apply still reported success, because the XML fallback had
	/// already updated the named properties it could reach, and the real failure was only visible
	/// as "full apply skipped: ..." in the output channel.
	///
	/// So this asserts the mechanism, not just an effect: the probe is a NEW named element. The
	/// fallback only writes properties of elements that already exist in the live tree, so an
	/// element that appears at all proves the tree was rebuilt by a real full apply.
	/// </summary>
	[Fact]
	public async Task Apply_WithDynamicResourceOnDependencyProperty_StillRunsFullApply()
	{
		var solution = _app.WpfHotReloadFixturePath;
		var xaml = Path.Combine(RepositoryRoot(), "externals", "vscode-wpf", "sample", "net6.0", "SamplePane.xaml");
		Assert.True(File.Exists(xaml), $"The linked sample XAML must exist: {xaml}");

		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());

		var originalXaml = await File.ReadAllTextAsync(xaml);
		try {
			var started = await _app.InvokeAsync("od.hot-reload.start-command");
			Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());

			var session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(90));
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));

			// The element must not exist yet, or its later presence would prove nothing.
			Assert.Null(await QueryAgentAsync(endpoint!, "PaneDynamicProbe.Text"));

			// Background is a DependencyProperty, so this is the exact shape that used to abort the
			// parse. The key deliberately does not exist: DynamicResource resolves lazily and an
			// unresolved key is not an error, which keeps the test about the parse, not the lookup.
			var withDynamicResource = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Background=\"LightBlue\"",
				"Background=\"{DynamicResource SampleHotReloadBrush}\"");
			Assert.True(withDynamicResource.GetProperty("success").GetBoolean(), withDynamicResource.ToString());

			var withNewElement = await _app.InvokeAsync("od.file.replace-text", xaml,
				"<TextBlock x:Name=\"PaneBody\"",
				"<TextBlock x:Name=\"PaneDynamicProbe\" Text=\"Full apply ran\" />\n      <TextBlock x:Name=\"PaneBody\"");
			Assert.True(withNewElement.GetProperty("success").GetBoolean(), withNewElement.ToString());

			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			// Only a rebuilt tree can contain an element the previous tree never had.
			await WaitForAgentValueAsync(endpoint!, "PaneDynamicProbe.Text", "Full apply ran",
				TimeSpan.FromSeconds(30));
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await _app.InvokeAsync("od.hot-reload.stop-command");
			var status = await _app.InvokeAsync("od.debug.service-info");
			if (status.TryGetProperty("isDebugging", out var debugging) && debugging.GetBoolean())
				await _app.InvokeAsync("od.debug.stop");
			await _app.InvokeAsync("od.stop-project");
		}
	}

	static string RepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null && !Directory.Exists(Path.Combine(directory, "externals", "vscode-wpf")))
			directory = Path.GetDirectoryName(directory);
		Assert.NotNull(directory);
		return directory!;
	}

	async Task<JsonElement> WaitForSessionAsync(string expectedState, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		JsonElement session = default;
		while (DateTime.UtcNow < deadline) {
			session = await _app.InvokeAsync("od.hot-reload.session");
			if (session.TryGetProperty("active", out var active) && active.GetBoolean()) {
				var state = session.GetProperty("state").GetString();
				// Ready is reported by the adapter once the agent answers; it is never inferred
				// from the application process merely existing.
				if (state == expectedState)
					return session;
				if (state is "Failed" or "Disconnected" or "Stopped" or "Unsupported")
					throw new Xunit.Sdk.XunitException($"The Hot Reload session ended in {state}: {session}");
			}
			await Task.Delay(500);
		}
		throw new Xunit.Sdk.XunitException(
			$"No Hot Reload session reached {expectedState} within {timeout}; last was {session}");
	}

	static async Task WaitForAgentValueAsync(string endpoint, string query, string expected, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		string? last = null;
		while (DateTime.UtcNow < deadline) {
			last = await QueryAgentAsync(endpoint, query);
			if (last == expected)
				return;
			await Task.Delay(250);
		}
		throw new Xunit.Sdk.XunitException(
			$"The running application never reported '{expected}' for '{query}'; last was '{last}'.");
	}

	/// <summary>
	/// Asks the running application directly, bypassing the session under test: an apply that
	/// reports success while the UI is unchanged is the failure this journey exists to catch.
	/// The agent serves one request per connection and recreates its listener afterwards, so on
	/// Unix - where the endpoint is a socket file that briefly stops existing - a failed connect
	/// means "retry", not "the application is gone".
	/// </summary>
	static async Task<string?> QueryAgentAsync(string endpoint, string query)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		Exception? last = null;
		while (DateTime.UtcNow < deadline) {
			try {
				using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
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
			} catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) {
				last = ex;
				await Task.Delay(200);
			}
		}
		throw new TimeoutException($"The agent did not answer '{query}' in time.", last);
	}
}
