using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>
	/// Who detects a change and who applies it. Adapters differ in shape, not just in transport,
	/// and pretending otherwise produces an adapter that has to lie about what it did.
	/// </summary>
	public enum HotReloadChangeDelivery
	{
		/// <summary>
		/// The IDE watches the editor, classifies the edit and pushes it to an agent that applies
		/// it. Unsaved buffer text can be applied. Example: the WPF/LibreWPF in-process agent.
		/// </summary>
		IdePushesEdits,

		/// <summary>
		/// The framework's own server watches the filesystem and applies the change itself. The
		/// IDE only configures the launch and observes status; it never transmits an edit, so
		/// saving the document is the only way to trigger one. Example: Uno's DevServer.
		/// </summary>
		FrameworkWatchesFiles,
	}

	/// <summary>
	/// The one vocabulary every adapter must express identically, because this is what the status
	/// UI binds to and what a test can wait on.
	/// </summary>
	public enum HotReloadSessionState
	{
		NotStarted,
		Starting,
		Connecting,
		/// <summary>
		/// The framework can accept a change *now*. This is always reported by the adapter and
		/// never inferred from a started process: Uno's DevServer, for one, accepts connections
		/// long before its workspace is loaded, and an edit made in that window is silently lost.
		/// </summary>
		Ready,
		Applying,
		Applied,
		/// <summary>Applied, but live state was not preserved.</summary>
		Degraded,
		RestartRequired,
		Failed,
		Unsupported,
		Disconnected,
		Stopped,
	}

	/// <summary>
	/// A hint about the smallest safe update, not a promise that any runtime can apply it. Only
	/// meaningful for <see cref="HotReloadChangeDelivery.IdePushesEdits"/>: when the framework
	/// watches the files itself, OpenDevelop never sees the change request.
	/// </summary>
	public enum HotReloadChangeKind
	{
		Property,
		Subtree,
		Resource,
		FullDocument,
		CodeDelta,
		RestartRequired,
	}

	public enum HotReloadOutcome
	{
		Applied,
		Degraded,
		RestartRequired,
		BuildRequired,
		Unsupported,
		Failed,
		Disconnected,
	}

	/// <summary>What an adapter can actually do, so the UI never offers an action it cannot perform.</summary>
	public sealed class HotReloadCapabilities
	{
		public HotReloadCapabilities(
			HotReloadChangeDelivery delivery,
			bool requiresSavedFile,
			bool supportsUnsavedBuffer,
			IEnumerable<HotReloadChangeKind> supportedChanges,
			bool reportsStatePreservation)
		{
			Delivery = delivery;
			RequiresSavedFile = requiresSavedFile;
			SupportsUnsavedBuffer = supportsUnsavedBuffer;
			SupportedChanges = supportedChanges?.ToImmutableArray() ?? ImmutableArray<HotReloadChangeKind>.Empty;
			ReportsStatePreservation = reportsStatePreservation;
		}

		public HotReloadChangeDelivery Delivery { get; }

		/// <summary>The change only reaches the framework once the document is on disk.</summary>
		public bool RequiresSavedFile { get; }

		public bool SupportsUnsavedBuffer { get; }

		public ImmutableArray<HotReloadChangeKind> SupportedChanges { get; }

		public bool ReportsStatePreservation { get; }

		/// <summary>
		/// True when the workbench can offer a real Apply. For a framework-driven adapter the
		/// equivalent user gesture is Save, and the UI presents it that way.
		/// </summary>
		public bool CanApplyFromIde => Delivery == HotReloadChangeDelivery.IdePushesEdits;

		public bool Supports(HotReloadChangeKind kind) => SupportedChanges.Contains(kind);
	}

	/// <summary>Everything an adapter needs to decide whether it applies, and how to launch.</summary>
	public sealed class HotReloadLaunchContext
	{
		/// <param name="project">
		/// May be null. Selection happens before launch, so by the time an adapter is asked to
		/// start it has already decided; only <see cref="IApplicationHotReloadAdapter.CanHandle"/>
		/// needs the project, and it must answer false for a null one. Requiring it here instead
		/// would force anything that merely wants to configure a launch - a test most of all - to
		/// conjure up a whole IProject it never reads.
		/// </param>
		public HotReloadLaunchContext(IProject project, bool withDebugger)
		{
			Project = project;
			WithDebugger = withDebugger;
		}

		public IProject Project { get; }

		public bool WithDebugger { get; }

		public ISolution Solution => Project.ParentSolution;
	}

	/// <summary>
	/// One edit offered to an adapter. <see cref="PreviousAcceptedText"/> is only advanced after a
	/// successful apply, so a failed apply leaves the next attempt with a full, valid fallback.
	/// </summary>
	public sealed class HotReloadDocumentChange
	{
		public HotReloadDocumentChange(string filePath, string previousAcceptedText, string currentText,
			int documentVersion, HotReloadChangeKind kind)
		{
			FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
			PreviousAcceptedText = previousAcceptedText;
			CurrentText = currentText ?? throw new ArgumentNullException(nameof(currentText));
			DocumentVersion = documentVersion;
			Kind = kind;
		}

		public string FilePath { get; }

		public string PreviousAcceptedText { get; }

		public string CurrentText { get; }

		public int DocumentVersion { get; }

		public HotReloadChangeKind Kind { get; }
	}

	/// <summary>
	/// The result of one apply. Sending a request is never success, and neither is saving a file:
	/// an adapter reports <see cref="HotReloadOutcome.Applied"/> only when the framework confirmed it.
	/// </summary>
	public sealed class HotReloadApplyResult
	{
		public HotReloadApplyResult(HotReloadOutcome outcome, string message,
			string frameworkDiagnostic = null, bool? statePreserved = null)
		{
			Outcome = outcome;
			Message = message ?? outcome.ToString();
			FrameworkDiagnostic = frameworkDiagnostic;
			StatePreserved = statePreserved;
		}

		public HotReloadOutcome Outcome { get; }

		public string Message { get; }

		public string FrameworkDiagnostic { get; }

		/// <summary>Null when the adapter cannot report state preservation.</summary>
		public bool? StatePreserved { get; }

		public bool IsSuccess => Outcome == HotReloadOutcome.Applied || Outcome == HotReloadOutcome.Degraded;

		public static HotReloadApplyResult Unsupported(string message) =>
			new HotReloadApplyResult(HotReloadOutcome.Unsupported, message);
	}

	/// <summary>A live Hot Reload session for one launched application.</summary>
	public interface IHotReloadSession : IAsyncDisposable
	{
		string Framework { get; }

		HotReloadCapabilities Capabilities { get; }

		HotReloadSessionState State { get; }

		/// <summary>The launched application, when the adapter knows it.</summary>
		int? ProcessId { get; }

		event EventHandler<HotReloadSessionState> StateChanged;

		/// <summary>
		/// Adapter-specific details for the status surface and for diagnosing a session that is not
		/// behaving - the endpoint it talks to, the log the framework writes, and so on. Keys are
		/// adapter-defined; callers display them rather than branching on them.
		/// </summary>
		IReadOnlyDictionary<string, string> GetDiagnostics();

		/// <summary>Completes when the framework can actually accept a change, or the session fails.</summary>
		Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken token);

		/// <summary>
		/// Only meaningful when <see cref="HotReloadCapabilities.CanApplyFromIde"/>. A
		/// framework-driven adapter returns <see cref="HotReloadOutcome.Unsupported"/> rather than
		/// pretending to have applied something it never transmitted.
		/// </summary>
		Task<HotReloadApplyResult> ApplyAsync(HotReloadDocumentChange change, CancellationToken token);
	}

	/// <summary>
	/// A framework's Hot Reload integration. Adapters are contributed through the AddIn tree, so a
	/// new framework adds a contribution instead of editing shared launch code.
	/// </summary>
	public interface IApplicationHotReloadAdapter
	{
		/// <summary>
		/// Stable id for this framework (for example "WPF", "Uno"). It identifies the adapter in
		/// status and output messages; it is not a channel name, since Hot Reload has a single
		/// output channel.
		/// </summary>
		string Framework { get; }

		/// <summary>
		/// Must be cheap and free of side effects: it runs on every command-enablement query.
		/// </summary>
		bool CanHandle(HotReloadLaunchContext context, out string diagnostic);

		HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context);

		/// <summary>
		/// Configures the launch the normal Run/Debug path already prepared - environment
		/// variables, startup hooks, arguments - and starts any sidecar the adapter owns. It must
		/// never launch the application itself: that stays with the project's launch path so
		/// build-before-run, debugger attach and the launch fingerprint keep working unchanged.
		/// </summary>
		Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
			CancellationToken token);
	}
}
