using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

public enum WpfSurfaceHostBackend { LibreWpf, MicrosoftWpf }

/// <summary>Host-side DDP client for the WPF out-of-process design host, alongside
/// FormsDesignerHostClient/UnoDesignClient (see doc/technotes/designer-common.md's adapter
/// seam). Lives in its own WPF-free Remote project, mirroring FormsDesigner.Remote/
/// WinUIXamlDesigner.UnoDesignHost.Remote, so it can be referenced by both tests and
/// WpfDesign.AddIn without pulling in the child's own WPF/designer-engine dependencies.</summary>
public sealed class WpfSurfaceHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient, IDesignHostBounds, IDesignHostHitTesting, IDesignHostPropertyReset, IDesignHostEventBinding, IDesignHostBindingPath
{
	/// <summary>IDE-owned sink for a child's stdout/stderr. The Remote assembly deliberately does
	/// not reference the workbench, so WpfViewContent supplies the Output-pad implementation.</summary>
	public static Action<string>? OutputLineSink { get; set; }

	static readonly SharedDesignerHostPool<CompatibilityKey, Connection> sharedPool = new(
		(_, connection) => connection.IsAlive,
		async (key, token) => {
			var connection = new Connection(key.HostDllPath, key.OperationTimeout);
			RouteOutput(connection);
			// AcquireSharedAsync is also called synchronously by WpfViewContent.LoadInternal on the
			// dispatcher. Never capture that SynchronizationContext here or the handshake continuation
			// deadlocks against LoadInternal's GetResult().
			await connection.StartConnectionAsync(token).ConfigureAwait(false);
			return connection;
		});
	static readonly object clientsGate = new();
	static readonly HashSet<WpfSurfaceHostClient> clients = new();
	static readonly Dictionary<CompatibilityKey, SharedDesignerHostRecovery<WpfSurfaceHostClient, Connection>> recoveries = new();
	Connection connection;
	readonly CompatibilityKey? poolKey;
	// Themes are process-local dictionaries.  The source snapshot alone therefore cannot restore
	// what the user was looking at when a shared child is replaced.
	string? recoveryTheme;
	bool disposed;

	WpfSurfaceHostClient(Connection connection, CompatibilityKey? poolKey) : base(connection)
	{
		this.connection = connection;
		this.poolKey = poolKey;
		if (poolKey != null) {
			connection.HostExited += OnConnectionExited;
			lock (clientsGate) clients.Add(this);
		}
	}

	public int RecoveryCount { get; private set; }
	public event EventHandler<DesignerSessionState>? Recovered;
	/// <summary>Raised when this document cannot be reopened while sibling documents recover.</summary>
	public event EventHandler<Exception>? RecoveryFailed;

	/// <summary>Finds the deployed child under this assembly's own "Host"/"MicrosoftHost" subfolder,
	/// matching <c>FormsDesignerHostClient.LocateChildDll</c>'s general shape - each host project's
	/// own <c>DeployToAddIns</c> target copies its build output next to the deployed
	/// <c>WpfDesign.AddIn</c>/this Remote assembly.
	///
	/// That assembly-relative lookup is correct for the LibreWpf host, which deploys to exactly one
	/// place. It is NOT reliable for the Microsoft host: this very assembly
	/// (<c>WpfDesign.SurfaceHost.Remote.dll</c>) is a shared dependency that ends up copied into
	/// every addin folder that transitively references <c>ICSharpCode.WpfDesign.AddIn</c> - not
	/// just <c>DisplayBindings/WpfDesign</c>, but also XamlBinding, AvalonEdit, Decompiler,
	/// XmlEditor, PackageManagement and CodeCoverage's own addin folders (confirmed: seven identical
	/// copies on disk). <c>MicrosoftWpfDesign.SurfaceHost.csproj</c>'s <c>DeployToAddIns</c> only
	/// populates the one under <c>DisplayBindings/WpfDesign</c>, so whichever of those seven copies
	/// the .NET assembly resolver happens to load this type from determines whether the sibling
	/// "MicrosoftHost" folder is found - a fresh build failed this exact way after rebuilding only
	/// the Microsoft host (build order changed which copy loaded first), producing "Could not
	/// locate the WPF design host" for a host that was right there, one addin folder over. Probe
	/// the one true deployed location directly as a fallback, exactly like
	/// RegisterXamlLanguageServiceCommand does for wpf-xaml-ls.dll, so this is correct regardless of
	/// assembly-load order.</summary>
	public static WpfSurfaceHostBackend ResolveBackend(bool useMicrosoftWpf)
		=> useMicrosoftWpf ? WpfSurfaceHostBackend.MicrosoftWpf : WpfSurfaceHostBackend.LibreWpf;

