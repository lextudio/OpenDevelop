using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.WpfDesign.AddIn.HotReload;

using Debugger.AddIn.Service.Dap;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Route D for the XAML source generator (XSG), end to end and without a debugger: a real LibreWPF
/// app compiled by the generator runs with the Hot Reload agent, the real Roslyn host captures the
/// launch baseline, a <c>{cs:}</c> edit in the unsaved XAML buffer is turned into a metadata delta
/// (the host reruns the generator on that text), the agent applies it and the generated
/// <c>MetadataUpdateHandler</c> reruns <c>InitializeComponent</c>. The assertion reads the live
/// window, so a delta that applies but changes nothing on screen fails.
///
/// CI does not build the fixture (it uses the published LibreWPF.Xaml.SourceGenerator package, see
/// doc/technotes/xsg-hot-reload.md), so the test skips where that fixture has not been built.
/// </summary>
public sealed class XsgHotReloadEndToEndTests
{
	const string TargetFramework = "net10.0-windows";

	[Fact]
	public Task CsExpressionEdit_ReachesTheLiveWindowThroughACodeDelta() => RunAsync(underDebugger: false, CsExpressionEditAsync);

	[Fact]
	public Task EventHandlerEdit_ReachesTheLiveWindowThroughACodeDelta() => RunAsync(underDebugger: false, EventHandlerEditAsync);

	[Fact]
	public Task NameRemoval_IsARestartAndLeavesTheSessionUsable() => RunAsync(underDebugger: false, NameRemovalAsync);

	[Fact]
	public Task EventHandlerEdit_UnderSharpDbg_ReachesTheLiveWindow() => RunAsync(underDebugger: true, EventHandlerEditAsync);

	[Fact]
	public Task NameRemoval_UnderSharpDbg_IsARestartAndLeavesTheSessionUsable() => RunAsync(underDebugger: true, NameRemovalAsync);

	/// <summary>
	/// The same edit while the application runs under SharpDbg (E3a): the delta goes through
	/// <c>sharpdbg/applyDeltas</c> instead of the agent, without the user pausing the debugger first.
	/// </summary>
	[Fact]
	public Task CsExpressionEdit_UnderSharpDbg_ReachesTheLiveWindow() => RunAsync(underDebugger: true, CsExpressionEditAsync);

	async Task RunAsync(bool underDebugger, Func<Scenario, Task> scenario)
	{
		var token = TestContext.Current.CancellationToken;
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var projectPath = Path.Combine(fixtureDirectory, "XsgLocalFeedFixture.csproj");
		var output = Path.Combine(fixtureDirectory, "bin", CurrentConfiguration(), TargetFramework);
		var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "XsgLocalFeedFixture.exe" : "XsgLocalFeedFixture");
		if (!File.Exists(executable))
			Assert.Skip($"Build the XSG fixture first: {projectPath} (-c {CurrentConfiguration()}).");
		var xamlPath = Path.Combine(fixtureDirectory, "MainWindow.xaml");
		var originalXaml = await File.ReadAllTextAsync(xamlPath, token);

		using var transport = new RoslynHostProcessTransport(LocateRoslynHost(), TimeSpan.FromSeconds(60));
		var protocol = new RemoteRoslynLanguageProtocol(transport);
		await protocol.RoslynProjectLoadAsync(await EvaluateAsync(projectPath, token), token);
		var project = new HotReloadProjectId(projectPath, TargetFramework);
		// The bytes the app is about to load, as HotReloadService snapshots them at launch.
		var baseline = await protocol.RoslynHotReloadCaptureBaselineAsync(new HotReloadBaselineRequest(project,
			await File.ReadAllBytesAsync(Path.Combine(output, "XsgLocalFeedFixture.dll"), token),
			await File.ReadAllBytesAsync(Path.Combine(output, "XsgLocalFeedFixture.pdb"), token)), token);
		Assert.True(baseline.Status == HotReloadDeltaStatus.Applied, baseline.Status + ": " + baseline.Diagnostic);

