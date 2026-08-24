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
public sealed class WpfSurfaceHostClient : IDesignHostClient
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

	public string DocumentId { get; } = Guid.NewGuid().ToString("N");

	WpfSurfaceHostClient(Connection connection, CompatibilityKey? poolKey)
	{
		this.connection = connection;
		this.poolKey = poolKey;
	}
	public int ProcessId => connection.ProcessId;
	public bool IsAlive => connection.IsAlive;
	public string ChildLog => connection.ChildLog;
	public string SessionId => connection.SessionId;
	public event EventHandler? HostExited { add => connection.HostExited += value; remove => connection.HostExited -= value; }

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
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
		return connection.InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
		return connection.InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);
	}

	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerEditSet>("session/flush", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, cancellationToken);

	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/set-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, value }, cancellationToken);

	public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
		=> throw new NotSupportedException("design/set-event is not implemented by this Phase 0 slice.");

	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, proposedName, x, y }, cancellationToken);

	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x, y, width, height }, cancellationToken);

	/// <summary>Grid row/column drag guides (WPF-specific - not part of <see cref="IDesignHostClient"/>,
	/// since Uno/WinUI implements the same user-facing feature off its own live XAML text editor
	/// instead, and WinForms has no equivalent Grid concept at all). Read-only; see
	/// <see cref="SetGridTrackSizeAsync"/> for committing a completed drag.</summary>
	public Task<DesignerGridGuides> QueryGridGuidesAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerGridGuides>("design/query-grid-guides", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

	/// <summary>Commits one Grid row's/column's new pixel size (a completed divider drag).</summary>
	public Task<DesignerSessionState> SetGridTrackSizeAsync(long baseVersion, string elementId, bool isRow, int index, double pixels, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/set-grid-track-size", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, index, pixels }, cancellationToken);

	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds }, cancellationToken);

	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/rename", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, newName }, cancellationToken);

	/// <summary>Switches the design-time theme by name - only has an effect for a project that
	/// embeds <c>themes/*.xaml</c> resources; the response's <c>DesignThemes</c> tells the caller
	/// which themes the project has at all, so the IDE can show exactly those in its combo.</summary>
	public Task<DesignerSessionState> SetThemeAsync(long baseVersion, string theme, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerSessionState>("design/theme", new { sessionId = SessionId, documentId = DocumentId, baseVersion, theme }, cancellationToken);

	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x, y }, cancellationToken);

	public Task PingAsync(CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<object>("ping", null!, cancellationToken);
	public void TerminateHost() => connection.TerminateHost();

	public Task ShutdownAsync(CancellationToken cancellationToken = default)
		=> connection.InvokeAsync<object>("session/close", new { sessionId = SessionId, documentId = DocumentId }, cancellationToken, TimeSpan.FromSeconds(3));

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
		public new Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken token, TimeSpan? timeout = null) => base.InvokeAsync<T>(method, arguments, token, timeout);
		protected override string GetChildDllPath() => hostDllPath;
		protected override string BuildCommandLine(string childDll, int port, string token) => $"exec \"{childDll}\" --port {port} --token {token}";
		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);
	}
}
