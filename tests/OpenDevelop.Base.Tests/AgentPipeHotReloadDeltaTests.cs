using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.WpfDesign.AddIn.HotReload;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class AgentPipeHotReloadDeltaTests
{
	[Fact]
	public async Task RestartRequired_DoesNotWaitForOrContactTheAgent()
	{
		await using var session = new AgentPipeHotReloadSession(new Adapter(),
			new HotReloadCapabilities(HotReloadChangeDelivery.IdePushesEdits, false, true,
			Array.Empty<HotReloadChangeKind>(), false), "no-agent-" + Guid.NewGuid().ToString("N"), null);

		var result = await session.ApplyAsync(new HotReloadDocumentChange("/tmp/View.xaml", "old", "new", 1,
			HotReloadChangeKind.RestartRequired), CancellationToken.None);

		Assert.Equal(HotReloadOutcome.RestartRequired, result.Outcome);
	}

	[Fact]
	public async Task CodeDelta_UsesDebuggerRouteWithoutWaitingForTheAgent()
	{
		var applier = new RecordingApplier();
		var mvid = Guid.NewGuid();
		var protocol = DispatchProxy.Create<IRoslynLanguageProtocol, DeltaProtocol>();
		((DeltaProtocol)(object)protocol).Delta = new HotReloadModuleDelta(mvid, new byte[] { 1 }, new byte[] { 2 }, null, Array.Empty<int>());
		var adapter = new WpfApplicationHotReloadAdapter();
		var capabilities = adapter.GetCapabilities(new HotReloadLaunchContext(project: null, withDebugger: true));
		Assert.True(capabilities.Supports(HotReloadChangeKind.CodeDelta));
		await using var session = new AgentPipeHotReloadSession(adapter, capabilities,
			"no-agent-" + Guid.NewGuid().ToString("N"), null, protocol,
			new HotReloadProjectId("/tmp/Test.csproj", "net11.0"));
		session.ConfigureCodeDelta(protocol, new HotReloadProjectId("/tmp/Test.csproj", "net11.0"), applier);

		var result = await session.ApplyAsync(new HotReloadDocumentChange("/tmp/Test.cs", "old", "new", 1,
			HotReloadChangeKind.CodeDelta), CancellationToken.None);

		Assert.Equal(HotReloadOutcome.Applied, result.Outcome);
		Assert.Same(((DeltaProtocol)(object)protocol).Delta, applier.Delta);
	}

	/// <summary>
	/// After a debugger-applied delta the session asks the agent to run the update handlers. In a paused
	/// debuggee the connection is accepted (by the OS) but nothing ever answers: the apply must still
	/// finish, as Applied with a note, within the request's own timeout instead of hanging in Applying.
	/// </summary>
	[Fact]
	public async Task CodeDelta_UnderAPausedDebuggee_FinishesWhenTheAgentNeverAnswers()
	{
		var pipeName = "paused-agent-" + Guid.NewGuid().ToString("N").Substring(0, 8);
		using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var accepted = new TaskCompletionSource();
		var server = Task.Run(async () => {
			using var pipe = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
				System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
			await pipe.WaitForConnectionAsync(serverCts.Token);
			accepted.TrySetResult();
			try { await Task.Delay(Timeout.Infinite, serverCts.Token); } catch (OperationCanceledException) { }
		});
		var protocol = DispatchProxy.Create<IRoslynLanguageProtocol, DeltaProtocol>();
		((DeltaProtocol)(object)protocol).Delta = new HotReloadModuleDelta(Guid.NewGuid(), new byte[] { 1 }, new byte[] { 2 }, null, new[] { 0x02000002 });
		var adapter = new WpfApplicationHotReloadAdapter();
		var capabilities = adapter.GetCapabilities(new HotReloadLaunchContext(project: null, withDebugger: true));
		await using var session = new AgentPipeHotReloadSession(adapter, capabilities, pipeName, null, protocol,
			new HotReloadProjectId("/tmp/Test.csproj", "net11.0"));
		session.ConfigureCodeDelta(protocol, new HotReloadProjectId("/tmp/Test.csproj", "net11.0"), new RecordingApplier());

		var watch = Stopwatch.StartNew();
		var result = await session.ApplyAsync(new HotReloadDocumentChange("/tmp/Test.cs", "old", "new", 1,
			HotReloadChangeKind.CodeDelta), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45));
		watch.Stop();
		serverCts.Cancel();
		await server;

		Assert.True(accepted.Task.IsCompleted, "The agent pipe was never connected; the test did not reach the read.");
		Assert.Equal(HotReloadOutcome.Applied, result.Outcome);
		Assert.Contains("refreshes once the application runs again", result.Message);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Took " + watch.Elapsed);
		Assert.NotEqual(HotReloadSessionState.Applying, session.State);
	}

	/// <summary>
	/// Two Applies in flight at once: the second must not ask for its delta until the first one's outcome is
	/// known, and must report it as applied, or the host computes it against the wrong generation.
	/// </summary>
	[Fact]
	public async Task OverlappingCodeDeltaApplies_RunOneAtATimeAndChainTheGeneration()
	{
		var protocol = DispatchProxy.Create<IRoslynLanguageProtocol, RecordingDeltaProtocol>();
		var recorder = (RecordingDeltaProtocol)(object)protocol;
		var applier = new SlowApplier();
		var adapter = new WpfApplicationHotReloadAdapter();
		var capabilities = adapter.GetCapabilities(new HotReloadLaunchContext(project: null, withDebugger: true));
		var project = new HotReloadProjectId("/tmp/Test.csproj", "net11.0");
		// No agent pipe: runUpdateHandlers times out, keeping each apply in flight even longer.
		await using var session = new AgentPipeHotReloadSession(adapter, capabilities,
			"no-agent-" + Guid.NewGuid().ToString("N"), null, protocol, project);
		session.ConfigureCodeDelta(protocol, project, applier);

		var first = session.ApplyAsync(new HotReloadDocumentChange("/tmp/Test.cs", "a", "b", 1, HotReloadChangeKind.CodeDelta), CancellationToken.None);
		var second = session.ApplyAsync(new HotReloadDocumentChange("/tmp/Test.cs", "b", "c", 2, HotReloadChangeKind.CodeDelta), CancellationToken.None);
		var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));

		Assert.All(results, result => Assert.Equal(HotReloadOutcome.Applied, result.Outcome));
		Assert.Equal(new[] { false, true }, recorder.PreviousDeltaApplied);
		Assert.Equal(1, applier.MaxConcurrency);
	}

	class RecordingDeltaProtocol : DispatchProxy
	{
		public List<bool> PreviousDeltaApplied { get; } = new();
		protected override object? Invoke(MethodInfo? method, object?[]? arguments)
		{
			if (method?.Name != nameof(IRoslynLanguageProtocol.RoslynHotReloadProduceDeltaAsync))
				throw new NotSupportedException(method?.Name);
			lock (PreviousDeltaApplied)
				PreviousDeltaApplied.Add(((HotReloadDeltaRequest)arguments![0]!).PreviousDeltaApplied);
			return Task.FromResult(new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied,
				new HotReloadModuleDelta(Guid.NewGuid(), new byte[] { 1 }, new byte[] { 2 }, null, new[] { 0x02000002 }), "ok"));
		}
	}

	sealed class SlowApplier : IHotReloadDeltaApplier
	{
		int current;
		public int MaxConcurrency { get; private set; }
		public async Task<HotReloadApplyResult> ApplyAsync(HotReloadModuleDelta delta, CancellationToken token)
		{
			var now = Interlocked.Increment(ref current);
			MaxConcurrency = Math.Max(MaxConcurrency, now);
			await Task.Delay(300, token);
			Interlocked.Decrement(ref current);
			return new HotReloadApplyResult(HotReloadOutcome.Applied, "debugger applied");
		}
	}

	class DeltaProtocol : DispatchProxy
	{
		public HotReloadModuleDelta Delta { get; set; } = null!;
		protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
			method?.Name == nameof(IRoslynLanguageProtocol.RoslynHotReloadProduceDeltaAsync)
				? Task.FromResult(new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied, Delta, "ok"))
				: throw new NotSupportedException(method?.Name);
	}

	sealed class RecordingApplier : IHotReloadDeltaApplier
	{
		public HotReloadModuleDelta? Delta { get; private set; }
		public Task<HotReloadApplyResult> ApplyAsync(HotReloadModuleDelta delta, CancellationToken token)
		{
			Delta = delta;
			return Task.FromResult(new HotReloadApplyResult(HotReloadOutcome.Applied, "debugger applied"));
		}
	}

	sealed class Adapter : IApplicationHotReloadAdapter
	{
		public string Framework => "WPF";
		public HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context) => throw new NotSupportedException();
		public bool CanHandle(HotReloadLaunchContext context, out string reason) { reason = null!; return true; }
		public bool CanHandle(ProcessStartInfo startInfo) => true;
		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo, CancellationToken token) => throw new NotSupportedException();
	}
}