		Environment.SetEnvironmentVariable("OD_WPF_HOTRELOAD_AGENT", LocateAgent());
		var adapter = new WpfApplicationHotReloadAdapter();
		var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = output };
		var session = await adapter.StartAsync(new HotReloadLaunchContext(project: null, withDebugger: underDebugger), startInfo, token);
		await using (session) {
			var agentSession = Assert.IsType<AgentPipeHotReloadSession>(session);
			var pipeName = startInfo.Environment["WPF_HOTRELOAD_PIPE"]!;
			using var debugger = underDebugger ? new DapSession() : null;
			Process? app = null;
			if (debugger != null) {
				UseDeployedSharpDbg();
				debugger.EnableEditAndContinue = true;
				agentSession.ConfigureCodeDelta(protocol, project, new SharpDbgDeltaApplier(() => debugger));
				await debugger.StartAsync(executable, output, breakAtBeginning: false, cancellationToken: token,
					launchEnvironment: startInfo.Environment.Where(pair => pair.Value != null)
						.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!)).ToArray());
				await debugger.ConfigurationDoneAsync(token);
			} else {
				agentSession.ConfigureCodeDelta(protocol, project);
				app = Process.Start(startInfo)!;
			}
			try {
				Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(90), token),
					$"The agent never became ready (state {session.State}).");
				await scenario(new Scenario(session, pipeName, xamlPath, originalXaml, token));
				if (debugger != null)
					Assert.False(debugger.IsPaused, "Hot Reload under the debugger must not leave the application paused.");
			} finally {
				debugger?.Stop();
				if (app != null && !app.HasExited) {
					app.Kill(entireProcessTree: true);
					app.WaitForExit(10_000);
				}
			}
		}
		// The edit only ever travelled over the pipe and the host protocol.
		Assert.Equal(originalXaml, await File.ReadAllTextAsync(xamlPath, token));
	}

	/// <summary>What a scenario needs from the running fixture.</summary>
	sealed record Scenario(IHotReloadSession Session, string PipeName, string XamlPath, string OriginalXaml, CancellationToken Token);

	static async Task CsExpressionEditAsync(Scenario s)
	{
		const string originalExpression = "{cs: &quot;Hot reload ready&quot;}";
		Assert.Contains(originalExpression, s.OriginalXaml);
		Assert.Equal("Hot reload ready", await QueryAsync(s.PipeName, "Status.Text"));

		var editedXaml = s.OriginalXaml.Replace(originalExpression, "{cs: &quot;Hot reload &quot; + &quot;applied&quot;}");
		Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.Classify(s.OriginalXaml, editedXaml, generatesXamlCode: true));
		var result = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, editedXaml, 1, HotReloadChangeKind.CodeDelta), s.Token);

		Assert.True(result.Outcome == HotReloadOutcome.Applied, result.Outcome + ": " + result.Message);
		Assert.Equal("Hot reload applied", await WaitForStatusAsync(s.PipeName, "Hot reload applied", s.Token));

		// A second edit and then the revert: each delta must build on the generation the
		// application now runs, and reverting must converge on the launch state.
		var secondXaml = s.OriginalXaml.Replace(originalExpression, "{cs: &quot;Hot reload &quot; + &quot;again&quot;}");
		var second = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, editedXaml, secondXaml, 2, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(second.Outcome == HotReloadOutcome.Applied, second.Outcome + ": " + second.Message);
		Assert.Equal("Hot reload again", await WaitForStatusAsync(s.PipeName, "Hot reload again", s.Token));
		var revert = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, secondXaml, s.OriginalXaml, 3, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(revert.Outcome == HotReloadOutcome.Applied, revert.Outcome + ": " + revert.Message);
		Assert.Equal("Hot reload ready", await WaitForStatusAsync(s.PipeName, "Hot reload ready", s.Token));
	}

	/// <summary>
	/// An event handler is generated code too (<c>+=</c> in <c>InitializeComponent</c>). The fixture's root
	/// grid names <c>OnLoadedA</c>; <c>Loaded</c> fires again for every tree the rerun builds, so the
	/// handler that wins shows in <c>Marker.Text</c>. Switching the attribute must reach it, and
	/// reverting must converge on the launch handler.
	/// </summary>
	static async Task EventHandlerEditAsync(Scenario s)
	{
		const string handlerA = "Loaded=\"OnLoadedA\"";
		const string handlerB = "Loaded=\"OnLoadedB\"";
		Assert.Contains(handlerA, s.OriginalXaml);
		Assert.Equal("handler A", await WaitForAsync(s.PipeName, "Marker.Text", "handler A", s.Token));

		var editedXaml = s.OriginalXaml.Replace(handlerA, handlerB);
		Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.Classify(s.OriginalXaml, editedXaml, generatesXamlCode: true));
		var result = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, editedXaml, 1, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(result.Outcome == HotReloadOutcome.Applied, result.Outcome + ": " + result.Message);
		Assert.Equal("handler B", await WaitForAsync(s.PipeName, "Marker.Text", "handler B", s.Token));

		var revert = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, editedXaml, s.OriginalXaml, 2, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(revert.Outcome == HotReloadOutcome.Applied, revert.Outcome + ": " + revert.Message);
		Assert.Equal("handler A", await WaitForAsync(s.PipeName, "Marker.Text", "handler A", s.Token));
	}

	/// <summary>
	/// Removing an <c>x:Name</c> deletes a generated field, which the runtime cannot apply. Two layers must
	/// agree: the workflow predicts it from the XAML (<c>RestartRequired</c>, nothing is sent), and the
	/// Roslyn producer, asked anyway, reports the rude edit. Neither may touch the live window or poison the
	/// delta chain: a valid <c>{cs:}</c> edit still applies afterwards, and reverting converges on launch.
	/// </summary>
	static async Task NameRemovalAsync(Scenario s)
	{
		const string spare = "x:Name=\"Spare\" ";
		Assert.Contains(spare, s.OriginalXaml);
		Assert.Equal("spare", await QueryAsync(s.PipeName, "Spare.Text"));

		var removedXaml = s.OriginalXaml.Replace(spare, "");
		Assert.Equal(HotReloadChangeKind.RestartRequired, HotReloadWorkflow.Classify(s.OriginalXaml, removedXaml, generatesXamlCode: true));

		// Layer 1: the workflow's verdict short-circuits; nothing reaches the app.
		var predicted = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, removedXaml, 1, HotReloadChangeKind.RestartRequired), s.Token);
		Assert.Equal(HotReloadOutcome.RestartRequired, predicted.Outcome);
		Assert.Equal("spare", await QueryAsync(s.PipeName, "Spare.Text"));
		Assert.Equal("Hot reload ready", await QueryAsync(s.PipeName, "Status.Text"));

		// Layer 2: the producer is the authority. Forced through as a code delta it must agree.
		var forced = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, removedXaml, 2, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(forced.Outcome == HotReloadOutcome.RestartRequired, forced.Outcome + ": " + forced.Message);
		Assert.Equal("spare", await QueryAsync(s.PipeName, "Spare.Text"));

		// The chain survives: an unrelated valid edit applies, and reverting reaches the launch state.
		const string originalExpression = "{cs: &quot;Hot reload ready&quot;}";
		var editedXaml = s.OriginalXaml.Replace(originalExpression, "{cs: &quot;after rude edit&quot;}");
		var valid = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, editedXaml, 3, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(valid.Outcome == HotReloadOutcome.Applied, valid.Outcome + ": " + valid.Message);
		Assert.Equal("after rude edit", await WaitForStatusAsync(s.PipeName, "after rude edit", s.Token));
		Assert.Equal("spare", await QueryAsync(s.PipeName, "Spare.Text"));
		var revert = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, editedXaml, s.OriginalXaml, 4, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(revert.Outcome == HotReloadOutcome.Applied, revert.Outcome + ": " + revert.Message);
		Assert.Equal("Hot reload ready", await WaitForStatusAsync(s.PipeName, "Hot reload ready", s.Token));
	}

	/// <summary>
	/// DapSession looks for the adapter beside the add-in assembly or under the host's <c>Debugger</c>
	/// folder; in a test run that is the test output, so point the latter at the deployed add-in
	/// (which carries the pinned LeXtudio.SharpDbg.Cli package) rather than a sibling checkout's build.
	/// </summary>
	static void UseDeployedSharpDbg()
	{
		var deployed = Path.Combine(RepositoryRoot(), "AddIns", "Debugger");
		Assert.True(File.Exists(Path.Combine(deployed, "SharpDbg.Cli.dll")), "Build the debugger add-in first: " + deployed);
		var link = Path.Combine(AppContext.BaseDirectory, "Debugger");
		if (!Directory.Exists(link))
			Directory.CreateSymbolicLink(link, deployed);
	}

	/// <summary>
	/// The project's real compiler inputs, as the IDE's evaluation would send them: sources, resolved
	/// references, analyzers (the generator) and AdditionalFiles (the XAML).
	/// </summary>
	static async Task<LanguageServiceProjectSnapshot> EvaluateAsync(string projectPath, CancellationToken token)
	{
		var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
		var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var argument in new[] { "msbuild", projectPath, "-p:Configuration=" + CurrentConfiguration(),
			"-t:ResolveReferences;GenerateGlobalUsings;GenerateAssemblyInfo", "-getItem:Compile", "-getItem:ReferencePath",
			"-getItem:Analyzer", "-getItem:AdditionalFiles", "-getProperty:DefineConstants", "-getProperty:LangVersion",
			"-getProperty:Nullable" })
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start)!;
		var stdout = process.StandardOutput.ReadToEndAsync(token);
		var stderr = process.StandardError.ReadToEndAsync(token);
		await process.WaitForExitAsync(token);
		Assert.True(process.ExitCode == 0, await stdout + await stderr);
		using var json = JsonDocument.Parse(await stdout);
		string[] Items(string name) => json.RootElement.GetProperty("Items").GetProperty(name).EnumerateArray()
			.Select(item => item.GetProperty("FullPath").GetString()!).ToArray();
		string Property(string name) => json.RootElement.GetProperty("Properties").GetProperty(name).GetString()!;
		return new LanguageServiceProjectSnapshot(projectPath, "C#", Items("Compile"), Items("ReferencePath"),
			Array.Empty<string>(), Property("DefineConstants").Split(';', StringSplitOptions.RemoveEmptyEntries),
			Property("LangVersion"), Property("Nullable"), TargetFramework, Items("Analyzer"), Items("AdditionalFiles"));
	}

	// The handler queues InitializeComponent on the window's dispatcher, so the value arrives later.
	static Task<string> WaitForStatusAsync(string pipeName, string expected, CancellationToken token)
		=> WaitForAsync(pipeName, "Status.Text", expected, token);

	static async Task<string> WaitForAsync(string pipeName, string query, string expected, CancellationToken token)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
		string text;
		while ((text = await QueryAsync(pipeName, query)) != expected && DateTime.UtcNow < deadline)
			await Task.Delay(200, token);
		return text;
	}

	static async Task<string> QueryAsync(string pipeName, string query)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (true) {
			try {
				using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				await pipe.ConnectAsync(2000);
				var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
				await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = "query", query }));
				var line = await new StreamReader(pipe, new UTF8Encoding(false)).ReadLineAsync();
				using var document = JsonDocument.Parse(line ?? throw new IOException("empty response"));
				return document.RootElement.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
			} catch (Exception ex) when ((ex is IOException || ex is TimeoutException) && DateTime.UtcNow < deadline) {
				await Task.Delay(200);
			}
		}
	}

	static string LocateRoslynHost()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "RoslynHost", "ICSharpCode.Roslyn.Host.dll");
		Assert.True(File.Exists(path), "The test build did not prepare its RoslynHost dependency: " + path);
		return path;
	}

	static string LocateAgent() => Path.Combine(RepositoryRoot(), "src", "Main", "HotReload", "WpfHotReload.Agent",
		"bin", CurrentConfiguration(), "net10.0-windows", "WpfHotReload.Agent.dll");

	static string CurrentConfiguration() =>
		Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)))!;

	static string RepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory != null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
			directory = Path.GetDirectoryName(directory);
		Assert.NotNull(directory);
		return directory;
	}
}
