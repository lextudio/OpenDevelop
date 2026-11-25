using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Covers the parts of the contract that every adapter has to honour, using fakes rather than a
/// real framework. The point is that the workbench never has to know which framework it has: an
/// adapter that only watches files must be expressible without the service special-casing it.
/// </summary>
public sealed class HotReloadServiceTests : IDisposable
{
	public void Dispose() => HotReloadService.SetAdaptersForTesting(Array.Empty<IApplicationHotReloadAdapter>());

	[Fact]
	public void FindAdapter_WithoutStartupProject_ExplainsWhyRatherThanThrowing()
	{
		HotReloadService.SetAdaptersForTesting(new[] { new FakeAdapter("WPF", canHandle: true) });

		var adapter = HotReloadService.FindAdapter(null!, out var diagnostic);

		Assert.Null(adapter);
		Assert.Equal("No startup project is selected.", diagnostic);
	}

	[Fact]
	public void TryConfigureLaunch_WithNoAdapters_LeavesANormalLaunchUntouched()
	{
		HotReloadService.SetAdaptersForTesting(Array.Empty<IApplicationHotReloadAdapter>());
		var startInfo = new ProcessStartInfo("app");

		var configured = HotReloadService.TryConfigureLaunch(null!, startInfo, withDebugger: false);

		Assert.False(configured);
		Assert.Empty(startInfo.Environment.Keys.Where(k => k.StartsWith("DOTNET_STARTUP_HOOKS")));
		Assert.Null(HotReloadService.CurrentSession);
	}

	[Fact]
	public async Task FrameworkDrivenAdapter_RefusesToApply_InsteadOfReportingAFakeSuccess()
	{
		// The whole reason Delivery exists: an adapter whose framework watches the filesystem
		// never transmits an edit, so "I applied it" would be a lie. Saving is the trigger.
		var capabilities = new HotReloadCapabilities(
			HotReloadChangeDelivery.FrameworkWatchesFiles,
			requiresSavedFile: true,
			supportsUnsavedBuffer: false,
			supportedChanges: Array.Empty<HotReloadChangeKind>(),
			reportsStatePreservation: false);
		var session = new FakeSession("Uno", capabilities);

		Assert.False(capabilities.CanApplyFromIde);
		var result = await session.ApplyAsync(
			new HotReloadDocumentChange("/tmp/MainPage.xaml", "old", "new", 1, HotReloadChangeKind.Property),
			CancellationToken.None);

		Assert.Equal(HotReloadOutcome.Unsupported, result.Outcome);
		Assert.False(result.IsSuccess);
	}

	[Fact]
	public void IdeDrivenAdapter_CanApplyAnUnsavedBuffer()
	{
		var capabilities = new HotReloadCapabilities(
			HotReloadChangeDelivery.IdePushesEdits,
			requiresSavedFile: false,
			supportsUnsavedBuffer: true,
			supportedChanges: new[] { HotReloadChangeKind.Property, HotReloadChangeKind.FullDocument },
			reportsStatePreservation: false);

		Assert.True(capabilities.CanApplyFromIde);
		Assert.True(capabilities.SupportsUnsavedBuffer);
		Assert.False(capabilities.RequiresSavedFile);
		Assert.True(capabilities.Supports(HotReloadChangeKind.Property));
		Assert.False(capabilities.Supports(HotReloadChangeKind.CodeDelta));
	}

	[Fact]
	public void ApplyResult_TreatsOnlyAppliedAndDegradedAsSuccess()
	{
		// Degraded means it really was applied, just without preserving state - so it is success.
		Assert.True(new HotReloadApplyResult(HotReloadOutcome.Applied, "ok").IsSuccess);
		Assert.True(new HotReloadApplyResult(HotReloadOutcome.Degraded, "reloaded").IsSuccess);
		// Everything else must not read as success, especially "we sent it".
		Assert.False(new HotReloadApplyResult(HotReloadOutcome.RestartRequired, "x:Class changed").IsSuccess);
		Assert.False(new HotReloadApplyResult(HotReloadOutcome.Failed, "rejected").IsSuccess);
		Assert.False(new HotReloadApplyResult(HotReloadOutcome.Unsupported, "not supported").IsSuccess);
		Assert.False(new HotReloadApplyResult(HotReloadOutcome.Disconnected, "gone").IsSuccess);
		Assert.False(new HotReloadApplyResult(HotReloadOutcome.NoChanges, "nothing to do").IsSuccess);
	}

