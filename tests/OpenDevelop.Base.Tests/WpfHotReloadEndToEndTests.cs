using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.WpfDesign.AddIn.HotReload;

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
	public async Task ApplyDelta_UpdatesALiveWpfAssemblyAndRunsItsMetadataHandler()
	{
		var token = TestContext.Current.CancellationToken;
		var directory = Directory.CreateTempSubdirectory("WpfEnc-");
		try {
			var sourcePath = Path.Combine(directory.FullName, "Program.cs");
			var probePath = Path.Combine(directory.FullName, "result.txt");
			string Source(string value) => $$"""
				using System;
				using System.IO;
				using System.Runtime.CompilerServices;
				using System.Reflection.Metadata;
				[assembly: MetadataUpdateHandler(typeof(Handler))]
				public static class Target { public static string Value() => "{{value}}"; }
				public static class Handler { public static void UpdateApplication(Type[] types) => File.WriteAllText(Environment.GetEnvironmentVariable("ENC_PROBE_PATH")!, Target.Value() + ":" + types.Length); }
				""";
			await File.WriteAllTextAsync(sourcePath, Source("v1"), token);
			var references = ManagedReferences.RuntimeDirectory()
				.Select(path => MetadataReference.CreateFromFile(path));
			CSharpCompilation Compile(string source) => CSharpCompilation.Create("WpfEncProbe",
				[CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: sourcePath)], references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
			var v1 = Compile(Source("v1"));
			using var image = new MemoryStream();
			using var symbols = new MemoryStream();
			var pdbPath = Path.Combine(directory.FullName, "WpfEncProbe.pdb");
			var emitted = v1.Emit(image, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath));
			Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
			var dll = Path.Combine(directory.FullName, "WpfEncProbe.dll");
			await File.WriteAllBytesAsync(dll, image.ToArray(), token);
			await File.WriteAllBytesAsync(pdbPath, symbols.ToArray(), token);
			using var metadata = ModuleMetadata.CreateFromImage(image.ToArray());
			var reader = metadata.GetMetadataReader();
			var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
			var baseline = EmitBaseline.CreateInitialBaseline(v1, metadata,
				_ => EditAndContinueMethodDebugInformation.Create(ImmutableArray<byte>.Empty, ImmutableArray<byte>.Empty),
				_ => default(StandaloneSignatureHandle), hasPortableDebugInformation: true);
			var v2 = Compile(Source("v2"));
			using var md = new MemoryStream(); using var il = new MemoryStream(); using var pdb = new MemoryStream();
			var edit = new SemanticEdit(SemanticEditKind.Update, v1.GetTypeByMetadataName("Target")!.GetMembers("Value")[0], v2.GetTypeByMetadataName("Target")!.GetMembers("Value")[0]);
			var difference = v2.EmitDifference(baseline, [edit], _ => false, md, il, pdb);
			Assert.True(difference.Success, string.Join(Environment.NewLine, difference.Diagnostics));
			Environment.SetEnvironmentVariable("OD_WPF_HOTRELOAD_AGENT", LocateAgentForTest());
			var startInfo = new ProcessStartInfo(LocateFixtureExecutable()) { UseShellExecute = false };
			var adapter = new WpfApplicationHotReloadAdapter();
			await using var session = await adapter.StartAsync(new HotReloadLaunchContext(project: null, withDebugger: false), startInfo, token);
			var agentSession = Assert.IsType<AgentPipeHotReloadSession>(session);
			startInfo.Environment["ENC_PROBE_ASSEMBLY"] = dll;
			startInfo.Environment["ENC_PROBE_PATH"] = probePath;
			var agentLog = startInfo.Environment["WPF_HOTRELOAD_LOG"]!;
			using var app = Process.Start(startInfo); Assert.NotNull(app);
			try {
				Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(30), token));
				(string Result, string Value) response;
				var targetToken = MetadataTokens.GetToken(reader.TypeDefinitions.Single(handle => reader.GetString(reader.GetTypeDefinition(handle).Name) == "Target"));
				try { response = await SendPipeAsync(agentSession.PipeName, new { kind = "applyDelta", mvid, metadata = md.ToArray(), il = il.ToArray(), pdb = pdb.ToArray(), updatedTypeTokens = new[] { targetToken } }); }
				catch (Exception ex) { throw new Xunit.Sdk.XunitException("Agent log:\n" + await File.ReadAllTextAsync(agentLog, token) + "\n" + ex); }
				Assert.StartsWith("ok:", response.Result, StringComparison.OrdinalIgnoreCase);
				for (var i = 0; i < 50 && !File.Exists(probePath); i++) await Task.Delay(100, token);
				Assert.True(File.Exists(probePath), "MetadataUpdateHandler was not invoked after the live delta.");
				Assert.Equal("v2:1", await File.ReadAllTextAsync(probePath, token));
			} finally { if (!app.HasExited) { app.Kill(true); app.WaitForExit(10_000); } }
		} finally { directory.Delete(true); }
	}

	/// <summary>
	/// A merge that throws after it already changed the live tree must not leave the window as a mix
	/// of two texts: the agent re-applies the last text that applied, or the text the IDE says the
	/// application started from. The failure is injected (WPF_HOTRELOAD_TEST_FAULT_AFTER_MERGE) after
	/// a complete merge, so the new text is on screen when the failure happens.
	/// </summary>
	[Fact]
	public async Task FailedMerge_RestoresTheLastAppliedState()
	{
		const string faultMarker = "HR_FAULT_AFTER_MERGE";
		var fixture = LocateFixtureExecutable();
		Assert.True(File.Exists(fixture), $"The WPF Hot Reload fixture must be built before this test; expected '{fixture}'.");
		Environment.SetEnvironmentVariable("OD_WPF_HOTRELOAD_AGENT", LocateAgentForTest());
		var adapter = new WpfApplicationHotReloadAdapter();
		var startInfo = new ProcessStartInfo(fixture) { UseShellExecute = false };
		if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.Arm64
			&& Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") {
			startInfo.Arguments = "\"" + Path.ChangeExtension(fixture, ".dll") + "\"";
			startInfo.FileName = Environment.ProcessPath!;
		}
		var session = await adapter.StartAsync(
			new HotReloadLaunchContext(project: null, withDebugger: false), startInfo, CancellationToken.None);
		await using (session) {
			startInfo.Environment["WPF_HOTRELOAD_TEST_FAULT_AFTER_MERGE"] = faultMarker;
			var pipeName = startInfo.Environment["WPF_HOTRELOAD_PIPE"];
			using var app = Process.Start(startInfo);
			Assert.NotNull(app);
			try {
				Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(60), CancellationToken.None),
					$"The agent never became ready (state {session.State}, exited {app.HasExited}).");
				var xamlPath = Path.Combine(RepositoryRoot(), "tests", "fixtures", "WpfHotReloadFixture", "SamplePane.xaml");
				var original = await File.ReadAllTextAsync(xamlPath);
				string Faulty(string text) => text.Replace("Text=\"Sample pane\"", "Text=\"Broken\"") + "<!-- " + faultMarker + " -->";
				Task<HotReloadApplyResult> Apply(string previous, string current, int version) => session.ApplyAsync(
					new HotReloadDocumentChange(xamlPath, previous, current, version, HotReloadChangeKind.FullDocument),
					CancellationToken.None);

				// Nothing applied yet: the rollback target is the text the application started from.
				var first = await Apply(original, Faulty(original), 1);
				Assert.False(first.IsSuccess, first.Message);
				Assert.Contains("restored the last applied state", first.Message);
				Assert.Equal("Sample pane", await QueryAgentAsync(pipeName, "PaneTitle.Text"));
				// A second failure before any complete apply, sent with the failed edit as "previous":
				// the restored launch text, not that edit, is what the agent must return to.
				var again = await Apply(Faulty(original), Faulty(original).Replace("Broken", "Broken again"), 2);
				Assert.False(again.IsSuccess, again.Message);
				Assert.Contains("restored the last applied state", again.Message);
				Assert.Equal("Sample pane", await QueryAgentAsync(pipeName, "PaneTitle.Text"));

				var good = original.Replace("Text=\"Sample pane\"", "Text=\"Hot reloaded\"");
				var applied = await Apply(original, good, 2);
				Assert.True(applied.IsSuccess, applied.Message);
				Assert.Equal("Hot reloaded", await QueryAgentAsync(pipeName, "PaneTitle.Text"));

				// After a successful apply, that text is the rollback target.
				var second = await Apply(good, Faulty(original), 3);
				Assert.False(second.IsSuccess, second.Message);
				Assert.Contains("restored the last applied state", second.Message);
				Assert.Equal("Hot reloaded", await QueryAgentAsync(pipeName, "PaneTitle.Text"));

				// Text only the XML fallback could apply ("ok ... | full apply skipped") is not a state to
				// roll back to: the next failure must still restore the last complete apply.
				// "{}" escapes a literal brace; the fallback must drop it like the XAML parser does.
				var partial = good.Replace("Text=\"Hot reloaded\"", "Text=\"{}{Partly applied}\"")
					.Replace("</UserControl>", "<nope:Missing xmlns:nope=\"clr-namespace:Nope.Missing\" /></UserControl>");
				Assert.NotEqual(good, partial);
				var fallback = await Apply(good, partial, 4);
				Assert.True(fallback.IsSuccess, fallback.Message);
				Assert.Contains("full apply", fallback.Message);
				Assert.Equal("{Partly applied}", await QueryAgentAsync(pipeName, "PaneTitle.Text"));
				var third = await Apply(partial, Faulty(original), 5);
				Assert.False(third.IsSuccess, third.Message);
				Assert.Contains("restored the last applied state", third.Message);
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
		// The fixture's apphost has the architecture of the machine that built it, and CI builds once on
		// Windows x64 for both Windows lanes; an ARM64 runner has no x64 runtime for that apphost. The
		// fixture assembly itself is portable, so run it with this test's own dotnet host there.
		if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.Arm64
			&& Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") {
			startInfo.Arguments = "\"" + Path.ChangeExtension(fixture, ".dll") + "\"";
			startInfo.FileName = Environment.ProcessPath!;
		}
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
			Assert.Equal("debug", startInfo.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"]);
			Assert.True(session.Capabilities.Supports(HotReloadChangeKind.CodeDelta));
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
				var invalidDelta = await SendPipeAsync(pipeName, new {
					kind = "applyDelta", mvid = "not-a-guid", metadata = Convert.ToBase64String(new byte[] { 1 }),
					il = Convert.ToBase64String(new byte[] { 2 }), pdb = Convert.ToBase64String(new byte[] { 3 }),
					updatedTypeTokens = new[] { 0x02000002 },
				});
				Assert.StartsWith("error:", invalidDelta.Result, StringComparison.OrdinalIgnoreCase);
				Assert.False(app.HasExited);

				// PaneTitle is declared in the self-contained WPF Hot Reload fixture.
				// The edit is only ever sent over the pipe - the sample on disk is never written to.
				var xamlPath = Path.Combine(RepositoryRoot(), "tests", "fixtures", "WpfHotReloadFixture",
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
		=> (await SendPipeAsync(pipeName, new { kind = "query", query })).Value;

	static async Task<(string Result, string Value)> SendPipeAsync(string pipeName, object request)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		Exception last = null;
		while (DateTime.UtcNow < deadline) {
			try {
				using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
				await pipe.ConnectAsync(2000);
				var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
				await writer.WriteLineAsync(JsonSerializer.Serialize(request));
				var reader = new StreamReader(pipe, new UTF8Encoding(false));
				var line = await reader.ReadLineAsync();
				if (string.IsNullOrWhiteSpace(line))
					throw new IOException("empty response");
				using var document = JsonDocument.Parse(line);
				var result = document.RootElement.TryGetProperty("result", out var resultElement) ? resultElement.GetString() : null;
				var value = document.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
					? valueElement.GetString() : null;
				return (result, value);
			} catch (Exception ex) when (ex is IOException || ex is TimeoutException) {
				last = ex;
				await Task.Delay(200);
			}
		}
		throw new TimeoutException("The agent did not answer the pipe request in time.", last);
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
