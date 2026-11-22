using System;
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