	public static string GetBackendName(WpfSurfaceHostBackend backend)
		=> backend == WpfSurfaceHostBackend.MicrosoftWpf ? "WPF" : "LibreWPF";

	public static string? LocateChildDll(WpfSurfaceHostBackend backend)
	{
		var subfolder = backend == WpfSurfaceHostBackend.MicrosoftWpf ? "MicrosoftHost" : "Host";
		var fileName = backend == WpfSurfaceHostBackend.MicrosoftWpf
			? "MicrosoftWpfDesign.SurfaceHost.dll"
			: "WpfDesign.SurfaceHost.dll";

		var directory = Path.GetDirectoryName(typeof(WpfSurfaceHostClient).Assembly.Location);
		if (!string.IsNullOrEmpty(directory)) {
			var path = Path.Combine(directory, subfolder, fileName);
			if (File.Exists(path))
				return path;
		}

		// AppContext.BaseDirectory is the deployed AddIns/DisplayBindings/WpfDesign folder itself
		// only in a packaged distribution, where OpenDevelop.exe sits next to AddIns/. In a dev/test
		// run (dotnet run/exec from src/Main/SharpDevelop/bin/...), it is several levels below the
		// checkout root that actually holds AddIns/ - SharpDevelopMain.FindApplicationRootPath walks
		// up from the exe's own directory to find it, so the fallback here must do the same, not
		// treat AppContext.BaseDirectory as if it already were that root (confirmed the hard way: a
		// full integration test run still failed every Microsoft-backend WPF test with "Could not
		// locate the WPF design host" after this fallback was deployed everywhere, because
		// AppContext.BaseDirectory/AddIns never existed in that layout).
		var root = FindOpenDevelopRoot(AppContext.BaseDirectory) ?? FindOpenDevelopRoot(Environment.CurrentDirectory);
		if (root == null)
			return null;

		var wellKnown = Path.Combine(root, "AddIns", "DisplayBindings", "WpfDesign", subfolder, fileName);
		return File.Exists(wellKnown) ? wellKnown : null;
	}

	static string? FindOpenDevelopRoot(string startDirectory)
	{
		if (string.IsNullOrEmpty(startDirectory))
			return null;

		var directory = new DirectoryInfo(startDirectory);
		while (directory != null) {
			if (Directory.Exists(Path.Combine(directory.FullName, "externals", "vscode-wpf")) &&
				Directory.Exists(Path.Combine(directory.FullName, "src", "Main", "Base")))
				return directory.FullName;
			directory = directory.Parent;
		}
		return null;
	}

	/// <summary>Requires an explicit, already-resolved host path. There is deliberately no
	/// fallback to a different backend here - a caller that cannot locate its requested
	/// backend's host must fail loudly (see <see cref="LocateChildDll"/>), never silently run
	/// a WPF project's design surface under the LibreWPF host or vice versa.</summary>
	public static async Task<WpfSurfaceHostClient> StartAsync(string hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		if (string.IsNullOrEmpty(hostDllPath))
			throw new ArgumentException("A resolved host DLL path is required.", nameof(hostDllPath));
		var connection = new Connection(hostDllPath, operationTimeout);
		RouteOutput(connection);
		await connection.StartConnectionAsync(cancellationToken).ConfigureAwait(false);
		return new WpfSurfaceHostClient(connection, null);
	}

	/// <summary>Requires an explicit, already-resolved host path - see the <see cref="StartAsync"/> remarks.</summary>
	public static async Task<WpfSurfaceHostClient> AcquireSharedAsync(string hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		if (string.IsNullOrEmpty(hostDllPath))
			throw new ArgumentException("A resolved host DLL path is required.", nameof(hostDllPath));
		var key = new CompatibilityKey(Path.GetFullPath(hostDllPath), operationTimeout ?? TimeSpan.FromSeconds(30), RuntimeInformation.ProcessArchitecture, DesignerProtocol.Version);
		return new WpfSurfaceHostClient(await sharedPool.AcquireAsync(key, cancellationToken).ConfigureAwait(false), key);
	}

