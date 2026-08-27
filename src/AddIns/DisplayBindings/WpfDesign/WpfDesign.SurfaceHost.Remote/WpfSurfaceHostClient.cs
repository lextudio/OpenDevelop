using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

/// <summary>Host-side DDP client for the WPF out-of-process design host, alongside
/// FormsDesignerHostClient/UnoDesignClient (see doc/technotes/designer-common.md's adapter
/// seam). Lives in its own WPF-free Remote project, mirroring FormsDesigner.Remote/
/// WinUIXamlDesigner.UnoDesignHost.Remote, so it can be referenced both by tests and - once
/// WpfViewContent.cs actually cuts over - by WpfDesign.AddIn without pulling in the child's own
/// WPF/designer-engine dependencies. No IDE-side caller exists yet (WpfDesign.AddIn is still
/// fully in-process); this class is otherwise real, not a test double.</summary>
public sealed class WpfSurfaceHostClient : DesignerDocumentHostClient, IDesignHostClient
{
	static readonly SharedDesignerHostPool<CompatibilityKey, Connection> sharedPool = new(
		(_, connection) => connection.IsAlive,
		async (key, token) => {
			var connection = new Connection(key.HostDllPath, key.OperationTimeout);
			// AcquireSharedAsync is also called synchronously by WpfViewContent.LoadInternal on the
			// dispatcher. Never capture that SynchronizationContext here or the handshake continuation
			// deadlocks against LoadInternal's GetResult().
			await connection.StartConnectionAsync(token).ConfigureAwait(false);
			return connection;
		});
	readonly Connection connection;
	readonly CompatibilityKey? poolKey;
	bool disposed;

	WpfSurfaceHostClient(Connection connection, CompatibilityKey? poolKey) : base(connection)
	{
		this.connection = connection;
		this.poolKey = poolKey;
	}

	/// <summary>Finds the deployed child under this assembly's own "Host" subfolder, matching
	/// <c>FormsDesignerHostClient.LocateChildDll</c> exactly - <c>WpfDesign.SurfaceHost.csproj</c>'s
	/// own <c>DeployToAddIns</c> target copies its build output there, next to the deployed
	/// <c>WpfDesign.AddIn</c>/this Remote assembly.</summary>
	public static string? LocateChildDll()
	{
		var directory = Path.GetDirectoryName(typeof(WpfSurfaceHostClient).Assembly.Location);
		if (string.IsNullOrEmpty(directory))
			return null;
		var path = Path.Combine(directory, "Host", "WpfDesign.SurfaceHost.dll");
		return File.Exists(path) ? path : null;
	}

	public static async Task<WpfSurfaceHostClient> StartAsync(string? hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		hostDllPath ??= LocateChildDll() ?? throw new InvalidOperationException(
			"Could not locate WpfDesign.SurfaceHost.dll under this assembly's Host subfolder.");
		var connection = new Connection(hostDllPath, operationTimeout);
		await connection.StartConnectionAsync(cancellationToken).ConfigureAwait(false);
		return new WpfSurfaceHostClient(connection, null);
	}

	public static async Task<WpfSurfaceHostClient> AcquireSharedAsync(string? hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		hostDllPath ??= LocateChildDll() ?? throw new InvalidOperationException("Could not locate WpfDesign.SurfaceHost.dll under this assembly's Host subfolder.");
		var key = new CompatibilityKey(Path.GetFullPath(hostDllPath), operationTimeout ?? TimeSpan.FromSeconds(30), RuntimeInformation.ProcessArchitecture);
		return new WpfSurfaceHostClient(await sharedPool.AcquireAsync(key, cancellationToken).ConfigureAwait(false), key);
	}

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		=> Document.OpenAsync(snapshot, cancellationToken);

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		=> Document.UpdateAsync(snapshot, cancellationToken);

	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> Document.FlushAsync(baseVersion, cancellationToken);

	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken);

	public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
		=> throw new NotSupportedException("design/set-event is not implemented by this Phase 0 slice.");

	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		=> Document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, cancellationToken);

	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> Document.SetBoundsAsync(baseVersion, elementId, x, y, width, height, cancellationToken);

	/// <summary>Grid row/column drag guides (WPF-specific - not part of <see cref="IDesignHostClient"/>,
	/// since Uno/WinUI implements the same user-facing feature off its own live XAML text editor
	/// instead, and WinForms has no equivalent Grid concept at all). Read-only; see
	/// <see cref="SetGridTrackSizeAsync"/> for committing a completed drag.</summary>
	public Task<DesignerGridGuides> QueryGridGuidesAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerGridGuides>("design/query-grid-guides", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

	/// <summary>Commits one Grid row's/column's new pixel size (a completed divider drag).</summary>
	public Task<DesignerSessionState> SetGridTrackSizeAsync(long baseVersion, string elementId, bool isRow, int index, double pixels, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerSessionState>("design/set-grid-track-size", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, index, pixels }, cancellationToken);

	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> Document.DeleteElementsAsync(baseVersion, elementIds, cancellationToken);

	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> Document.RenameAsync(baseVersion, elementId, newName, cancellationToken);

	/// <summary>Switches the design-time theme by name - only has an effect for a project that
	/// embeds <c>themes/*.xaml</c> resources; the response's <c>DesignThemes</c> tells the caller
	/// which themes the project has at all, so the IDE can show exactly those in its combo.</summary>
	public Task<DesignerSessionState> SetThemeAsync(long baseVersion, string theme, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerSessionState>("design/theme", new { sessionId = SessionId, documentId = DocumentId, baseVersion, theme }, cancellationToken);

	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> Document.HitTestAsync(baseVersion, x, y, cancellationToken);

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		try { ShutdownAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
		if (poolKey != null) sharedPool.Release(poolKey, connection); else connection.Dispose();
	}

	sealed record CompatibilityKey(string HostDllPath, TimeSpan OperationTimeout, Architecture Architecture);
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDllPath;
		public Connection(string hostDllPath, TimeSpan? operationTimeout) : base(operationTimeout) => this.hostDllPath = hostDllPath;
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDllPath;
		protected override string BuildCommandLine(string childDll, int port, string token) => $"exec \"{childDll}\" --port {port} --token {token}";
		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);
	}
}
