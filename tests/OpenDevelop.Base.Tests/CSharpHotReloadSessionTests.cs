using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Debugger.AddIn.HotReload;
using Debugger.AddIn.Service.Dap;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The C# debugger session has no agent pipe: readiness is the launch baseline, and every edit
/// is a Roslyn-produced code delta applied through the debugger. These tests pin the states the
/// shared launch/UI code relies on, without booting a debuggee.
/// </summary>
public sealed class CSharpHotReloadSessionTests
{
	static HotReloadCapabilities Capabilities() => new HotReloadCapabilities(
		HotReloadChangeDelivery.IdePushesEdits,
		requiresSavedFile: false,
		supportsUnsavedBuffer: true,
		supportedChanges: new[] { HotReloadChangeKind.CodeDelta, HotReloadChangeKind.RestartRequired },
		reportsStatePreservation: false);

	[Fact]
	public async Task StartAsync_OptsTheDebuggerIntoEditorAndContinue()
	{
		var adapter = new CSharpApplicationHotReloadAdapter();
		var startInfo = new System.Diagnostics.ProcessStartInfo("app");

		var session = await adapter.StartAsync(new HotReloadLaunchContext(null, withDebugger: true),
			startInfo, CancellationToken.None);

		Assert.Equal("1", startInfo.Environment["OD_HOTRELOAD_ENC"]);
		Assert.IsType<CSharpHotReloadSession>(session);
	}

	[Fact]
	public void Adapter_DoesNotClaimANonDebuggerLaunch()
	{
		var adapter = new CSharpApplicationHotReloadAdapter();

		Assert.False(adapter.CanHandle(new HotReloadLaunchContext(null, withDebugger: false), out var diagnostic));
		Assert.Contains("needs the SharpDbg debugger", diagnostic);
	}

	[Fact]
	public async Task AttachedSession_WithoutProjectOrModule_IsNotStarted()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());

		Assert.False(await HotReloadService.StartAttachedSessionAsync(session, null, null));
	}

	[Fact]
	public async Task BaselineCapture_IsWhatMakesTheSessionReady()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());
		Assert.Equal(HotReloadSessionState.Starting, session.State);

		var ready = session.WaitForReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
		session.NotifyBaselineCaptured(true, null);

		Assert.True(await ready);
		Assert.Equal(HotReloadSessionState.Ready, session.State);
	}

	[Fact]
	public async Task FailedBaseline_ReportsFailureInsteadOfPretendingToBeReady()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());
		session.NotifyBaselineCaptured(false, "no portable PDB");

		Assert.False(await session.WaitForReadyAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
		Assert.Equal(HotReloadSessionState.Failed, session.State);
		Assert.Contains("no portable PDB", session.GetDiagnostics()["lastError"]);
	}

	[Fact]
	public async Task ApplyWithoutRoslynProducer_IsNonBlocking()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());
		session.NotifyBaselineCaptured(true, null);

		var result = await session.ApplyPendingChangesAsync(CancellationToken.None);

		Assert.Equal(HotReloadOutcome.NoChanges, result.Outcome);
	}

	[Fact]
	public async Task FailedBaseline_DoesNotBlockResume()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());
		session.NotifyBaselineCaptured(false, "no portable PDB");

		var result = await session.ApplyPendingChangesAsync(CancellationToken.None);

		Assert.Equal(HotReloadOutcome.NoChanges, result.Outcome);
	}

	[Fact]
	public async Task FailedApply_LeavesTheSessionRetryable()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());
		var proxy = DispatchProxy.Create<IRoslynLanguageProtocol, ProtocolProxy>();
		((ProtocolProxy)(object)proxy).Delta = new HotReloadDeltaResponse(HotReloadDeltaStatus.Applied,
			new HotReloadModuleDelta(Guid.NewGuid(), new byte[] { 1 }, new byte[] { 2 }, Array.Empty<byte>(), new[] { 0x02000001 }), "ok");
		var applier = new RecordingApplier();
		session.ConfigureCodeDelta(proxy, new HotReloadProjectId("/tmp/Test.csproj", null), applier);
		session.NotifyBaselineCaptured(true, null);

		var first = await session.ApplyPendingChangesAsync(CancellationToken.None);
		var second = await session.ApplyPendingChangesAsync(CancellationToken.None);

		Assert.Equal(HotReloadOutcome.Failed, first.Outcome);
		Assert.Equal(HotReloadOutcome.Applied, second.Outcome);
		Assert.Equal(2, applier.Calls);
		Assert.Equal(HotReloadSessionState.Ready, session.State);
	}

	[Fact]
	public async Task RudeEdit_IsReportedAsRestartRequired()
	{
		var session = new CSharpHotReloadSession(new FakeAdapter(), Capabilities());

		var result = await session.ApplyAsync(
			new HotReloadDocumentChange("/tmp/Program.cs", "old", "new", 1, HotReloadChangeKind.RestartRequired),
			CancellationToken.None);

		Assert.Equal(HotReloadOutcome.RestartRequired, result.Outcome);
		Assert.Equal(HotReloadSessionState.RestartRequired, session.State);
	}

	class ProtocolProxy : DispatchProxy
	{
		public HotReloadDeltaResponse Delta { get; set; }

		protected override object Invoke(MethodInfo targetMethod, object[] args)
		{
			if (targetMethod.Name == nameof(IRoslynLanguageProtocol.RoslynHotReloadProduceDeltaAsync))
				return Task.FromResult(Delta);
			throw new NotSupportedException(targetMethod.Name);
		}
	}

	sealed class RecordingApplier : IHotReloadDeltaApplier
	{
		public int Calls { get; private set; }

		public Task<HotReloadApplyResult> ApplyAsync(HotReloadModuleDelta delta, CancellationToken token)
		{
			Calls++;
			return Task.FromResult(new HotReloadApplyResult(
				Calls == 1 ? HotReloadOutcome.Failed : HotReloadOutcome.Applied,
				Calls == 1 ? "rejected" : "applied"));
		}
	}

	sealed class FakeAdapter : IApplicationHotReloadAdapter
	{
		public string Framework => "C#";

		public bool CanHandle(HotReloadLaunchContext context, out string diagnostic)
		{
			diagnostic = null;
			return true;
		}

		public HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context) => Capabilities();

		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context,
			System.Diagnostics.ProcessStartInfo startInfo, CancellationToken token)
			=> Task.FromResult<IHotReloadSession>(new CSharpHotReloadSession(this, Capabilities()));
	}
}
