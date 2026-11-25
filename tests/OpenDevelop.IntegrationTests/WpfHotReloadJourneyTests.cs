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
		// The self-contained fixture owns the XAML whose named elements form the agent contract.
		var xaml = Path.Combine(RepositoryRoot(), "tests", "fixtures", "WpfHotReloadFixture", "SamplePane.xaml");
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
	/// The designer on the XSG path. The fixture project enables the generator, so Simpler XAML is on: an
	/// old-style document (MainWindow, every namespace declared) and a new-style one (ThirdWindow, none)
	/// both load in the WPF designer, and neither file is rewritten by loading. The BAML case (old-style XAML in a
	/// project that did not enable XSG) is the rest of the designer journeys.
	/// </summary>
	[Fact]
	public async Task XsgProject_OldAndNewStyleXaml_BothLoadInTheDesigner()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var opened = await _app.ReopenSolutionAsync(Path.Combine(fixtureDirectory, "XsgLocalFeedFixture.slnx"));
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		foreach (var name in new[] { "MainWindow.xaml", "ThirdWindow.xaml" }) {
			var xaml = Path.Combine(fixtureDirectory, name);
			var before = await File.ReadAllTextAsync(xaml);
			await OpenAsActiveDocumentAsync(xaml);
			JsonElement status = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await _app.InvokeAsync("od.wpf-designer.status");
				return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
			}, TimeSpan.FromSeconds(90)), $"{name}: {status}");
			Assert.Equal(before, await File.ReadAllTextAsync(xaml));
			await _app.InvokeAsync("od.close-active-view");
		}
	}

	/// <summary>
	/// What the designer shows for a C# expression value (<c>Text="{= 'Hot reload ready'}"</c>, the syntax of .NET MAUI that the
	/// source generator follows): the Properties pad row after selecting the element, with a literal as the control.
	/// </summary>
	[Fact]
	public async Task XsgProject_CsExpressionValue_IsShownAsWrittenInTheDesigner()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var opened = await _app.ReopenSolutionAsync(Path.Combine(fixtureDirectory, "XsgLocalFeedFixture.slnx"));
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		var xaml = Path.Combine(fixtureDirectory, "MainWindow.xaml");
		var before = await File.ReadAllTextAsync(xaml);
		await OpenAsActiveDocumentAsync(xaml);
		JsonElement status = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			status = await _app.InvokeAsync("od.wpf-designer.status");
			return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
		}, TimeSpan.FromSeconds(90)), status.ToString());

		var selected = await _app.InvokeAsync("od.wpf-designer.select", "Status");
		Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
		JsonElement text = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			text = await _app.InvokeAsync("od.wpf-designer.properties-pad.get", "Text");
			return text.GetProperty("success").GetBoolean();
		}, TimeSpan.FromSeconds(30)), text.ToString());

		// The designer cannot evaluate the expression, so it shows the authored text (an empty field would invite typing over it). The design
		// instance keeps its default and the attribute is never rewritten by loading (asserted at the end).
		Assert.Equal("{= 'Hot reload ready'}", text.GetProperty("value").GetString());
		Assert.False(text.GetProperty("isReadOnly").GetBoolean(), text.ToString());

		// The control: the same read on a literal returns the literal, so the empty value above is the expression's, not the action's.
		var marker = await _app.InvokeAsync("od.wpf-designer.select", "Marker");
		Assert.True(marker.GetProperty("success").GetBoolean(), marker.ToString());
		JsonElement literal = default;
		Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
			literal = await _app.InvokeAsync("od.wpf-designer.properties-pad.get", "Text");
			return literal.GetProperty("success").GetBoolean() && literal.GetProperty("selectedName").GetString() == "Marker";
		}, TimeSpan.FromSeconds(30)), literal.ToString());
		Assert.Equal("not loaded", literal.GetProperty("value").GetString());
		Assert.Equal(before, await File.ReadAllTextAsync(xaml));
		await _app.InvokeAsync("od.close-active-view");
	}

	/// <summary>
	/// Editing a C# expression value (<c>Text="{OnLoadedB}"</c>, the syntax of .NET MAUI that the XAML source generator follows) in the
	/// text editor: the real language server colours it as C# (strings, names), completes names from the page's x:Class, reports the
	/// generator's errors while typing, shows a hover, and goes to the member's definition in the code-behind. Works on a copy of the fixture under tmp/.
	/// </summary>
	[Fact]
	public async Task XsgProject_CsExpressionValue_HasColourCompletionAndDefinitionInTheEditor()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-editing-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory).Where(f => Path.GetFileName(f) != "App.xaml"))
				File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
			var project = Path.Combine(copy, "XsgLocalFeedFixture.csproj");
			var projectText = await File.ReadAllTextAsync(project);
			const string appItem = "<None Include=\"App.xaml\" />";
			Assert.Contains(appItem, projectText);
			await File.WriteAllTextAsync(project, projectText.Replace(appItem, ""));

			// The fixtures folder turns central package versions off for the projects under it; the copy sits outside it.
			File.Copy(Path.Combine(fixtureDirectory, "..", "Directory.Packages.props"), Path.Combine(copy, "Directory.Packages.props"));
			// The language server loads the project for its types (x:Class members), which needs a restored copy.
			var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet") {
				ArgumentList = { "restore", project },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			})!;
			var restoreOutput = await restore.StandardOutput.ReadToEndAsync();
			Assert.True(restore.WaitForExit(180_000) && restore.ExitCode == 0, "dotnet restore failed: " + restoreOutput);

			await File.WriteAllTextAsync(Path.Combine(copy, "Customer.cs"),
				"namespace XsgLocalFeedFixture;\npublic class Customer { public string Nickname { get; set; } = \"\"; public int Visits { get; set; } }\n");
			var xaml = Path.Combine(copy, "MainWindow.xaml");
			await File.WriteAllTextAsync(xaml, """
				<Window x:Class="XsgLocalFeedFixture.MainWindow"
				        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				        xmlns:local="clr-namespace:XsgLocalFeedFixture"
				        Title="expressions">
				  <Grid Loaded="OnLoadedA" x:DataType="local:Customer">
				    <StackPanel>
				      <TextBlock x:Name="Status" Text="{= 'Hot reload ready'}" />
				      <TextBlock x:Name="Marker" Text="not loaded" />
				      <TextBlock x:Name="Spare" Text="{OnLoadedB}" />
				      <TextBlock x:Name="Probe" Text="{OnLo}" />
				      <TextBlock x:Name="Mine" Text="{Visits + 1}" />
				      <TextBlock x:Name="Typing" Text="{Nick}" />
				      <Button x:Name="Saver" Click="{async (s, e) => await System.Threading.Tasks.Task.Delay(1)}" />
				    </StackPanel>
				  </Grid>
				</Window>
				""");

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
			Assert.True((await _app.InvokeAsync("od.open-file", xaml)).GetProperty("opened").GetBoolean());

			var lines = (await File.ReadAllTextAsync(xaml)).Replace("\r\n", "\n").Split('\n');
			int LineOf(string needle) => Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal)) + 1;
			int ColumnAfter(int line, string needle) => lines[line - 1].IndexOf(needle, StringComparison.Ordinal) + needle.Length + 1;

			// Completion: the caret after "{OnLo" offers the page's methods.
			var probeLine = LineOf("x:Name=\"Probe\"");
			JsonElement completions = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				completions = await _app.InvokeAsync("od.completions", xaml, probeLine, ColumnAfter(probeLine, "{OnLo"));
				return completions.TryGetProperty("count", out var count) && count.GetInt32() > 0;
			}, TimeSpan.FromSeconds(90)), "no completions inside the expression: " + completions);
			var names = completions.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayText").GetString()).ToArray();
			Assert.Contains("OnLoadedA", names);
			Assert.Contains("OnLoadedB", names);

			// The DataContext (x:DataType) is searched first: its members complete, and hover and definition find them in its type.
			var typingLine = LineOf("x:Name=\"Typing\"");
			JsonElement dataContext = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				dataContext = await _app.InvokeAsync("od.completions", xaml, typingLine, ColumnAfter(typingLine, "{Nick"));
				return dataContext.TryGetProperty("count", out var count) && count.GetInt32() > 0;
			}, TimeSpan.FromSeconds(60)), "no DataContext completions: " + dataContext);
			Assert.Contains("Nickname", dataContext.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayText").GetString()));
			var mineLine = LineOf("x:Name=\"Mine\"");
			JsonElement visits = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				visits = await _app.InvokeAsync("od.quick-info", xaml, mineLine, ColumnAfter(mineLine, "{Vis") + 1);
				return visits.TryGetProperty("found", out var f) && f.GetBoolean();
			}, TimeSpan.FromSeconds(60)), "no hover on a DataContext member: " + visits);
			Assert.Contains("Visits", visits.GetProperty("text").GetString());
			var customerDefinition = await _app.InvokeAsync("od.go-to-definition", xaml, mineLine, ColumnAfter(mineLine, "{Vis") + 1);
			Assert.True(customerDefinition.TryGetProperty("count", out var customerFound) && customerFound.GetInt32() > 0, customerDefinition.ToString());
			Assert.EndsWith("Customer.cs", customerDefinition.GetProperty("targets")[0].GetProperty("filePath").GetString());

			// Colour: the string of the explicit expression and the name of the bare one are C# tokens.
			var statusLine = LineOf("x:Name=\"Status\"");
			var spareLine = LineOf("x:Name=\"Spare\"");
			var tokens = await _app.InvokeAsync("od.semantic-tokens", xaml);
			Assert.False(tokens.TryGetProperty("error", out _), tokens.ToString());
			var all = tokens.GetProperty("tokens").EnumerateArray()
				.Select(t => (Line: t.GetProperty("startLine").GetInt32(), Column: t.GetProperty("startColumn").GetInt32(), Type: t.GetProperty("type").GetString()))
				.ToArray();
			Assert.Contains(all, t => t.Line == statusLine && t.Type == "string");
			Assert.Contains(all, t => t.Line == spareLine && t.Type == "variable");

			// Diagnostics: the generator's own error for an async event lambda (LWXSG0011), shown while typing with the build's id.
			JsonElement diagnostics = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				diagnostics = await _app.InvokeAsync("od.diagnostics", xaml);
				return diagnostics.TryGetProperty("diagnostics", out var list) && list.EnumerateArray().Any(d => d.GetProperty("id").GetString() == "LWXSG0011");
			}, TimeSpan.FromSeconds(60)), "no LWXSG0011 for the async lambda: " + diagnostics);

			// Hover: the member under the caret, described from the code-behind.
			JsonElement quickInfo = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				quickInfo = await _app.InvokeAsync("od.quick-info", xaml, spareLine, ColumnAfter(spareLine, "{OnLoaded") + 1);
				return quickInfo.TryGetProperty("found", out var f) && f.GetBoolean();
			}, TimeSpan.FromSeconds(60)), "no hover on the expression's name: " + quickInfo);
			Assert.Contains("OnLoadedB", quickInfo.GetProperty("text").GetString());

			// Go to definition: the method in the code-behind.
			var definition = await _app.InvokeAsync("od.go-to-definition", xaml, spareLine, ColumnAfter(spareLine, "{OnLoaded") + 1);
			Assert.True(definition.TryGetProperty("count", out var found) && found.GetInt32() > 0, definition.ToString());
			Assert.EndsWith("MainWindow.xaml.cs", definition.GetProperty("targets")[0].GetProperty("filePath").GetString());
		} finally {
			try { await _app.InvokeAsync("od.close-active-view"); } catch { }
			try { Directory.Delete(copy, recursive: true); } catch { }
		}
	}

	/// <summary>
	/// Simpler XAML as in .NET MAUI: a control of the project's own <c>global using</c> namespace is written without an <c>xmlns</c>
	/// (<c>&lt;Fancy/&gt;</c>), and the WPF designer finds it. The project is built so the designer can load its assembly; the file stays
	/// byte-identical. Works on a copy of the fixture under tmp/.
	/// </summary>
	[Fact]
	public async Task XsgProject_GlobalUsingType_LoadsInTheDesignerWithoutAnXmlns()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-globalusing-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory).Where(f => Path.GetFileName(f) != "App.xaml"))
				File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
			File.Copy(Path.Combine(fixtureDirectory, "..", "Directory.Packages.props"), Path.Combine(copy, "Directory.Packages.props"));
			var project = Path.Combine(copy, "XsgLocalFeedFixture.csproj");
			var projectText = await File.ReadAllTextAsync(project);
			const string appItem = "<None Include=\"App.xaml\" />";
			Assert.Contains(appItem, projectText);
			await File.WriteAllTextAsync(project, projectText.Replace(appItem, ""));
			await File.WriteAllTextAsync(Path.Combine(copy, "Fancy.cs"),
				"namespace XsgLocalFeedFixture.Controls;\npublic class Fancy : System.Windows.Controls.TextBlock { }\n");
			await File.WriteAllTextAsync(Path.Combine(copy, "GlobalUsings.cs"), "global using XsgLocalFeedFixture.Controls;\n");

			var xaml = Path.Combine(copy, "MainWindow.xaml");
			await File.WriteAllTextAsync(xaml, """
				<Window x:Class="XsgLocalFeedFixture.MainWindow"
				        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
				        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
				        Title="global using">
				  <Grid Loaded="OnLoadedA">
				    <StackPanel>
				      <TextBlock x:Name="Status" Text="x" />
				      <TextBlock x:Name="Marker" Text="not loaded" />
				      <TextBlock x:Name="Spare" Text="spare" />
				      <Fancy x:Name="FancyOne" Text="from a global using" />
				    </StackPanel>
				  </Grid>
				</Window>
				""");

			// The designer loads the project's built assembly.
			var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet") {
				ArgumentList = { "build", project, "-c", "Debug" },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			})!;
			var buildOutput = await build.StandardOutput.ReadToEndAsync();
			Assert.True(build.WaitForExit(240_000) && build.ExitCode == 0, "dotnet build failed: " + buildOutput);

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
			var before = await File.ReadAllTextAsync(xaml);
			await OpenAsActiveDocumentAsync(xaml);
			JsonElement status = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await _app.InvokeAsync("od.wpf-designer.status");
				return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
			}, TimeSpan.FromSeconds(90)), status.ToString());

			// The support question "is this project XSG or BAML, and what is on" is answerable without reading the csproj.
			var xsg = await _app.InvokeAsync("od.wpf-designer.xsg-status");
			Assert.Equal("xsg", xsg.GetProperty("compileMode").GetString());
			Assert.True(xsg.GetProperty("simplerXaml").GetBoolean(), xsg.ToString());
			Assert.True(xsg.GetProperty("cSharpExpressions").GetBoolean(), xsg.ToString());
			Assert.Contains("XsgLocalFeedFixture.Controls", xsg.GetProperty("globalUsingNamespaces").EnumerateArray().Select(e => e.GetString()));
		// The status bar says this XAML is compiled by the generator, with the features on, and offers the generated code.
			JsonElement statusBar = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				statusBar = await _app.InvokeAsync("od.status-bar.message");
				return statusBar.TryGetProperty("message", out var message) && (message.GetString() ?? "").Contains("LibreWPF source generator");
			}, TimeSpan.FromSeconds(30)), "no XSG status bar message: " + statusBar);
			Assert.Contains("Simpler XAML", statusBar.GetProperty("message").GetString());
			Assert.Contains("C# expressions", statusBar.GetProperty("message").GetString());
			Assert.Equal("View Generated Code", statusBar.GetProperty("actionText").GetString());

			// The generated C# of this window, read from the Roslyn host's compilation (the build does not write it to obj/ by default).
			JsonElement generated = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				generated = await _app.InvokeAsync("od.xsg.generated-sources", xaml);
				return generated.TryGetProperty("count", out var count) && count.GetInt32() > 0;
			}, TimeSpan.FromSeconds(90)), "no generated source for the XAML: " + generated);
			var mainSource = generated.GetProperty("sources").EnumerateArray().First();
			Assert.Contains("MainWindow", mainSource.GetProperty("hintName").GetString());
			Assert.Contains("InitializeComponent", mainSource.GetProperty("text").GetString());
			Assert.Contains("FancyOne", mainSource.GetProperty("text").GetString());
			// The View Generated Code command opens that text as a read-only file in the editor.
			var viewed = await _app.InvokeAsync("od.wpf-designer.view-generated-code");
			Assert.True(viewed.GetProperty("opened").GetBoolean(), viewed.ToString());
			Assert.True(viewed.GetProperty("readOnly").GetBoolean(), viewed.ToString());
			Assert.EndsWith("MainWindow.g.cs", viewed.GetProperty("path").GetString());
			Assert.Contains("InitializeComponent", viewed.GetProperty("text").GetString());
			await _app.InvokeAsync("od.close-active-view");
			await OpenAsActiveDocumentAsync(xaml);
			var selected = await _app.InvokeAsync("od.wpf-designer.select", "FancyOne");
			Assert.True(selected.GetProperty("success").GetBoolean(), "the global-using type is not in the design tree: " + selected);
			Assert.Equal(before, await File.ReadAllTextAsync(xaml));
		} finally {
			try { await _app.InvokeAsync("od.close-active-view"); } catch { }
			try { Directory.Delete(copy, recursive: true); } catch { }
		}
	}

	/// <summary>
	/// A generator error is reported where the author wrote it: an async event lambda (LWXSG0011) breaks the build and the Error List names
	/// the XAML file and the line of the attribute, not the generated code. Works on a copy of the fixture under tmp/.
	/// </summary>
	[Fact]
	public async Task XsgProject_GeneratorError_ReachesTheErrorListOnTheXamlLine()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-errorlist-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory))
				File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
			File.Copy(Path.Combine(fixtureDirectory, "..", "Directory.Packages.props"), Path.Combine(copy, "Directory.Packages.props"));
			var xaml = Path.Combine(copy, "MainWindow.xaml");
			var lines = (await File.ReadAllTextAsync(xaml)).Replace("\r\n", "\n").Split('\n').ToList();
			var insertAt = lines.FindIndex(l => l.Contains("x:Name=\"Marker\""));
			Assert.True(insertAt >= 0, "the fixture has no Marker element to put the bad attribute beside");
			lines.Insert(insertAt, "    <Button x:Name=\"Broken\" Click=\"{async (s, e) => await System.Threading.Tasks.Task.Delay(1)}\" />");
			await File.WriteAllTextAsync(xaml, string.Join("\n", lines));
			var expectedLine = insertAt + 1;

			// Restore only (the build is the thing that must fail), so the IDE opens a project that already has its assets file.
			var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet") {
				ArgumentList = { "restore", Path.Combine(copy, "XsgLocalFeedFixture.csproj") },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			})!;
			var restoreOutput = await restore.StandardOutput.ReadToEndAsync();
			Assert.True(restore.WaitForExit(240_000) && restore.ExitCode == 0, "dotnet restore failed: " + restoreOutput);

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
			await _app.InvokeAsync("od.error-list.clear");
			var build = await _app.InvokeAsync("od.build-solution", "", "rebuild");
			Assert.True(build.GetProperty("errorCount").GetInt32() > 0, "the async lambda must break the build: " + build);

			JsonElement errorList = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				errorList = await _app.InvokeAsync("od.error-list");
				return errorList.GetProperty("tasks").EnumerateArray().Any(t => (t.GetProperty("description").GetString() ?? "").Contains("LWXSG0011"));
			}, TimeSpan.FromSeconds(60)), "no LWXSG0011 in the Error List: " + errorList);
			var task = errorList.GetProperty("tasks").EnumerateArray().First(t => (t.GetProperty("description").GetString() ?? "").Contains("LWXSG0011"));
			Assert.Equal("Error", task.GetProperty("type").GetString());
			Assert.EndsWith("MainWindow.xaml", (task.GetProperty("file").GetString() ?? "").Replace('\\', '/'));
			Assert.Equal(expectedLine, task.GetProperty("line").GetInt32());
		} finally {
			try { Directory.Delete(copy, recursive: true); } catch { }
		}
	}

	/// <summary>
	/// A C# expression typed into the Properties pad is written as the attribute text exactly as typed (a plain string with a leading brace
	/// would be escaped as <c>{}</c> literal text), and a literal typed over an expression replaces it. Works on a copy of the fixture under tmp/;
	/// the saved file differs from the original by exactly the edited attribute.
	/// </summary>
	[Fact]
	public async Task XsgProject_CsExpressionValue_CanBeEditedInTheDesignerAndIsSavedAsTyped()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-pad-edit-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory).Where(f => Path.GetFileName(f) != "App.xaml"))
				File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
			var project = Path.Combine(copy, "XsgLocalFeedFixture.csproj");
			var projectText = await File.ReadAllTextAsync(project);
			const string appItem = "<None Include=\"App.xaml\" />";
			Assert.Contains(appItem, projectText);
			await File.WriteAllTextAsync(project, projectText.Replace(appItem, ""));

			var xaml = Path.Combine(copy, "MainWindow.xaml");
			var original = await File.ReadAllTextAsync(xaml);
			const string expression = "Text=\"{= 'Hot reload ready'}\"";
			Assert.Contains(expression, original);

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
			await OpenAsActiveDocumentAsync(xaml);
			JsonElement status = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await _app.InvokeAsync("od.wpf-designer.status");
				return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
			}, TimeSpan.FromSeconds(90)), status.ToString());

			async Task<JsonElement> EditStatusText(string value)
			{
				JsonElement selected = default;
				Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
					selected = await _app.InvokeAsync("od.wpf-designer.select", "Status");
					return selected.GetProperty("success").GetBoolean();
				}, TimeSpan.FromSeconds(30)), selected.ToString());
				JsonElement edit = default;
				Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
					edit = await _app.InvokeAsync("od.wpf-designer.properties-pad.edit", "Text", value);
					return edit.GetProperty("success").GetBoolean();
				}, TimeSpan.FromSeconds(30)), "the Properties pad did not take '" + value + "': " + edit);
				return edit;
			}

			var typed = await EditStatusText("{= 'edited in the pad'}");
			Assert.Equal("{= 'edited in the pad'}", typed.GetProperty("after").GetString());
			var saved = await _app.InvokeAsync("od.file.save", xaml);
			Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
			Assert.Equal(original.Replace(expression, "Text=\"{= 'edited in the pad'}\""), await File.ReadAllTextAsync(xaml));

			// A literal typed over the expression replaces it, and an expression typed over a literal is written as typed too.
			await EditStatusText("plain text");
			await EditStatusText("{Count + 1}");
			saved = await _app.InvokeAsync("od.file.save", xaml);
			Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
			Assert.Equal(original.Replace(expression, "Text=\"{Count + 1}\""), await File.ReadAllTextAsync(xaml));

			// Undo and redo bring the expression text back as written (the same undo action as any property edit).
			var undone = await _app.InvokeAsync("od.wpf-designer.undo");
			Assert.True(undone.GetProperty("success").GetBoolean(), undone.ToString());
			saved = await _app.InvokeAsync("od.file.save", xaml);
			Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
			Assert.Equal(original.Replace(expression, "Text=\"plain text\""), await File.ReadAllTextAsync(xaml));
			var redone = await _app.InvokeAsync("od.wpf-designer.redo");
			Assert.True(redone.GetProperty("success").GetBoolean(), redone.ToString());
			saved = await _app.InvokeAsync("od.file.save", xaml);
			Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
			Assert.Equal(original.Replace(expression, "Text=\"{Count + 1}\""), await File.ReadAllTextAsync(xaml));
		} finally {
			try { await _app.InvokeAsync("od.close-active-view"); } catch { }
			try { Directory.Delete(copy, recursive: true); } catch { }
		}
	}

	/// <summary>
	/// The XAML source generator's compiled-binding directives, <c>x:DataType</c> and <c>x:CompileBindings</c>, are unknown members to the
	/// run-time System.Xaml (it throws "Cannot set unknown member"), but the designer reads documents with its own XamlDom parser, which
	/// skips every attribute of the XAML namespace except <c>x:Name</c>. A document that uses them must therefore still load in the designer
	/// (old-style and new-style alike), and loading must leave the file byte-identical. Works on a copy of the fixture under tmp/.
	/// </summary>
	[Fact]
	public async Task XsgProject_CompiledBindingDirectives_DoNotStopTheDesigner()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-directives-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory).Where(f => Path.GetFileName(f) != "App.xaml"))
				File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
			var project = Path.Combine(copy, "XsgLocalFeedFixture.csproj");
			var projectText = await File.ReadAllTextAsync(project);
			const string appItem = "<None Include=\"App.xaml\" />";
			Assert.Contains(appItem, projectText);
			await File.WriteAllTextAsync(project, projectText.Replace(appItem, ""));

			const string newStyleRoot = "<Window x:Class=\"XsgLocalFeedFixture.ThirdWindow\"";
			const string oldStyleRoot = "<Window x:Class=\"XsgLocalFeedFixture.MainWindow\"";
			var directives = " x:DataType=\"x:String\" x:CompileBindings=\"True\"";
			foreach (var (name, root) in new[] { ("ThirdWindow.xaml", newStyleRoot), ("MainWindow.xaml", oldStyleRoot) }) {
				var path = Path.Combine(copy, name);
				var text = await File.ReadAllTextAsync(path);
				Assert.Contains(root, text);
				await File.WriteAllTextAsync(path, text.Replace(root, root + directives));
			}

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

			foreach (var name in new[] { "ThirdWindow.xaml", "MainWindow.xaml" }) {
				var xaml = Path.Combine(copy, name);
				var before = await File.ReadAllTextAsync(xaml);
				Assert.Contains("x:CompileBindings", before);
				await OpenAsActiveDocumentAsync(xaml);
				JsonElement status = default;
				Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
					status = await _app.InvokeAsync("od.wpf-designer.status");
					return status.GetProperty("active").GetBoolean()
						&& (status.GetProperty("designerLoaded").GetBoolean() || status.TryGetProperty("loadError", out _));
				}, TimeSpan.FromSeconds(90)), $"{name}: {status}");
				Assert.True(status.GetProperty("designerLoaded").GetBoolean(), $"{name} with x:DataType/x:CompileBindings did not load: {status}");
				Assert.Equal(before, await File.ReadAllTextAsync(xaml));
				await _app.InvokeAsync("od.close-active-view");
			}
		} finally {
			try { Directory.Delete(copy, recursive: true); } catch (IOException) { }
		}
	}

	/// <summary>
	/// The reverse control of the journey above: the same project with <c>LibreWpfSimplerXaml</c> switched off. Simpler XAML is
	/// then off for the project, so the designer must read a document without xmlns as written and fail to load it (it reports a
	/// load error), while an old-style document of the same project still loads. A copy of the fixture under tmp/ is
	/// used, so no tracked file is touched.
	/// </summary>
	[Fact]
	public async Task XsgProject_WithSimplerXamlOff_NewStyleXamlDoesNotLoadInTheDesigner()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var copy = Path.Combine(RepositoryRoot(), "tmp", "xsg-simpler-off-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(copy);
		try {
			// Without App.xaml: its new-style root is itself invalid with Simpler XAML off (the designer then fails for the whole
			// project), which would hide what this test isolates, the document under test.
			foreach (var file in Directory.EnumerateFiles(fixtureDirectory).Where(f => Path.GetFileName(f) != "App.xaml")) {
				var target = Path.Combine(copy, Path.GetFileName(file));
				File.Copy(file, target);
			}
			var project = Path.Combine(copy, "XsgLocalFeedFixture.csproj");
			var text = await File.ReadAllTextAsync(project);
			const string overall = "<UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator>";
			Assert.Contains(overall, text);
			const string appItem = "<None Include=\"App.xaml\" />";
			Assert.Contains(appItem, text);
			await File.WriteAllTextAsync(project, text.Replace(appItem, "")
				.Replace(overall, overall + "<LibreWpfSimplerXaml>false</LibreWpfSimplerXaml>"));

			var opened = await _app.ReopenSolutionAsync(Path.Combine(copy, "XsgLocalFeedFixture.slnx"));
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

			var main = Path.Combine(copy, "MainWindow.xaml");
			await OpenAsActiveDocumentAsync(main);
			JsonElement status = default;
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await _app.InvokeAsync("od.wpf-designer.status");
				return status.GetProperty("active").GetBoolean() && status.GetProperty("designerLoaded").GetBoolean();
			}, TimeSpan.FromSeconds(90)), $"old-style MainWindow.xaml must still load: {status}");
			await _app.InvokeAsync("od.close-active-view");

			var third = Path.Combine(copy, "ThirdWindow.xaml");
			await OpenAsActiveDocumentAsync(third);
			Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => {
				status = await _app.InvokeAsync("od.wpf-designer.status");
				return status.TryGetProperty("loadError", out var error) && error.ValueKind == JsonValueKind.String
					&& !string.IsNullOrEmpty(error.GetString());
			}, TimeSpan.FromSeconds(90)), $"new-style ThirdWindow.xaml must not load with Simpler XAML off: {status}");
			Assert.False(status.GetProperty("designerLoaded").GetBoolean(), status.ToString());
			Assert.Contains("undeclared prefix", status.GetProperty("loadError").GetString(), StringComparison.Ordinal);
			await _app.InvokeAsync("od.close-active-view");
		} finally {
			try { Directory.Delete(copy, recursive: true); } catch (IOException) { }
		}
	}

	/// <summary>
	/// Route D through the IDE for a project compiled by the XAML source generator: a C# expression
	/// edit cannot be pushed as XAML text, so Apply has to classify it as a code delta, have the
	/// Roslyn host rerun the generator on the unsaved buffer, and send the metadata delta to the
	/// agent, whose generated handler rebuilds the window. It covers what the Base-level
	/// XsgHotReloadEndToEndTests bypass: the IDE's own project evaluation (the generator is a
	/// package analyzer, the XAML an AdditionalFiles item), the launch baseline capture and the
	/// classification behind the Apply command. CI does not build the fixture, so the journey skips
	/// where it has not been built.
	/// </summary>
	[Fact]
	public async Task XsgProject_CsExpressionEdit_AppliesAsACodeDelta()
	{
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var built = Path.Combine(fixtureDirectory, "bin", "Debug", "net10.0-windows",
			OperatingSystem.IsWindows() ? "XsgLocalFeedFixture.exe" : "XsgLocalFeedFixture");
		if (!File.Exists(built))
			Assert.Skip("Build tests/fixtures/XsgLocalFeedFixture (Debug) first.");
		var xaml = Path.Combine(fixtureDirectory, "MainWindow.xaml");
		var originalXaml = await File.ReadAllTextAsync(xaml);

		var opened = await _app.ReopenSolutionAsync(Path.Combine(fixtureDirectory, "XsgLocalFeedFixture.slnx"));
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		await OpenAsActiveDocumentAsync(xaml);
		try {
			await StartHotReloadAsync();
			var session = await WaitForSessionAsync("Ready", TimeSpan.FromSeconds(120));
			var endpoint = session.GetProperty("diagnostics").GetProperty("endpoint").GetString();
			Assert.False(string.IsNullOrWhiteSpace(endpoint));
			Assert.Equal("Hot reload ready", await QueryAgentAsync(endpoint!, "Status.Text"));

			var replaced = await _app.InvokeAsync("od.file.replace-text", xaml,
				"{= 'Hot reload ready'}", "{= 'Hot reload from ' + 'the IDE'}");
			Assert.True(replaced.GetProperty("success").GetBoolean(), replaced.ToString());
			var applied = await _app.InvokeAsync("od.hot-reload.apply-command");
			Assert.True(applied.GetProperty("success").GetBoolean(), applied.ToString());

			try {
				await WaitForAgentValueAsync(endpoint!, "Status.Text", "Hot reload from the IDE", TimeSpan.FromSeconds(60));
			} catch (Xunit.Sdk.XunitException ex) {
				// What the apply did is only reported in the Hot Reload output channel.
				throw new Xunit.Sdk.XunitException(ex.Message + Environment.NewLine + "Hot Reload output: "
					+ await _app.InvokeAsync("od.output-text", "Hot Reload"));
			}
			// Unsaved: the delta came from the editor buffer, not the file.
			Assert.Equal(originalXaml, await File.ReadAllTextAsync(xaml));
		} finally {
			await File.WriteAllTextAsync(xaml, originalXaml);
			await _app.InvokeAsync("od.hot-reload.stop-command");
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
	/// The failure is only ever reported in one place - the "Hot Reload" output category, as
	/// "full apply skipped: ..." appended to an apply that still reports success - so that is what
	/// this reads. (The agent can now read any named element's property, "Name.Property", but a
	/// value that the XML fallback also reaches would look right either way.)
	/// </summary>
	[Fact]
	public async Task Apply_WithDynamicResourceOnDependencyProperty_StillRunsFullApply()
	{
		var solution = _app.WpfHotReloadFixturePath;
		var xaml = Path.Combine(RepositoryRoot(), "tests", "fixtures", "WpfHotReloadFixture", "SamplePane.xaml");
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
			var options = await _app.InvokeAsync("od.project-browser.open-project-options", "HotReloadClassLib");
			Assert.True(options.TryGetProperty("success", out var optionsSuccess) && optionsSuccess.GetBoolean(), options.ToString());
			Assert.True(options.TryGetProperty("projectOptionsOpen", out var projectOptionsOpen) && projectOptionsOpen.GetBoolean(), options.ToString());
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
		while (directory is not null && !Directory.Exists(Path.Combine(directory, "externals", "WpfDesigner")))
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
