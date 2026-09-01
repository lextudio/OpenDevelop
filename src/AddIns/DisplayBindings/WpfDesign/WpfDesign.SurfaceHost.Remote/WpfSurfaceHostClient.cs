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
public sealed class WpfSurfaceHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient, IDesignHostBounds, IDesignHostHitTesting
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
	static readonly object clientsGate = new();
	static readonly HashSet<WpfSurfaceHostClient> clients = new();
	static readonly Dictionary<CompatibilityKey, SharedDesignerHostRecovery<WpfSurfaceHostClient, Connection>> recoveries = new();
	Connection connection;
	readonly CompatibilityKey? poolKey;
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

	/// <summary>Finds the deployed child under this assembly's own "Host" subfolder, matching
	/// <c>FormsDesignerHostClient.LocateChildDll</c> exactly - <c>WpfDesign.SurfaceHost.csproj</c>'s
	/// own <c>DeployToAddIns</c> target copies its build output there, next to the deployed
	/// <c>WpfDesign.AddIn</c>/this Remote assembly.</summary>
	public static WpfSurfaceHostBackend ResolveBackend(bool useMicrosoftWpf)
		=> useMicrosoftWpf ? WpfSurfaceHostBackend.MicrosoftWpf : WpfSurfaceHostBackend.LibreWpf;

	public static string GetBackendName(WpfSurfaceHostBackend backend)
		=> backend == WpfSurfaceHostBackend.MicrosoftWpf ? "Microsoft WPF" : "LibreWPF";

	public static string? LocateChildDll()
		=> LocateChildDll(string.Equals(Environment.GetEnvironmentVariable("OD_WPF_RUNTIME"), "microsoft", StringComparison.OrdinalIgnoreCase)
			? WpfSurfaceHostBackend.MicrosoftWpf : WpfSurfaceHostBackend.LibreWpf);

	public static string? LocateChildDll(WpfSurfaceHostBackend backend)
	{
		var directory = Path.GetDirectoryName(typeof(WpfSurfaceHostClient).Assembly.Location);
		if (string.IsNullOrEmpty(directory))
			return null;
		var path = backend == WpfSurfaceHostBackend.MicrosoftWpf
			? Path.Combine(directory, "MicrosoftHost", "MicrosoftWpfDesign.SurfaceHost.dll")
			: Path.Combine(directory, "Host", "WpfDesign.SurfaceHost.dll");
		return File.Exists(path) ? path : null;
	}

	public static string SelectedBackend => GetBackendName(WpfSurfaceHostBackend.LibreWpf);

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
		=> OpenRecoverableAsync(snapshot, cancellationToken);

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		=> UpdateRecoverableAsync(snapshot, cancellationToken);

	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> Document.FlushAsync(baseVersion, cancellationToken);

	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.SetBoundsAsync(baseVersion, elementId, x, y, width, height, cancellationToken), cancellationToken);

	/// <summary>Grid row/column drag guides (WPF-specific - not part of <see cref="IDesignHostClient"/>,
	/// since Uno/WinUI implements the same user-facing feature off its own live XAML text editor
	/// instead, and WinForms has no equivalent Grid concept at all). Read-only; see
	/// <see cref="SetGridTrackSizeAsync"/> for committing a completed drag.</summary>
	public Task<DesignerGridGuides> QueryGridGuidesAsync(long baseVersion, string elementId, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerGridGuides>("design/query-grid-guides", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

	/// <summary>Commits one Grid row's/column's new pixel size (a completed divider drag).</summary>
	public Task<DesignerSessionState> SetGridTrackSizeAsync(long baseVersion, string elementId, bool isRow, int index, double pixels, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(HostConnection.InvokeAsync<DesignerSessionState>("design/set-grid-track-size", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, isRow, index, pixels }, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.DeleteElementsAsync(baseVersion, elementIds, cancellationToken), cancellationToken);

	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> TrackMutationAsync(Document.RenameAsync(baseVersion, elementId, newName, cancellationToken), cancellationToken);

	/// <summary>Switches the design-time theme by name - only has an effect for a project that
	/// embeds <c>themes/*.xaml</c> resources; the response's <c>DesignThemes</c> tells the caller
	/// which themes the project has at all, so the IDE can show exactly those in its combo.</summary>
	public Task<DesignerSessionState> SetThemeAsync(long baseVersion, string theme, CancellationToken cancellationToken = default)
		=> HostConnection.InvokeAsync<DesignerSessionState>("design/theme", new { sessionId = SessionId, documentId = DocumentId, baseVersion, theme }, cancellationToken);

	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> Document.HitTestAsync(baseVersion, x, y, cancellationToken);

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
		var state = await Document.OpenAsync(RecoverySnapshot!, cancellationToken).ConfigureAwait(false);
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

	sealed record CompatibilityKey(string HostDllPath, TimeSpan OperationTimeout, Architecture Architecture);
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDllPath;
		public Connection(string hostDllPath, TimeSpan? operationTimeout) : base(operationTimeout) => this.hostDllPath = hostDllPath;
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDllPath;
		protected override string BuildCommandLine(string childDll, int port, string token)
			=> new DesignerHostLaunchSpec().BuildCommandLine(childDll, port, token);
		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);
	}
}