	static void RouteOutput(Connection connection)
		=> connection.OutputLineReceived += (_, line) => OutputLineSink?.Invoke(line);

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default, DesignerViewport? viewport = null)
		=> OpenRecoverableAsync(snapshot, cancellationToken, viewport);

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default, DesignerViewport? viewport = null)
		=> UpdateRecoverableAsync(snapshot, cancellationToken, viewport);

	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> Document.FlushAsync(baseVersion, cancellationToken);

	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken), cancellationToken);

	/// <summary>Removes an explicitly authored WPF property value, letting the design model restore
	/// its normal default. The child records this as its native undoable property transaction.</summary>
	public Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/reset-property",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName }, cancellationToken), cancellationToken);

	/// <summary>Writes an already-known handler name to a WPF XAML event attribute. This does not
	/// execute project code or synthesize code-behind; the editor remains source-authoritative.</summary>
	public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-event",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, eventName, handlerName }, cancellationToken), cancellationToken);

	/// <summary>Updates an existing WPF Binding through the child's design model. The RPC accepts
	/// only a property path, never arbitrary markup, so a Properties-pad edit cannot replace a
	/// Binding object with a literal <c>{Binding ...}</c> string.</summary>
	public Task<DesignerSessionState> SetBindingPathAsync(long baseVersion, string elementId, string propertyName, string path, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-path",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, path }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingModeAsync(long baseVersion, string elementId, string propertyName, string mode, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-mode",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, mode }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingUpdateSourceTriggerAsync(long baseVersion, string elementId, string propertyName, string trigger, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-update-source-trigger",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, trigger }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingElementNameAsync(long baseVersion, string elementId, string propertyName, string elementName, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-element-name",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, elementName }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingRelativeSourceAsync(long baseVersion, string elementId, string propertyName, string relativeSource, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-relative-source",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, relativeSource }, cancellationToken), cancellationToken);

	/// <summary>Sets an existing Binding's Source to a resource key that the child proved is an
	/// authored <c>{StaticResource}</c> reference. This cannot carry arbitrary source markup.</summary>
	public Task<DesignerSessionState> SetBindingSourceAsync(long baseVersion, string elementId, string propertyName, string resourceKey, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-source",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, resourceKey }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingNotifyOnValidationErrorAsync(long baseVersion, string elementId, string propertyName, bool notify, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-notify-on-validation-error",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, notify }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingValidationOptionAsync(long baseVersion, string elementId, string propertyName, string optionName, bool enabled, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-validation-option",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, optionName, enabled }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingTextOptionAsync(long baseVersion, string elementId, string propertyName, string optionName, string value, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-text-option",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, optionName, value }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingConverterAsync(long baseVersion, string elementId, string propertyName, string resourceKey, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-converter",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, resourceKey }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBindingDelayAsync(long baseVersion, string elementId, string propertyName, int delay, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-binding-delay",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, delay }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, DesignerDropTarget dropTarget = null, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, dropTarget, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.SetBoundsAsync(baseVersion, elementId, x, y, width, height, cancellationToken), cancellationToken);

	/// <summary>Commits a multi-element placement as one host transaction and one recovery-tracked
	/// mutation. This is deliberately WPF-specific until other hosts can make the same atomic
	/// source/model guarantee.</summary>
	public Task<DesignerSessionState> SetBoundsBatchAsync(long baseVersion, DesignerBoundsEdit[] edits, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-bounds-batch",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, edits }, cancellationToken), cancellationToken);

	/// <summary>Sets one visible Margin/Canvas edge while preserving the source's existing layout
	/// anchor. WPF-specific: other runtimes do not share the same Margin/attached-property model.</summary>
	public Task<DesignerSessionState> SetLayoutInsetAsync(long baseVersion, string elementId, string kind, string edge, double value, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-layout-inset",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, kind, edge, value }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> ToggleLayoutInsetAnchorAsync(long baseVersion, string elementId, string edge, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/toggle-layout-inset-anchor",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, edge }, cancellationToken), cancellationToken);

	/// <summary>Appends one more MenuItem sibling under an existing Menu/ContextMenu/MenuItem - the
	/// WPF-specific "Type Here" insertion slot's commit action (see WpfSurfaceDesignerControl and
	/// StripTypeHereCommit.Resolve, which the caller runs on the typed text before calling this).
	/// Not part of IDesignHostClient, matching QueryGridGuidesAsync/SetGridTrackSizeAsync above:
	/// WinForms/WinUI have no equivalent RPC shape to share it with.</summary>
	public Task<DesignerSessionState> AddMenuItemAsync(long baseVersion, string parentId, string header, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/add-menu-item", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, header }, cancellationToken), cancellationToken);

	/// <summary>Appends one more item under an existing StatusBar or ToolBar - the "Type Here"
	/// insertion-node commit action for those two strip types, mirroring
	/// <see cref="AddMenuItemAsync"/> for Menu/ContextMenu/MenuItem.</summary>
	public Task<DesignerSessionState> AddStripItemAsync(long baseVersion, string parentId, string text, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/add-strip-item", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, text }, cancellationToken), cancellationToken);

	/// <summary>Replaces a ListBox/ComboBox/ListView's plain items or a TabControl's page headers (<c>design/set-items</c>).</summary>
	public Task<DesignerSessionState> SetItemsAsync(long baseVersion, string elementId, IReadOnlyList<DesignerCollectionItemEdit> items, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-items",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, items }, cancellationToken), cancellationToken);

	/// <summary>Wraps sibling elements in a new Canvas/Grid/Viewbox (<c>design/wrap-elements</c>).</summary>
	public Task<DesignerSessionState> WrapElementsAsync(long baseVersion, IReadOnlyList<string> elementIds, string containerType, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/wrap-elements",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds, containerType }, cancellationToken), cancellationToken);

	/// <summary>Removes a Grid/Canvas, keeping its children in place (<c>design/unwrap-element</c>).</summary>
	public Task<DesignerSessionState> UnwrapElementAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/unwrap-element",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	/// <summary>Moves an element to the front/back or one step forward/backward in its parent's
	/// collection (<c>design/set-z-order</c>) - the designer context menu's Order commands.</summary>
	public Task<DesignerSessionState> SetZOrderAsync(long baseVersion, string elementId, string placement, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-z-order",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, placement }, cancellationToken), cancellationToken);

	/// <summary>Moves an element by <paramref name="delta"/> positions among its siblings (e.g. -1 to
	/// swap with the previous one, +1 with the next) - the tray's reorder-arrow UI's commit action.
	/// Not part of IDesignHostClient, matching AddMenuItemAsync above.</summary>
	public Task<DesignerSessionState> MoveElementAsync(long baseVersion, string elementId, int delta, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/move-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, delta }, cancellationToken), cancellationToken);

	/// <summary>Grid row/column drag guides (WPF-specific - not part of <see cref="IDesignHostClient"/>,
	/// since Uno/WinUI implements the same user-facing feature off its own live XAML text editor
	/// instead, and WinForms has no equivalent Grid concept at all). Read-only; see
	/// <see cref="SetGridTrackSizeAsync"/> for committing a completed drag.</summary>
	public Task<DesignerGridGuides> QueryGridGuidesAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerGridGuides>("design/query-grid-guides", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

	/// <summary>Commits one Grid row's/column's new pixel size (a completed divider drag).</summary>
	public Task<DesignerSessionState> SetGridTrackSizeAsync(long baseVersion, string elementId, bool isRow, int index, double pixels, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-grid-track-size", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, index, pixels }, cancellationToken), cancellationToken);

	/// <summary>Serializes elements to XAML snippets for the clipboard (read-only).</summary>
	public Task<DesignerCopyResult> CopyElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerCopyResult>("design/copy-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds }, cancellationToken);

	/// <summary>Pastes XAML snippets into the target or its nearest accepting ancestor.</summary>
	public Task<DesignerSessionState> PasteElementsAsync(long baseVersion, string targetId, string[] snippets, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/paste-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, targetId, snippets }, cancellationToken), cancellationToken);

	/// <summary>Pastes snippets into one exact Canvas, anchoring their bounds at its local pointer coordinate.</summary>
	public Task<DesignerSessionState> PasteElementsAtAsync(long baseVersion, string canvasId, string[] snippets, double x, double y, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/paste-elements-at", new { sessionId = SessionId, documentId = DocumentId, baseVersion, canvasId, snippets, x, y }, cancellationToken), cancellationToken);

	/// <summary>Pastes snippets into one exact Grid, in the measured cell under its local pointer coordinate.</summary>
	public Task<DesignerSessionState> PasteElementsIntoCellAsync(long baseVersion, string gridId, string[] snippets, double x, double y, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/paste-elements-into-cell", new { sessionId = SessionId, documentId = DocumentId, baseVersion, gridId, snippets, x, y }, cancellationToken), cancellationToken);

	/// <summary>Edit Style → Create Empty: a new keyed Style in the root's Resources, applied to the element.</summary>
	public Task<DesignerSessionState> CreateEmptyStyleAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/create-empty-style", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> CreateBasedOnStyleAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/create-based-on-style", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> CopyThemeStyleAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/copy-theme-style", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> CreateEmptyTemplateAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/create-empty-template", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	/// <summary>Moves elements into a container after one of its children (null = first).</summary>
	public Task<DesignerSessionState> MoveIntoAsync(long baseVersion, string[] elementIds, string targetId, string? afterId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/move-into", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds, targetId, afterId }, cancellationToken), cancellationToken);

	/// <summary>Clones one element into an exact Outline drop target after one of its children.</summary>
	public Task<DesignerSessionState> CloneIntoAsync(long baseVersion, string elementId, string targetId, string? afterId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/clone-into", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, targetId, afterId }, cancellationToken), cancellationToken);

	/// <summary>Sets or clears an element's design-time <c>Hidden</c> or <c>Locked</c> flag.</summary>
	public Task<DesignerSessionState> SetDesignTimeFlagAsync(long baseVersion, string elementId, string flag, bool value, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-design-time-flag", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, flag, value }, cancellationToken), cancellationToken);

	/// <summary>Replaces a Brush property with a solid, linear-gradient or radial-gradient brush.</summary>
	public Task<DesignerSessionState> SetBrushAsync(long baseVersion, string elementId, string propertyName, DesignerBrushInfo brush, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-brush", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, brush }, cancellationToken), cancellationToken);

	/// <summary>Changes one Grid row's/column's unit to <c>Pixel</c>, <c>Star</c> or <c>Auto</c>.</summary>
	public Task<DesignerSessionState> SetGridTrackUnitAsync(long baseVersion, string elementId, bool isRow, int index, string unit, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-grid-track-unit", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, index, unit }, cancellationToken), cancellationToken);

	/// <summary>Splits the row/column containing a local Grid coordinate. The host adjusts attached
	/// row/column indices atomically, so a client never has to reconstruct layout semantics from a
	/// bitmap snapshot.</summary>
	public Task<DesignerSessionState> SplitGridTrackAsync(long baseVersion, string elementId, bool isRow, double position, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/split-grid-track", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, position }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.DeleteElementsAsync(baseVersion, elementIds, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.RenameAsync(baseVersion, elementId, newName, cancellationToken), cancellationToken);

	/// <summary>Switches the design-time theme by name - only has an effect for a project that
	/// embeds <c>themes/*.xaml</c> resources; the response's <c>DesignThemes</c> tells the caller
	/// which themes the project has at all, so the IDE can show exactly those in its combo.</summary>
	public async Task<DesignerSessionState> SetThemeAsync(long baseVersion, string theme, CancellationToken cancellationToken = default)
	{
		var state = await HostConnection.InvokeAsync<DesignerSessionState>("design/theme",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, theme }, cancellationToken).ConfigureAwait(false);
		if (state.Accepted)
			recoveryTheme = theme;
		return state;
	}

	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> Document.HitTestAsync(baseVersion, x, y, cancellationToken);

	/// <summary>Tells the child which element is now selected (null to clear), so it can
	/// temporarily expand a collapsed <c>Expander</c> ancestor of the selection - see
	/// <c>WpfSurfaceHostService.Select</c>'s own doc comment for the full behavior. Deliberately
	/// NOT part of <see cref="IDesignHostClient"/>: WinForms/WinUI have no equivalent concept, and
	/// unlike every other call through <see cref="TrackMutationAsync{T}"/>, a rejected/no-op
	/// response here is completely ordinary (most selections touch no Expander at all), not a sign
	/// of anything wrong.</summary>
	public Task<DesignerSessionState> SelectAsync(long baseVersion, string? elementId, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/select", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

	static SharedDesignerHostRecovery<WpfSurfaceHostClient, Connection> RecoveryFor(CompatibilityKey key)
	{
		lock (clientsGate) {
			if (!recoveries.TryGetValue(key, out var recovery)) {
				recovery = new SharedDesignerHostRecovery<WpfSurfaceHostClient, Connection>(
					sharedPool.GetBroker(key), failed => GetAffectedClients(key, failed),
					client => client.RecoverySnapshot != null,
					(client, token) => client.CaptureRecoverySnapshotAsync(client.RecoverySnapshot!.Version, token),
					(client, replacement, token) => client.RestoreAsync(replacement, token),
					(client, exception) => client.OnRecoveryFailed(exception));
				recoveries.Add(key, recovery);
			}
			return recovery;
		}
	}

	static WpfSurfaceHostClient[] GetAffectedClients(CompatibilityKey key, Connection failed)
	{
		lock (clientsGate) return clients.Where(client => !client.disposed && Equals(client.poolKey, key)
			&& ReferenceEquals(client.connection, failed)).ToArray();
	}

	async Task RestoreAsync(Connection replacement, CancellationToken cancellationToken)
	{
		connection.HostExited -= OnConnectionExited;
		connection = replacement;
		RebindConnection(replacement);
		replacement.HostExited += OnConnectionExited;
		var state = await Document.OpenAsync(RecoverySnapshot!, RecoveryViewport, cancellationToken).ConfigureAwait(false);
		if (state.Accepted && recoveryTheme != null)
		{
			// The fresh session starts with no merged project theme.  Replay only a theme which was
			// successfully applied before the crash; an unavailable theme in newly rebuilt project
			// output must not turn an otherwise usable document recovery into a failure.
			var themed = await SetThemeAsync(state.Version, recoveryTheme, cancellationToken).ConfigureAwait(false);
			if (themed.Accepted)
				state = themed;
		}
		RecoveryCount++;
		Recovered?.Invoke(this, state);
	}

	void OnConnectionExited(object? sender, EventArgs e)
	{
		if (!disposed && poolKey != null)
			_ = RecoveryFor(poolKey).RecoverAllAsync(connection, false, CancellationToken.None);
	}

	void OnRecoveryFailed(Exception exception) => RecoveryFailed?.Invoke(this, exception);

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		if (poolKey != null) {
			connection.HostExited -= OnConnectionExited;
			DetachHostConnection();
			lock (clientsGate) clients.Remove(this);
		}
		try { ShutdownAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
		if (poolKey != null) sharedPool.Release(poolKey, connection); else connection.Dispose();
	}

	// The protocol belongs in the pool partition: an already-running v27 child may otherwise be
	// reused after an IDE update and serialize a tree that lacks a newly required node field.
	sealed record CompatibilityKey(string HostDllPath, TimeSpan OperationTimeout, Architecture Architecture, int ProtocolVersion);
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDllPath;
		public Connection(string hostDllPath, TimeSpan? operationTimeout) : base(operationTimeout) => this.hostDllPath = hostDllPath;
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDllPath;
		protected override string BuildCommandLine(string childDll, int port, string token)
			=> new DesignerHostLaunchSpec().BuildCommandLine(childDll, port, token);
		protected override void ConfigureChildProcess(System.Diagnostics.ProcessStartInfo startInfo)
		{
			// The two WPF implementations must never probe each other's payload.  dotnet resolves
			// managed assemblies from this host's deps/runtimeconfig, while this working directory
			// keeps relative pack/resource probing confined to its deployed runtime folder too.
			// MicrosoftHost and Host are deliberately disjoint sibling directories.
			var hostDirectory = Path.GetDirectoryName(hostDllPath);
			if (!string.IsNullOrEmpty(hostDirectory) && Directory.Exists(hostDirectory))
				startInfo.WorkingDirectory = hostDirectory;
			startInfo.Environment["OPENDEVELOP_WPF_HOST_RUNTIME"] = hostDllPath.Contains(
				Path.DirectorySeparatorChar + "MicrosoftHost" + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase) ? "microsoft" : "librewpf";
		}
		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);
	}
}
