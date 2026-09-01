using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

sealed class GtkDesignerHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient, IDesignHostEventBinding, IDesignHostHitTesting
{
	static readonly SharedDesignerHostBroker<GtkDesignerHostConnection> broker = new(
		connection => connection.IsAlive, StartConnectionAsync);
	static readonly object clientsGate = new();
	static readonly HashSet<GtkDesignerHostClient> clients = new();
	static readonly SharedDesignerHostRecovery<GtkDesignerHostClient, GtkDesignerHostConnection> recovery = new(
		broker, GetAffectedClients, client => client.RecoverySnapshot != null,
		(client, token) => client.CaptureRecoverySnapshotAsync(client.RecoverySnapshot!.Version, token),
		(client, replacement, token) => client.RestoreAsync(replacement, token));

	GtkDesignerHostConnection connection;
	DesignerSessionState? recoveredState;
	bool disposed;

	public string PoolKey => "gtk4";
	public static int ActiveLeaseCount { get { lock (clientsGate) return clients.Count; } }
	public int RecoveryCount { get; private set; }
	public event EventHandler<DesignerSessionState>? Recovered;

	GtkDesignerHostClient(GtkDesignerHostConnection connection) : base(connection)
	{
		this.connection = connection;
		connection.HostExited += OnConnectionExited;
		lock (clientsGate) clients.Add(this);
	}

	public static async Task<GtkDesignerHostClient> CreateAsync(CancellationToken token = default)
	{
		return new GtkDesignerHostClient(await broker.AcquireAsync(token).ConfigureAwait(false));
	}

	static async Task<GtkDesignerHostConnection> StartConnectionAsync(CancellationToken token)
	{
		var root = Path.GetDirectoryName(typeof(GtkDesignerHostClient).Assembly.Location)!;
		var connection = new GtkDesignerHostConnection(Path.Combine(root, "Host", "GtkDesigner.Host.dll"));
		await connection.StartConnectionAsync(token).ConfigureAwait(false);
		return connection;
	}

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default)
	{
		return OpenRecoverableAsync(snapshot, token);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default)
	{
		return UpdateRecoverableAsync(snapshot, token);
	}

	public Task<DesignerEditSet> FlushAsync(long version, CancellationToken token = default) => Document.FlushAsync(version, token);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string name, string value, CancellationToken token = default) => TrackMutationAsync(Document.SetPropertyAsync(v, id, name, value, token), token);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken token = default) => TrackMutationAsync(Document.AddElementAsync(v, parent, item, name, x, y, token), token);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken token = default) => TrackMutationAsync(Document.DeleteElementsAsync(v, ids, token), token);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken token = default) => TrackMutationAsync(Document.RenameAsync(v, id, name, token), token);
	public Task<DesignerSessionState> UndoAsync(long v, CancellationToken token = default) => TrackMutationAsync(connection.UndoAsync(DocumentId, v, token), token);
	public Task<DesignerSessionState> RedoAsync(long v, CancellationToken token = default) => TrackMutationAsync(connection.RedoAsync(DocumentId, v, token), token);
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => TrackMutationAsync(Document.SetEventAsync(v, id, e, h, t), t);
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => TrackMutationAsync(connection.ReorderAsync(DocumentId, v, id, delta, t), t);
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => Document.HitTestAsync(v, x, y, t);
	public Task<DesignerSessionState> RenderAsync(long version, CancellationToken token = default) => connection.RenderAsync(DocumentId, version, token);

	public async Task<DesignerSessionState> RestartPoolAsync(CancellationToken token = default)
	{
		await recovery.RecoverAllAsync(connection, true, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered.");
	}
	public async Task<DesignerSessionState> TerminateAndRecoverAsync(CancellationToken token = default)
	{
		var failed = connection;
		lock (clientsGate) foreach (var client in clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed))) { client.recoveredState = null; failed.HostExited -= client.OnConnectionExited; }
		failed.TerminateHost();
		await recovery.RecoverAllAsync(failed, false, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered after host termination.");
	}

	static GtkDesignerHostClient[] GetAffectedClients(GtkDesignerHostConnection failed)
	{
		lock (clientsGate) return clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed)).ToArray();
	}

	async Task RestoreAsync(GtkDesignerHostConnection replacement, CancellationToken token)
	{
		connection.HostExited -= OnConnectionExited;
		connection = replacement;
		RebindConnection(replacement);
		replacement.HostExited += OnConnectionExited;
		recoveredState = await Document.OpenAsync(RecoverySnapshot!, token).ConfigureAwait(false);
		RecoveryCount++;
		Recovered?.Invoke(this, recoveredState);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		connection.HostExited -= OnConnectionExited;
		DetachHostConnection();
		lock (clientsGate) clients.Remove(this);
		try { ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
		broker.Release(connection);
	}

	void OnConnectionExited(object? sender, EventArgs e)
	{
		_ = recovery.RecoverAllAsync(connection, false, CancellationToken.None);
	}

	sealed class GtkDesignerHostConnection : DesignerHostProcessClient
	{
		readonly string hostDll;
		public GtkDesignerHostConnection(string hostDll) => this.hostDll = hostDll;
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDll;
		protected override void ConfigureChildProcess(ProcessStartInfo startInfo)
		{
			if (OperatingSystem.IsMacOS()) {
				var homebrewLibraries = Directory.Exists("/opt/homebrew/lib") ? "/opt/homebrew/lib" : "/usr/local/lib";
				var existing = startInfo.Environment.TryGetValue("DYLD_LIBRARY_PATH", out var value) ? value : null;
				startInfo.Environment["DYLD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? homebrewLibraries : homebrewLibraries + Path.PathSeparator + existing;
				startInfo.Environment["LSUIElement"] = "1";
				startInfo.Environment["LSBackgroundOnly"] = "1";
			}
		}
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/undo", new { sessionId = SessionId, documentId, baseVersion = v }, token);
		public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/redo", new { sessionId = SessionId, documentId, baseVersion = v }, token);
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { sessionId = SessionId, documentId, baseVersion = v, elementId = id, delta }, t);
		public Task<DesignerSessionState> RenderAsync(string documentId, long version, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/render", new { sessionId = SessionId, documentId, baseVersion = version }, token);
	}
}
