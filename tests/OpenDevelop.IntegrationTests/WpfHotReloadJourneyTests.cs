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

		await OpenAsActiveDocumentAsync(xaml);

		var originalXaml = await File.ReadAllTextAsync(xaml);
		try {
			await StartHotReloadAsync();

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
			await WaitForSessionInactiveAsync();
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
	/// Asserting this through the live tree is not possible: the agent answers only a fixed set of
	/// named queries (PaneTitle.Text, PaneBody.Text, PaneList.SelectedIndex,
	/// PrimaryButton.Background) and returns null for anything else, so a probe element the test
	/// introduces is invisible to it. The failure is only ever reported in one place - the
	/// "Hot Reload" output category, as "full apply skipped: ..." appended to an apply that still
	/// reports success - so that is what this reads.
	/// </summary>
	[Fact]
	public async Task Apply_WithDynamicResourceOnDependencyProperty_StillRunsFullApply()
	{
		var solution = _app.WpfHotReloadFixturePath;
		var xaml = Path.Combine(RepositoryRoot(), "externals", "vscode-wpf", "sample", "net6.0", "SamplePane.xaml");
		Assert.True(File.Exists(xaml), $"The linked sample XAML must exist: {xaml}");

		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		await OpenAsActiveDocumentAsync(xaml);

		var originalXaml = await File.ReadAllTextAsync(xaml);
		try {
			await StartHotReloadAsync();

			var session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(90));
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));

			// Background is a DependencyProperty, so this is the exact shape that used to abort the
			// parse. The key deliberately does not exist: DynamicResource resolves lazily and an
			// unresolved key is not an error, which keeps the test about the parse, not the lookup.
			var withDynamicResource = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Background=\"LightBlue\"",
				"Background=\"{DynamicResource SampleHotReloadBrush}\"");
			Assert.True(withDynamicResource.GetProperty("success").GetBoolean(), withDynamicResource.ToString());

			// Also change a property the agent can read back, so a silently inert apply cannot pass.
			var retitled = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Text=\"Sample pane\"", "Text=\"Dynamic resource applied\"");
			Assert.True(retitled.GetProperty("success").GetBoolean(), retitled.ToString());

			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			try {
				await WaitForAgentValueAsync(endpoint!, "PaneTitle.Text", "Dynamic resource applied",
					TimeSpan.FromSeconds(30));
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
				var view = await _app.InvokeAsync("od.active-view");
				throw new Xunit.Sdk.XunitException($"{ex.Message}\nactive view: {view}\nHot Reload output:\n{log.GetProperty("text").GetString()}");
			}

			// The regression itself. apply-command returns {success:true} either way, because the
			// XML fallback still updates the named properties it can reach; only the output text
			// distinguishes a full apply from a parse that aborted on the DynamicResource.
			var output = await _app.InvokeAsync("od.output-text", "Hot Reload");
			var text = output.GetProperty("text").GetString() ?? string.Empty;
			Assert.Contains("Applied", text);
			Assert.DoesNotContain("full apply skipped", text);
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await _app.InvokeAsync("od.hot-reload.stop-command");
			var status = await _app.InvokeAsync("od.debug.service-info");
			if (status.TryGetProperty("isDebugging", out var debugging) && debugging.GetBoolean())
				await _app.InvokeAsync("od.debug.stop");
			await _app.InvokeAsync("od.stop-project");
			await WaitForSessionInactiveAsync();
		}
	}

	/// <summary>
	/// GitHub issue #18: Hot Reload on a WPF class-library project. The library has no entry point,
	/// so its Debug options name an external program (the host executable that loads it), exactly
	/// as one does to debug a library. Hot Reload must launch that program with the agent and apply
	/// the library's XAML edit to the control the host created from it.
	/// </summary>
	[Fact]
	public Task StartHotReload_OnClassLibraryWithExternalProgram_UpdatesTheHostedControl()
		=> RunClassLibExternalProgramJourneyAsync(Path.GetDirectoryName(_app.WpfHotReloadClassLibFixturePath)!, LibreWpfSdk);

	const string LibreWpfSdk = "LibreWPF.Sdk";
	const string MicrosoftSdk = "Microsoft.NET.Sdk";

	/// <summary>
	/// The Microsoft-adapter half of GitHub issue #18. The adapter is chosen from the project's own
	/// SDK, so the Microsoft fixture is what makes the Windows-only backend reachable at all; the
	/// LibreWPF test above covers the same user journey through the other adapter, and because it
	/// carries no OS gate it is also the LibreWPF-on-Windows case when the suite runs on Windows.
	/// </summary>
	[Fact(Skip = "Microsoft WPF class libraries only run on Windows.", SkipUnless = nameof(MicrosoftWpfAvailable))]
	public Task StartHotReload_OnMicrosoftClassLibraryWithExternalProgram_UpdatesTheHostedControl()
		=> RunClassLibExternalProgramJourneyAsync(Path.GetDirectoryName(_app.WpfHotReloadClassLibFixtureMicrosoftPath)!, MicrosoftSdk);

	public static bool MicrosoftWpfAvailable => OperatingSystem.IsWindows();

	async Task RunClassLibExternalProgramJourneyAsync(string fixtureDirectory, string expectedSdk)
	{
		AssertFixtureUsesSdk(fixtureDirectory, expectedSdk);
		var solution = Path.Combine(fixtureDirectory, "WpfHotReloadClassLibFixture.slnx");
		var xaml = Path.Combine(fixtureDirectory, "Lib", "LibraryPane.xaml");
		var hostDirectory = Path.Combine(fixtureDirectory, "Host", "bin", "Debug", "net10.0-windows");
		var hostExe = Path.Combine(hostDirectory, "HotReloadClassLibHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
		Assert.True(File.Exists(hostExe), $"The host fixture must be built first: {hostExe}");

		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		var originalXaml = await File.ReadAllTextAsync(xaml);
		// The Debug options page saves StartProgram into the project file itself, so the tracked
		// fixture has to be put back or it keeps this machine's absolute paths.
		var projectFile = Path.Combine(fixtureDirectory, "Lib", "HotReloadClassLib.csproj");
		var originalProject = await File.ReadAllTextAsync(projectFile);
		try {
			// The user's setup: a class library whose Debug options start an external program.
			var selected = await _app.InvokeAsync("od.project-browser.select", "Project", "HotReloadClassLib");
			Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
			var options = await _app.InvokeAsync("od.project-browser.open-selected");
			Assert.True(options.GetProperty("projectOptionsOpen").GetBoolean(), options.ToString());
			var configured = await _app.InvokeAsync("od.project-options.configure-debug-host", hostExe, hostDirectory);
			Assert.True(configured.GetProperty("success").GetBoolean(), configured.ToString());
			Assert.True(configured.GetProperty("startable").GetBoolean(), configured.ToString());
			// Start Hot Reload runs the SOLUTION's startup project; the library has to be it, and the
			// WPF adapter has to claim it, before the command can possibly start anything.
			Assert.Equal("HotReloadClassLib", configured.GetProperty("startupProject").GetString());
			Assert.True(configured.GetProperty("hotReloadSupported").GetBoolean(), configured.ToString());

			await OpenAsActiveDocumentAsync(xaml);

			await StartHotReloadAsync();

			JsonElement session;
			try {
				session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(90));
			} catch (Xunit.Sdk.XunitException ex) {
				// Silence is the failure mode: the command reports success and builds, and a build
				// error or a refused launch is visible only in the output channels.
				var build = await _app.InvokeAsync("od.output-text", "Build");
				var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
				var debug = await _app.InvokeAsync("od.debug.output");
				var service = await _app.InvokeAsync("od.debug.service-info");
				throw new Xunit.Sdk.XunitException($"{ex.Message}\nBuild output:\n{build.GetProperty("text").GetString()}\nHot Reload output:\n{log.GetProperty("text").GetString()}\nDebug output: {debug}\nDebug service: {service}");
			}
			Assert.Equal("WPF", session.GetProperty("framework").GetString());
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));

			Assert.Equal("Library pane", await QueryAgentAsync(endpoint!, "PaneTitle.Text"));

			var replaced = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Text=\"Library pane\"", "Text=\"Library hot reloaded\"");
			Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());

			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			try {
				await WaitForAgentValueAsync(endpoint!, "PaneTitle.Text", "Library hot reloaded", TimeSpan.FromSeconds(30));
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
				throw new Xunit.Sdk.XunitException($"{ex.Message}\nHot Reload output:\n{log.GetProperty("text").GetString()}");
			}
			Assert.Equal("Defined in a class library.", await QueryAgentAsync(endpoint!, "PaneBody.Text"));

				await ApplySecondEditAndAssertHealthyAsync(xaml, endpoint!, "Library hot reloaded");
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await File.WriteAllTextAsync(projectFile, originalProject);
			await StopHotReloadQuietlyAsync();
		}
	}

	/// <summary>
	/// The other half of GitHub issue #18: no external program is nominated for the library at all.
	/// Instead the runnable host is a solution project and the user makes it the startup project,
	/// as one does for a library that is only ever exercised through an application. The library has
	/// no entry point, so Start Hot Reload can only launch the host - and the library's XAML edit
	/// must still reach the control the host created, because Apply sends the active document and the
	/// agent matches it by path inside the running application. This configuration was previously
	/// untested; the external-program journey below covers the other way to reach the same code.
	/// </summary>
	[Fact]
	public Task StartHotReload_WithTheHostAsStartupProject_UpdatesTheLibrarysControl()
		=> RunClassLibStartupProjectJourneyAsync(Path.GetDirectoryName(_app.WpfHotReloadClassLibFixturePath)!, LibreWpfSdk);

	/// <summary>The Microsoft-adapter half of the same startup-project setup (see above).</summary>
	[Fact(Skip = "Microsoft WPF class libraries only run on Windows.", SkipUnless = nameof(MicrosoftWpfAvailable))]
	public Task StartHotReload_WithTheMicrosoftHostAsStartupProject_UpdatesTheLibrarysControl()
		=> RunClassLibStartupProjectJourneyAsync(Path.GetDirectoryName(_app.WpfHotReloadClassLibFixtureMicrosoftPath)!, MicrosoftSdk);

	async Task RunClassLibStartupProjectJourneyAsync(string fixtureDirectory, string expectedSdk)
	{
		AssertFixtureUsesSdk(fixtureDirectory, expectedSdk);
		var solution = Path.Combine(fixtureDirectory, "WpfHotReloadHostedLibFixture.slnx");
		var xaml = Path.Combine(fixtureDirectory, "Lib", "LibraryPane.xaml");
		var hostDirectory = Path.Combine(fixtureDirectory, "Host", "bin", "Debug", "net10.0-windows");
		var hostExe = Path.Combine(hostDirectory, "HotReloadClassLibHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
		Assert.True(File.Exists(hostExe), $"The host fixture must be built first: {hostExe}");

		var opened = await _app.ReopenSolutionAsync(solution);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		var originalXaml = await File.ReadAllTextAsync(xaml);
		try {
			// The user's setup this time: the library stays a plain library with no Start external
			// program, and the host becomes the startup project through the context-menu command a
			// user clicks (Set as Startup Project).
			Assert.DoesNotContain("StartProgram",
				await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "Lib", "HotReloadClassLib.csproj")));
			var selected = await _app.InvokeAsync("od.project-browser.set-startup-project", "HotReloadClassLibHost");
			Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
			Assert.Equal("HotReloadClassLibHost", selected.GetProperty("startupProject").GetString());

			await OpenAsActiveDocumentAsync(xaml);

			await StartHotReloadAsync();

			JsonElement session;
			try {
				session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(90));
			} catch (Xunit.Sdk.XunitException ex) {
				var build = await _app.InvokeAsync("od.output-text", "Build");
				var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
				var debug = await _app.InvokeAsync("od.debug.output");
				throw new Xunit.Sdk.XunitException($"{ex.Message}\nBuild output:\n{build.GetProperty("text").GetString()}\nHot Reload output:\n{log.GetProperty("text").GetString()}\nDebug output: {debug}");
			}
			Assert.Equal("WPF", session.GetProperty("framework").GetString());
			// A Ready session already proves the host was launched: the startup project is the
			// library, which has no entry point, so nothing else could have been started. (A process
			// probe is not a substitute - the launch runs the project's DLL, so the process name is
			// the runtime's, not the host's.)
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));

			Assert.Equal("Library pane", await QueryAgentAsync(endpoint!, "PaneTitle.Text"));

			var replaced = await _app.InvokeAsync("od.file.replace-text", xaml,
				"Text=\"Library pane\"", "Text=\"Library hot reloaded from a hosted startup\"");
			Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());

			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			try {
				await WaitForAgentValueAsync(endpoint!, "PaneTitle.Text", "Library hot reloaded from a hosted startup", TimeSpan.FromSeconds(30));
			} catch (Exception ex) when (ex is not OperationCanceledException) {
				var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
				throw new Xunit.Sdk.XunitException($"{ex.Message}\nHot Reload output:\n{log.GetProperty("text").GetString()}");
			}

			await ApplySecondEditAndAssertHealthyAsync(xaml, endpoint!, "Library hot reloaded from a hosted startup");
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await StopHotReloadQuietlyAsync();
		}
	}

	/// <summary>
	/// The Microsoft and LibreWPF fixtures are twins that differ only in the SDK their projects name,
	/// and the adapter is chosen from that SDK. If one were edited to the other's SDK, both journeys
	/// would still pass while testing the same backend twice, so pin it. This is the only proof that a
	/// journey ran on its intended backend: the session does not report which agent served it.
	/// </summary>
	static void AssertFixtureUsesSdk(string fixtureDirectory, string expectedSdk)
	{
		foreach (var project in new[] { Path.Combine("Lib", "HotReloadClassLib.csproj"), Path.Combine("Host", "HotReloadClassLibHost.csproj") }) {
			var first = File.ReadLines(Path.Combine(fixtureDirectory, project)).First(l => l.Contains("<Project"));
			Assert.Contains($"Sdk=\"{expectedSdk}\"", first);
		}
	}

	/// <summary>
	/// A second edit after the first apply, then a check that nothing went wrong along the way. The
	/// first apply can succeed on a session that is then unusable (a dropped agent connection, a build
	/// that left the host stale), and apply-command reports success even when the agent fell back to
	/// partial XML updates, so the output channel is the only place those show.
	/// </summary>
	async Task ApplySecondEditAndAssertHealthyAsync(string xaml, string endpoint, string currentTitle)
	{
		var next = currentTitle + " again";
		var replaced = await _app.InvokeAsync("od.file.replace-text", xaml,
			$"Text=\"{currentTitle}\"", $"Text=\"{next}\"");
		Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());
		var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
		Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

		var log = await _app.InvokeAsync("od.output-text", "Hot Reload");
		var logText = log.GetProperty("text").GetString() ?? string.Empty;
		try {
			await WaitForAgentValueAsync(endpoint, "PaneTitle.Text", next, TimeSpan.FromSeconds(30));
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			throw new Xunit.Sdk.XunitException($"{ex.Message}\nHot Reload output:\n{logText}");
		}

		log = await _app.InvokeAsync("od.output-text", "Hot Reload");
		logText = log.GetProperty("text").GetString() ?? string.Empty;
		Assert.DoesNotContain("full apply skipped", logText);
	}

	/// <summary>
	/// Ends the session and the launched host from a <c>finally</c> block. A cleanup call that throws
	/// there replaces the exception from the try block, which hid the real reason of a failed journey,
	/// so a cleanup failure is reported on stderr instead of thrown.
	/// </summary>
	async Task StopHotReloadQuietlyAsync()
	{
		try {
			await _app.InvokeAsync("od.hot-reload.stop-command");
			var status = await _app.InvokeAsync("od.debug.service-info");
			if (status.TryGetProperty("isDebugging", out var debugging) && debugging.GetBoolean())
				await _app.InvokeAsync("od.debug.stop");
			await _app.InvokeAsync("od.stop-project");
			await WaitForSessionInactiveAsync();
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			Console.Error.WriteLine($"Hot Reload cleanup failed (the journey result is unaffected): {ex.Message}");
		}
	}

	/// <summary>
	/// Both tests share one IDE instance, so the application one of them launched must be gone before
	/// the next Hot Reload build, which otherwise fails on the output the still-running process holds.
	/// <para>
	/// Hot Reload launches it WITHOUT the debugger (ExecuteWithHotReload), like Visual Studio's Start
	/// Without Debugging: the IDE keeps no handle to it, stop-command ends only the session, and
	/// od.stop-project only knows what od.run-project started. Nothing in the IDE ends it, so waiting
	/// for it to exit only ever timed out - a minute per test with its window left on screen, which
	/// looked like the suite had hung. The test launched it, so the test ends it.
	/// </para>
	/// </summary>
	async Task WaitForSessionInactiveAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (DateTime.UtcNow < deadline) {
			var session = await _app.InvokeAsync("od.hot-reload.session");
			if (!session.TryGetProperty("active", out var active) || !active.GetBoolean())
				break;
			await Task.Delay(250);
		}

		foreach (var process in FixtureProcesses()) {
			try {
				process.Kill(entireProcessTree: true);
				process.WaitForExit(10_000);
			} catch {
				// Already gone between the enumeration and the kill; nothing to clean up.
			}
		}
	}

	/// <summary>
	/// Runs Start Hot Reload as the toolbar button would: only while it is enabled. Opening a XAML
	/// file makes the designer start a build of its own, and the command is disabled until that
	/// build ends; invoking it meanwhile launches nothing.
	/// </summary>
	async Task StartHotReloadAsync()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		JsonElement started;
		do {
			started = await _app.InvokeAsync("od.hot-reload.start-command");
			if (started.GetProperty("success").GetBoolean())
				return;
			await Task.Delay(500);
		} while (DateTime.UtcNow < deadline);
		Assert.Fail($"Start Hot Reload never became available: {started}");
	}

	/// <summary>
	/// Opens <paramref name="xaml"/> and waits until it really is the active document, which is
	/// what apply-command targets. Reopening the solution restores the documents a previous test
	/// left open (WpfDesignerAssemblyLockTests opens this sample's MainWindow.xaml) asynchronously,
	/// after od.open-file has returned - so the restore can take the active view back, and the
	/// apply then went to MainWindow.xaml ("[WPF] MainWindow.xaml: Applied") while the test waited
	/// for SamplePane's title to change.
	/// </summary>
	async Task OpenAsActiveDocumentAsync(string xaml)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		JsonElement view = default;
		while (DateTime.UtcNow < deadline) {
			Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());
			await Task.Delay(500);
			view = await _app.InvokeAsync("od.active-view");
			if (view.TryGetProperty("fileName", out var name) && string.Equals(name.GetString(), xaml, StringComparison.Ordinal))
				return;
		}
		Assert.Fail($"{xaml} never became the active document; active view: {view}");
	}

	static System.Diagnostics.Process[] FixtureProcesses() =>
		System.Diagnostics.Process.GetProcessesByName("WpfHotReloadFixture")
			.Concat(System.Diagnostics.Process.GetProcessesByName("HotReloadClassLibHost")).ToArray();

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
