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
/// launch baseline, a C# expression edit in the unsaved XAML buffer is turned into a metadata delta
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

	/// <summary>
	/// C# expressions over the DataContext, in the syntax of .NET MAUI: <c>{Age + 1}</c> and <c>{$'{Label}: {Age}'}</c> under
	/// <c>x:DataType="local:Probe"</c> are MultiBindings with a generated converter, and the live window shows the values the converter
	/// computed from the DataContext (the real LibreWPF binding engine, not a stand-in).
	/// </summary>
	[Fact]
	public Task ExpressionOverTheDataContext_IsComputedByTheLiveWindow() => RunAsync(underDebugger: false, async s => {
		// The control: a plain {Binding} on the same DataContext, so a failure below is the expression's and not the environment's.
		Assert.Equal("41", await WaitForAsync(s.PipeName, "Classic.Text", "41", s.Token));
		// A simple path and the explicit {.Name} are bindings; an event lambda is a handler that runs when the event fires (Loaded).
		Assert.Equal("Ada", await WaitForAsync(s.PipeName, "Simple.Text", "Ada", s.Token));
		Assert.Equal("Ada", await WaitForAsync(s.PipeName, "Explicit.Text", "Ada", s.Token));
		Assert.Equal("lambda ran", await WaitForAsync(s.PipeName, "LambdaRan.Text", "lambda ran", s.Token));
		Assert.Equal("42", await WaitForAsync(s.PipeName, "Computed.Text", "42", s.Token));
		Assert.Equal("Ada: 41", await WaitForAsync(s.PipeName, "Described.Text", "Ada: 41", s.Token));
	});

	/// <summary>
	/// Editing a compound expression over the DataContext reaches the live window: the evaluation is a method of the page, updated in place by the
	/// delta (a lambda would be lost and the old tree would crash calling it), and the rerun builds a MultiBinding that computes the new value.
	/// An edit that changes how many DataContext names it reads, and the revert, must converge too.
	/// </summary>
	[Fact]
	public Task CompoundExpressionEdit_ReachesTheLiveWindowThroughACodeDelta() => RunAsync(underDebugger: false, CompoundExpressionEditAsync);

	/// <summary>The same compound-expression edits while the application runs under SharpDbg.</summary>
	[Fact]
	public Task CompoundExpressionEdit_UnderSharpDbg_ReachesTheLiveWindow() => RunAsync(underDebugger: true, CompoundExpressionEditAsync);

	static async Task CompoundExpressionEditAsync(Scenario s)
	{
		Assert.Equal("42", await WaitForAsync(s.PipeName, "Computed.Text", "42", s.Token));
		const string original = "Text=\"{Age + 1}\"";
		Assert.Contains(original, s.OriginalXaml);

		var first = s.OriginalXaml.Replace(original, "Text=\"{Age + 2}\"");
		Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.Classify(s.OriginalXaml, first, generatesXamlCode: true));
		var applied = await s.Session.ApplyAsync(new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, first, 1, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(applied.Outcome == HotReloadOutcome.Applied, applied.Outcome + ": " + applied.Message);
		Assert.Equal("43", await WaitForAsync(s.PipeName, "Computed.Text", "43", s.Token));

		var second = s.OriginalXaml.Replace(original, "Text=\"{$'{Label} is {Age + 1}'}\"");
		var again = await s.Session.ApplyAsync(new HotReloadDocumentChange(s.XamlPath, first, second, 2, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(again.Outcome == HotReloadOutcome.Applied, again.Outcome + ": " + again.Message);
		Assert.Equal("Ada is 42", await WaitForAsync(s.PipeName, "Computed.Text", "Ada is 42", s.Token));

		var revert = await s.Session.ApplyAsync(new HotReloadDocumentChange(s.XamlPath, second, s.OriginalXaml, 3, HotReloadChangeKind.CodeDelta), s.Token);
		Assert.True(revert.Outcome == HotReloadOutcome.Applied, revert.Outcome + ": " + revert.Message);
		Assert.Equal("42", await WaitForAsync(s.PipeName, "Computed.Text", "42", s.Token));
	}

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

	/// <summary>
	/// A breakpoint on the XAML line of an expression binds and hits: the generator's <c>#line</c> makes the XAML the document of the
	/// generated code, so SharpDbg reports the stop in MainWindow.xaml at that line (G8 of xsg-gaps.md).
	/// </summary>
	[Theory]
	[InlineData("Text=\"{= 'Hot reload ready'}\"")] // a constant expression, evaluated in InitializeComponent
	[InlineData("Text=\"{Age + 1}\"")] // a compound expression over the DataContext
	[InlineData("Loaded=\"{(s, e) => LambdaRan!.Text = 'lambda ran'}\"")] // an event lambda, run when the window loads
	[InlineData("Text=\"{$'{Label}: {Age}'}\"")] // an interpolated string over the DataContext
	public async Task BreakpointOnAnExpressionLine_HitsInTheXamlFile(string expressionAttribute)
	{
		var token = TestContext.Current.CancellationToken;
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var output = Path.Combine(fixtureDirectory, "bin", CurrentConfiguration(), TargetFramework);
		var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "XsgLocalFeedFixture.exe" : "XsgLocalFeedFixture");
		if (!File.Exists(executable))
			Assert.Skip($"Build the XSG fixture first: {fixtureDirectory} (-c {CurrentConfiguration()}).");
		var xamlPath = Path.Combine(fixtureDirectory, "MainWindow.xaml");
		var line = (await File.ReadAllLinesAsync(xamlPath, token)).Select((text, index) => (text, line: index + 1))
			.First(l => l.text.Contains(expressionAttribute)).line;

		UseDeployedSharpDbg();
		using var debugger = new DapSession();
		var stopped = new TaskCompletionSource<DapStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
		debugger.Stopped += e => Volatile.Read(ref stopped).TrySetResult(e);
		try {
			await debugger.StartAsync(executable, output, breakAtBeginning: false, cancellationToken: token);
			var verified = await debugger.SetBreakpointsAsync(xamlPath, new[] { (line, (string)null, (string)null, (string)null) });
			await debugger.ConfigurationDoneAsync(token);
			var hit = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
			var frames = await debugger.GetStackFramesAsync(hit.ThreadId);
			Assert.NotEmpty(frames);
			Assert.True(frames[0].FilePath != null && frames[0].FilePath.EndsWith("MainWindow.xaml", StringComparison.Ordinal),
				"top frame is " + frames[0].FilePath + ":" + frames[0].Line + " (breakpoint verified: " + string.Join(",", verified.Select(v => v.Verified + "@" + v.Line)) + ")");
			Assert.Equal(line, frames[0].Line);

			// One step over from the stop: the next stop is again reported in the XAML file (the statements around the expression map back to it through #line).
			// Repeated steps, step into and other expression shapes are not exercised here.
			Volatile.Write(ref stopped, new TaskCompletionSource<DapStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
			debugger.StepOver();
			var stepped = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
			Assert.Equal("step", stepped.Reason);
			var after = await debugger.GetStackFramesAsync(stepped.ThreadId);
			Assert.True(after[0].FilePath != null && after[0].FilePath.EndsWith("MainWindow.xaml", StringComparison.Ordinal),
				"after a step the top frame is " + after[0].FilePath + ":" + after[0].Line);

			// Five more steps over: each stops with reason "step"; the check ends when a stop leaves MainWindow.xaml. The lines of later stops are not
			// asserted: the generator's #line applies to a whole block, so statements after the first drift past their XAML line (G14 in xsg-gaps.md).
			for (var i = 0; i < 5; i++) {
				Volatile.Write(ref stopped, new TaskCompletionSource<DapStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
				debugger.StepOver();
				stepped = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
				Assert.Equal("step", stepped.Reason);
				after = await debugger.GetStackFramesAsync(stepped.ThreadId);
				if (after[0].FilePath == null || !after[0].FilePath.EndsWith("MainWindow.xaml", StringComparison.Ordinal))
					break;
				Assert.True(after[0].Line >= 1, "a step reported line " + after[0].Line);
			}
		} finally {
			debugger.Stop();
		}
	}

	/// <summary>
	/// The Roslyn host hands back the C# the XAML source generator wrote for a XAML file (G8 of xsg-gaps.md): the generated document whose
	/// <c>#line</c> directives name it, over the protocol the IDE uses.
	/// </summary>
	[Fact]
	public async Task GeneratedSources_OfAXamlFile_AreReadOverTheProtocol()
	{
		var token = TestContext.Current.CancellationToken;
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var projectPath = Path.Combine(fixtureDirectory, "XsgLocalFeedFixture.csproj");
		if (!File.Exists(Path.Combine(fixtureDirectory, "bin", CurrentConfiguration(), TargetFramework, "XsgLocalFeedFixture.dll")))
			Assert.Skip($"Build the XSG fixture first: {projectPath} (-c {CurrentConfiguration()}).");
		var xamlPath = Path.Combine(fixtureDirectory, "MainWindow.xaml");

		using var transport = new RoslynHostProcessTransport(LocateRoslynHost(), TimeSpan.FromSeconds(60));
		var protocol = new RemoteRoslynLanguageProtocol(transport);
		await protocol.RoslynProjectLoadAsync(await EvaluateAsync(projectPath, token), token);

		var sources = await protocol.RoslynGeneratedSourcesAsync(new TextDocumentIdentifier(xamlPath), token);
		var main = Assert.Single(sources, source => source.HintName.Contains("MainWindow", StringComparison.Ordinal));
		Assert.Contains("InitializeComponent", main.Text);
		Assert.Contains("\"" + xamlPath + "\"", main.Text);
		// Another XAML file of the project has its own generated document, not this one's.
		Assert.DoesNotContain(sources, source => source.Text.Contains("SecondWindow.xaml", StringComparison.Ordinal));
		// The same project loaded as a second target-framework slice: each slice answers with its own documents (no duplicates), an unknown one with none.
		var second = await EvaluateAsync(projectPath, token);
		await protocol.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(second.ProjectFileName, second.Language, second.DocumentFileNames,
			second.MetadataReferenceFileNames, second.ProjectReferenceFileNames, second.PreprocessorSymbols, second.LanguageVersion, second.NullableContext,
			"other-slice", second.AnalyzerAssemblyFileNames, second.AdditionalFileNames) { SolutionDirectory = second.SolutionDirectory, GlobalUsings = second.GlobalUsings }, token);
		foreach (var tfm in new string?[] { TargetFramework, "other-slice", null })
			Assert.Single(await protocol.RoslynGeneratedSourcesAsync(new TextDocumentIdentifier(xamlPath, tfm), token),
				source => source.HintName.Contains("MainWindow", StringComparison.Ordinal));
		Assert.Empty(await protocol.RoslynGeneratedSourcesAsync(new TextDocumentIdentifier(xamlPath, "missing"), token));
		// A file that is not an additional file of the project generates nothing.
		Assert.Empty(await protocol.RoslynGeneratedSourcesAsync(new TextDocumentIdentifier(Path.Combine(fixtureDirectory, "Probe.cs")), token));
	}

	/// <summary>
	/// Step into from a breakpoint on a XAML line: four steps in a row each stop with reason "step" in MainWindow.xaml, in the window's
	/// InitializeComponent (why a step into goes no deeper was not measured). The lines of those stops are not asserted:
	/// they follow the generator's #line numbering (G14 in xsg-gaps.md), which is not monotonic (8, 9, 7, 8, 8 were observed).
	/// </summary>
	[Fact]
	public async Task StepInto_FromAnExpressionBreakpoint_StaysInTheXamlFile()
	{
		var token = TestContext.Current.CancellationToken;
		var fixtureDirectory = Path.Combine(RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture");
		var output = Path.Combine(fixtureDirectory, "bin", CurrentConfiguration(), TargetFramework);
		var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "XsgLocalFeedFixture.exe" : "XsgLocalFeedFixture");
		if (!File.Exists(executable))
			Assert.Skip($"Build the XSG fixture first: {fixtureDirectory} (-c {CurrentConfiguration()}).");
		var xamlPath = Path.Combine(fixtureDirectory, "MainWindow.xaml");
		var line = (await File.ReadAllLinesAsync(xamlPath, token)).Select((text, index) => (text, line: index + 1))
			.First(l => l.text.Contains("Text=\"{= 'Hot reload ready'}\"")).line;

		UseDeployedSharpDbg();
		using var debugger = new DapSession();
		var stopped = new TaskCompletionSource<DapStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
		debugger.Stopped += e => Volatile.Read(ref stopped).TrySetResult(e);
		try {
			await debugger.StartAsync(executable, output, breakAtBeginning: false, cancellationToken: token);
			await debugger.SetBreakpointsAsync(xamlPath, new[] { (line, (string)null, (string)null, (string)null) });
			await debugger.ConfigurationDoneAsync(token);
			var hit = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
			Assert.Equal("breakpoint", hit.Reason);
			for (var i = 0; i < 4; i++) {
				Volatile.Write(ref stopped, new TaskCompletionSource<DapStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously));
				debugger.StepInto();
				var step = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
				Assert.Equal("step", step.Reason);
				var frames = await debugger.GetStackFramesAsync(step.ThreadId);
				Assert.NotEmpty(frames);
				Assert.True(frames[0].FilePath != null && frames[0].FilePath.EndsWith("MainWindow.xaml", StringComparison.Ordinal),
					"step into #" + i + " stopped in " + frames[0].FilePath + ":" + frames[0].Line);
				Assert.Contains("InitializeComponent", frames[0].Name);
			}
		} finally {
			debugger.Stop();
		}
	}

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
		const string originalExpression = "{= 'Hot reload ready'}";
		Assert.Contains(originalExpression, s.OriginalXaml);
		Assert.Equal("Hot reload ready", await QueryAsync(s.PipeName, "Status.Text"));

		var editedXaml = s.OriginalXaml.Replace(originalExpression, "{= 'Hot reload ' + 'applied'}");
		Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.Classify(s.OriginalXaml, editedXaml, generatesXamlCode: true));
		var result = await s.Session.ApplyAsync(
			new HotReloadDocumentChange(s.XamlPath, s.OriginalXaml, editedXaml, 1, HotReloadChangeKind.CodeDelta), s.Token);

		Assert.True(result.Outcome == HotReloadOutcome.Applied, result.Outcome + ": " + result.Message);
		Assert.Equal("Hot reload applied", await WaitForStatusAsync(s.PipeName, "Hot reload applied", s.Token));

		// A second edit and then the revert: each delta must build on the generation the
		// application now runs, and reverting must converge on the launch state.
		var secondXaml = s.OriginalXaml.Replace(originalExpression, "{= 'Hot reload ' + 'again'}");
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
	/// delta chain: a valid C# expression edit still applies afterwards, and reverting converges on launch.
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
		const string originalExpression = "{= 'Hot reload ready'}";
		var editedXaml = s.OriginalXaml.Replace(originalExpression, "{= 'after rude edit'}");
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