	[Fact]
	public async Task Session_ReportsReadinessRatherThanLettingCallersAssumeIt()
	{
		// Measured on Uno: a started process is not a ready one, so Ready is a reported state.
		var session = new FakeSession("WPF", IdeDriven());
		Assert.Equal(HotReloadSessionState.Starting, session.State);

		var states = new List<HotReloadSessionState>();
		session.StateChanged += (_, s) => states.Add(s);

		Assert.True(await session.WaitForReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
		Assert.Equal(HotReloadSessionState.Ready, session.State);
		Assert.Contains(HotReloadSessionState.Ready, states);
	}

	[Fact]
	public void ApplyAction_IsSaveForAFrameworkThatWatchesFiles_AndApplyOtherwise()
	{
		// The UI must not offer an action the adapter cannot perform: for a framework-driven
		// adapter the only gesture that does anything is Save.
		var watching = new FakeSession("Uno", new HotReloadCapabilities(
			HotReloadChangeDelivery.FrameworkWatchesFiles, true, false,
			Array.Empty<HotReloadChangeKind>(), false));
		Assert.Equal(HotReloadApplyAction.Save, HotReloadWorkflow.DecideApplyAction(watching));
		Assert.True(HotReloadWorkflow.MustSaveBeforeApply(watching));

		var pushing = new FakeSession("WPF", IdeDriven());
		Assert.Equal(HotReloadApplyAction.Apply, HotReloadWorkflow.DecideApplyAction(pushing));
		Assert.False(HotReloadWorkflow.MustSaveBeforeApply(pushing));
	}

	[Fact]
	public async Task ApplyAction_IsNoneWithoutAUsableSession()
	{
		Assert.Equal(HotReloadApplyAction.None, HotReloadWorkflow.DecideApplyAction(null));

		var session = new FakeSession("WPF", IdeDriven());
		await session.DisposeAsync();
		Assert.Equal(HotReloadSessionState.Stopped, session.State);
		Assert.Equal(HotReloadApplyAction.None, HotReloadWorkflow.DecideApplyAction(session));
	}

	[Theory]
	// Same number of elements, different attribute text: a property edit.
	[InlineData("<Grid><TextBlock Text=\"a\" /></Grid>", "<Grid><TextBlock Text=\"b\" /></Grid>",
		HotReloadChangeKind.Property)]
	// An element appeared, so nothing narrower than a subtree replacement is safe.
	[InlineData("<Grid><TextBlock /></Grid>", "<Grid><TextBlock /><Button /></Grid>",
		HotReloadChangeKind.Subtree)]
	// No baseline to diff against: fall back to the whole document rather than guess.
	[InlineData(null, "<Grid />", HotReloadChangeKind.FullDocument)]
	public void Classify_PrefersTheBroaderUpdateWhenUnsure(string previous, string current,
		HotReloadChangeKind expected)
	{
		Assert.Equal(expected, HotReloadWorkflow.Classify(previous, current));
	}

	[Theory]
	[InlineData(HotReloadChangeKind.CodeDelta, HotReloadOutcome.BuildRequired)]
	[InlineData(HotReloadChangeKind.RestartRequired, HotReloadOutcome.RestartRequired)]
	public async Task AgentSession_AnswersEditsATextPushCannotCarry_WithoutTouchingThePipe(
		HotReloadChangeKind kind, HotReloadOutcome expected)
	{
		// No agent listens on this pipe: an answer proves nothing was sent to it.
		var session = new AgentPipeHotReloadSession(new FakeAdapter("WPF", true), IdeDriven(),
			"odhr-test-" + Guid.NewGuid().ToString("N").Substring(0, 8), null);

		var result = await session.ApplyAsync(new HotReloadDocumentChange("/tmp/A.xaml", "old", "new", 1, kind),
			CancellationToken.None);

		Assert.Equal(expected, result.Outcome);
		Assert.False(result.IsSuccess);
	}

	[Fact]
	public async Task AgentSession_CodeDelta_ProducesAndSendsTheExactAgentPayload()
	{
		var pipeName = "odhr-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
		var mvid = Guid.NewGuid();
		var protocol = DeltaProtocol.Create(mvid);
		using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		JsonDocument? deltaRequest = null;
		var server = Task.Run(async () => {
			for (var requestNumber = 0; requestNumber != 2; requestNumber++) {
				using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
				await pipe.WaitForConnectionAsync(serverCts.Token);
				using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
				using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
				var line = await reader.ReadLineAsync(serverCts.Token);
				using var request = JsonDocument.Parse(line!);
				if (requestNumber == 0) {
					Assert.Equal("query", request.RootElement.GetProperty("kind").GetString());
					await writer.WriteLineAsync("{\"result\":\"ok\",\"value\":\"1\"}");
				} else {
					deltaRequest = JsonDocument.Parse(request.RootElement.GetRawText());
					await writer.WriteLineAsync("{\"result\":\"ok: delta applied\"}");
				}
			}
		});
		var capabilities = new HotReloadCapabilities(HotReloadChangeDelivery.IdePushesEdits, false, true,
			new[] { HotReloadChangeKind.CodeDelta }, false);
		await using var session = new AgentPipeHotReloadSession(new FakeAdapter("WPF", true), capabilities,
			pipeName, null, protocol, new HotReloadProjectId("/tmp/Delta.csproj", "net11.0"));

		var result = await session.ApplyAsync(new HotReloadDocumentChange("/tmp/Delta.cs", "old", "new", 1,
			HotReloadChangeKind.CodeDelta), CancellationToken.None);
		await server;

		Assert.Equal(HotReloadOutcome.Applied, result.Outcome);
		Assert.NotNull(deltaRequest);
		Assert.Equal("applyDelta", deltaRequest!.RootElement.GetProperty("kind").GetString());
		Assert.Equal(mvid.ToString("D"), deltaRequest.RootElement.GetProperty("mvid").GetString());
		Assert.Equal(new byte[] { 1, 2 }, deltaRequest.RootElement.GetProperty("metadata").GetBytesFromBase64());
		Assert.Equal(new byte[] { 3 }, deltaRequest.RootElement.GetProperty("il").GetBytesFromBase64());
		Assert.Equal(new byte[] { 4, 5 }, deltaRequest.RootElement.GetProperty("pdb").GetBytesFromBase64());
		Assert.Equal(new[] { 0x02000002 }, deltaRequest.RootElement.GetProperty("updatedTypeTokens").EnumerateArray().Select(v => v.GetInt32()));
		deltaRequest.Dispose();
	}

	[Theory]
	// A C# expression changed: Route T cannot express it.
	[InlineData("<Button Content=\"{= a + 1}\" />", "<Button Content=\"{= a + 2}\" />",
		HotReloadChangeKind.CodeDelta)]
	// A new x:Name is a new field.
	[InlineData("<Grid><Button /></Grid>", "<Grid><Button x:Name=\"Ok\" /></Grid>",
		HotReloadChangeKind.CodeDelta)]
	// Removing an x:Name is a rude edit; the Name form counts too.
	[InlineData("<Grid><Button Name=\"Ok\" /></Grid>", "<Grid><Button /></Grid>",
		HotReloadChangeKind.RestartRequired)]
	[InlineData("<Window x:Class=\"A.B\"><Grid /></Window>", "<Window x:Class=\"A.C\"><Grid /></Window>",
		HotReloadChangeKind.RestartRequired)]
	// Declared namespaces read the same as Simpler XAML's undeclared ones.
	[InlineData("<Window xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"A.B\"><Button x:Name=\"Ok\" /></Window>",
		"<Window xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"A.B\"><Button /></Window>",
		HotReloadChangeKind.RestartRequired)]
	// The generator emits a field for a name inside a template too, so removing it is a restart.
	[InlineData("<Window><Window.Resources><ControlTemplate><Border x:Name=\"Part\" /></ControlTemplate></Window.Resources></Window>",
		"<Window><Window.Resources><ControlTemplate><Border /></ControlTemplate></Window.Resources></Window>",
		HotReloadChangeKind.RestartRequired)]
	// Any prefix bound to the XAML namespace counts, declared on the same element or earlier.
	[InlineData("<Window xmlns:q=\"http://schemas.microsoft.com/winfx/2006/xaml\" q:Class=\"A.B\"><Button q:Name=\"Ok\" /></Window>",
		"<Window xmlns:q=\"http://schemas.microsoft.com/winfx/2006/xaml\" q:Class=\"A.B\"><Button /></Window>",
		HotReloadChangeKind.RestartRequired)]
	// Half-typed XAML does not parse, so it takes the old tag-count path rather than being read as
	// a removed x:Name.
	[InlineData("<Grid><Button x:Name=\"Ok\" /></Grid>", "<Grid><Button ",
		HotReloadChangeKind.Property)]
	// An event handler is generated code (+= in InitializeComponent), so changing, adding or removing
	// one is a rebuild. Recognised by name and value shape: a text-only diff cannot resolve the type.
	[InlineData("<Button Click=\"OnA\" />", "<Button Click=\"OnB\" />", HotReloadChangeKind.CodeDelta)]
	[InlineData("<Button />", "<Button Click=\"OnA\" />", HotReloadChangeKind.CodeDelta)]
	[InlineData("<Button Click=\"OnA\" />", "<Button />", HotReloadChangeKind.CodeDelta)]
	[InlineData("<Button PreviewMouseDown=\"OnA\" />", "<Button PreviewMouseDown=\"OnB\" />", HotReloadChangeKind.CodeDelta)]
	[InlineData("<ListBox SelectionChanged=\"OnA\" />", "<ListBox SelectionChanged=\"OnB\" />", HotReloadChangeKind.CodeDelta)]
	// Attached events carry the owner's name before the dot.
	[InlineData("<Button Mouse.MouseEnter=\"OnA\" />", "<Button Mouse.MouseEnter=\"OnB\" />", HotReloadChangeKind.CodeDelta)]
	// Not events: a property whose value is a bare word, and a binding on an event-looking name.
	[InlineData("<Button Content=\"Click\" />", "<Button Content=\"Tap\" />", HotReloadChangeKind.Property)]
	[InlineData("<Button Foreground=\"Red\" />", "<Button Foreground=\"Blue\" />", HotReloadChangeKind.Property)]
	[InlineData("<Button Click=\"{Binding A}\" />", "<Button Click=\"{Binding B}\" />", HotReloadChangeKind.Property)]
	// Simpler XAML has no xmlns declarations at all.
	[InlineData("<Grid><TextBlock Text=\"a\" /></Grid>", "<Grid><TextBlock Text=\"b\" /></Grid>",
		HotReloadChangeKind.Property)]
	public void Classify_ForGeneratedXaml_SeparatesEditsATextPushCannotCarry(string previous, string current,
		HotReloadChangeKind expected)
	{
		Assert.Equal(expected, HotReloadWorkflow.Classify(previous, current, generatesXamlCode: true));
	}

	[Theory]
	[InlineData("<Grid><Button x:Name=\"Ok\" /></Grid>", "<Grid><Button /></Grid>")]
	[InlineData("<Grid><Button Name=\"Ok\" /></Grid>", "<Grid><Button /></Grid>")]
	[InlineData("<Window x:Class=\"A.B\" />", "<Window x:Class=\"A.C\" />")]
	[InlineData("<Button Content=\"{= a + 1}\" />", "<Button Content=\"{= a + 2}\" />")]
	public void Classify_WithoutGeneratedXaml_KeepsTheTextPath(string previous, string current)
	{
		// A re-parse carries these under BAML and WinUI, so reporting them as structural would block a
		// reload that works.
		var kind = HotReloadWorkflow.Classify(previous, current);
		Assert.NotEqual(HotReloadChangeKind.RestartRequired, kind);
		Assert.NotEqual(HotReloadChangeKind.CodeDelta, kind);
	}

	[Fact]
	public void Classify_CustomEventName_IsAnEventWhenTheCodeBehindDeclaresTheHandler()
	{
		// "Frobbed" matches no event-name pattern; only the code-behind says OnFrobA/OnFrobB are handlers.
		const string previous = "<my:Widget Frobbed=\"OnFrobA\" />";
		const string current = "<my:Widget Frobbed=\"OnFrobB\" />";
		var handlers = HotReloadWorkflow.HandlerNamesIn(
			"partial class View { void OnFrobA(object s, EventArgs e) { }\n private async void OnFrobB (object s, EventArgs e) { } }");
		Assert.Equal(HotReloadChangeKind.Property, HotReloadWorkflow.Classify(previous, current, generatesXamlCode: true));
		Assert.Equal(HotReloadChangeKind.CodeDelta, HotReloadWorkflow.Classify(previous, current, generatesXamlCode: true, handlers));
		// A property value that names no method stays a property edit.
		Assert.Equal(HotReloadChangeKind.Property,
			HotReloadWorkflow.Classify("<my:Widget Mode=\"Fast\" />", "<my:Widget Mode=\"Slow\" />", generatesXamlCode: true, handlers));
		Assert.Empty(HotReloadWorkflow.HandlerNamesIn(null));
	}

	[Fact]
	public void UsesGeneratedXaml_PrefersTheEvaluatedProperty()
	{
		// The evaluated value is what the build used: Directory.Build.props, imported targets and the
		// environment switch already applied. A project file without the property still opts in.
		var dir = Path.Combine(Path.GetTempPath(), "od-xsg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
		Directory.CreateDirectory(dir);
		try {
			var plain = Path.Combine(dir, "Plain.csproj");
			var on = Path.Combine(dir, "On.csproj");
			File.WriteAllText(plain, "<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");
			File.WriteAllText(on, "<Project><PropertyGroup><UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator></PropertyGroup></Project>");
			Func<string, string> noEnvironment = _ => null;
			Func<string, string> Evaluated(string value) => name => name == "UseLibreWpfXamlSourceGenerator" ? value : null;

			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(plain, Evaluated("true"), noEnvironment));
			// The build said no, so a stray environment switch or a file value the build overrode does not win.
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(plain, Evaluated("false"), n => n == "LIBREWPF_USE_XSG" ? "1" : null));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(on, Evaluated("false"), noEnvironment));
			// No evaluated value (not an MSBuild project, property undefined, evaluation failed): the file and
			// the environment decide, as before.
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(on, Evaluated(""), noEnvironment));
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(on, _ => throw new InvalidOperationException("evaluation failed"), noEnvironment));
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(on, null, noEnvironment));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(plain, Evaluated(null), noEnvironment));
		} finally {
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void UsesGeneratedXaml_ReadsTheProjectFileProperty()
	{
		var dir = Path.Combine(Path.GetTempPath(), "od-xsg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
		Directory.CreateDirectory(dir);
		try {
			var on = Path.Combine(dir, "On.csproj");
			var off = Path.Combine(dir, "Off.csproj");
			File.WriteAllText(on, "<Project><PropertyGroup><UseLibreWpfXamlSourceGenerator>true</UseLibreWpfXamlSourceGenerator></PropertyGroup></Project>");
			File.WriteAllText(off, "<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");
			Func<string, string> noEnvironment = _ => null;
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(on, noEnvironment));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(off, noEnvironment));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(Path.Combine(dir, "Missing.csproj"), noEnvironment));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(null, noEnvironment));

			// The environment switch counts only when it is truly on: "0" must not turn it on.
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(off, n => n == "LIBREWPF_USE_XSG" ? "1" : null));
			Assert.True(HotReloadWorkflow.UsesGeneratedXaml(off, n => n == "LIBREWPF_USE_XAML_SOURCE_GENERATOR" ? "true" : null));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(off, n => n == "LIBREWPF_USE_XSG" ? "0" : null));
			Assert.False(HotReloadWorkflow.UsesGeneratedXaml(off, n => n == "LIBREWPF_USE_XSG" ? "false" : null));
		} finally {
			Directory.Delete(dir, true);
		}
	}

	static HotReloadCapabilities IdeDriven() => new HotReloadCapabilities(
		HotReloadChangeDelivery.IdePushesEdits, false, true,
		new[] { HotReloadChangeKind.FullDocument }, false);

	class DeltaProtocol : DispatchProxy
	{
		Guid mvid;
		public static IRoslynLanguageProtocol Create(Guid mvid)
		{
			var protocol = DispatchProxy.Create<IRoslynLanguageProtocol, DeltaProtocol>();
			((DeltaProtocol)(object)protocol).mvid = mvid;
			return protocol;
		}
		protected override object? Invoke(MethodInfo? method, object?[]? arguments)
		{
			if (method?.Name == nameof(IRoslynLanguageProtocol.RoslynHotReloadProduceDeltaAsync))
				return Task.FromResult(new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied,
					new HotReloadModuleDelta(mvid, new byte[] { 1, 2 }, new byte[] { 3 }, new byte[] { 4, 5 }, new[] { 0x02000002 }), "ok"));
			throw new NotSupportedException(method?.Name);
		}
	}

	sealed class FakeAdapter : IApplicationHotReloadAdapter
	{
		readonly bool canHandle;

		public FakeAdapter(string framework, bool canHandle)
		{
			Framework = framework;
			this.canHandle = canHandle;
		}

		public string Framework { get; }

		public bool CanHandle(HotReloadLaunchContext context, out string diagnostic)
		{
			diagnostic = canHandle ? null : "fake adapter declined";
			return canHandle;
		}

		public HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context) => IdeDriven();

		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
			CancellationToken token)
			=> Task.FromResult<IHotReloadSession>(new FakeSession(Framework, IdeDriven()));
	}

	sealed class FakeSession : IHotReloadSession
	{
		HotReloadSessionState state = HotReloadSessionState.Starting;

		public FakeSession(string framework, HotReloadCapabilities capabilities)
		{
			Framework = framework;
			Capabilities = capabilities;
		}

		public string Framework { get; }

		public HotReloadCapabilities Capabilities { get; }

		public int? ProcessId => null;

		public HotReloadSessionState State {
			get => state;
			private set {
				if (state == value)
					return;
				state = value;
				StateChanged?.Invoke(this, value);
			}
		}

		public event EventHandler<HotReloadSessionState>? StateChanged;

		public IReadOnlyDictionary<string, string> GetDiagnostics()
			=> new Dictionary<string, string> { ["state"] = State.ToString() };

		public Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken token)
		{
			State = HotReloadSessionState.Ready;
			return Task.FromResult(true);
		}

		public Task<HotReloadApplyResult> ApplyAsync(HotReloadDocumentChange change, CancellationToken token)
		{
			if (!Capabilities.CanApplyFromIde) {
				return Task.FromResult(HotReloadApplyResult.Unsupported(
					"This framework watches the files itself; save the document to trigger a reload."));
			}
			return Task.FromResult(new HotReloadApplyResult(HotReloadOutcome.Applied, "ok"));
		}

		public ValueTask DisposeAsync()
		{
			State = HotReloadSessionState.Stopped;
			return default;
		}
	}
}
