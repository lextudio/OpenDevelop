using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.HotReload;

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

	static HotReloadCapabilities IdeDriven() => new HotReloadCapabilities(
		HotReloadChangeDelivery.IdePushesEdits, false, true,
		new[] { HotReloadChangeKind.FullDocument }, false);

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
