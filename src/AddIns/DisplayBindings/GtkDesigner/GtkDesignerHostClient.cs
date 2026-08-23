using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

sealed class GtkDesignerHostClient : IDesignHostClient
{
	static readonly SharedDesignerHostBroker<GtkDesignerHostConnection> broker = new(
		connection => connection.IsAlive, StartConnectionAsync);
	static readonly SemaphoreSlim recoveryGate = new(1, 1);
	static readonly object clientsGate = new();
	static readonly HashSet<GtkDesignerHostClient> clients = new();

	GtkDesignerHostConnection connection;
	DesignerDocumentSnapshot? recoverySnapshot;
	DesignerSessionState? recoveredState;
	bool disposed;

	public string DocumentId { get; } = Guid.NewGuid().ToString("N");
	public int ProcessId => connection.ProcessId;
	public bool IsAlive => connection.IsAlive;
	public string ChildLog => connection.ChildLog;
	public string SessionId => connection.SessionId;
	public string PoolKey => "gtk4";
	public static int ActiveLeaseCount { get { lock (clientsGate) return clients.Count; } }
	public int RecoveryCount { get; private set; }
	public event EventHandler? HostExited;
	public event EventHandler<DesignerSessionState>? Recovered;

	GtkDesignerHostClient(GtkDesignerHostConnection connection)
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
		Stamp(snapshot);
		recoverySnapshot = snapshot;
		return connection.OpenAsync(snapshot, token);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default)
	{
		Stamp(snapshot);
		recoverySnapshot = snapshot;
		return connection.UpdateAsync(snapshot, token);
	}

	void Stamp(DesignerDocumentSnapshot snapshot)
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
	}

	public Task<DesignerEditSet> FlushAsync(long version, CancellationToken token = default) => connection.FlushAsync(DocumentId, version, token);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string name, string value, CancellationToken token = default) => TrackAsync(connection.SetPropertyAsync(DocumentId, v, id, name, value, token), token);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken token = default) => TrackAsync(connection.AddElementAsync(DocumentId, v, parent, item, name, x, y, token), token);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken token = default) => TrackAsync(connection.DeleteElementsAsync(DocumentId, v, ids, token), token);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken token = default) => TrackAsync(connection.RenameAsync(DocumentId, v, id, name, token), token);
	public Task<DesignerSessionState> UndoAsync(long v, CancellationToken token = default) => TrackAsync(connection.UndoAsync(DocumentId, v, token), token);
	public Task<DesignerSessionState> RedoAsync(long v, CancellationToken token = default) => TrackAsync(connection.RedoAsync(DocumentId, v, token), token);
	public Task PingAsync(CancellationToken token = default) => connection.PingAsync(token);
	public Task ShutdownAsync(CancellationToken token = default) => connection.ShutdownAsync(token);
	public void TerminateHost() => connection.TerminateHost();
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => TrackAsync(connection.SetEventAsync(DocumentId, v, id, e, h, t), t);
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => TrackAsync(connection.ReorderAsync(DocumentId, v, id, delta, t), t);
	public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => connection.HitTestAsync(DocumentId, v, x, y, t);
	public Task<DesignerSessionState> RenderAsync(long version, CancellationToken token = default) => connection.RenderAsync(DocumentId, version, token);

	async Task<DesignerSessionState> TrackAsync(Task<DesignerSessionState> operation, CancellationToken token)
	{
		var result = await operation.ConfigureAwait(false);
		var edit = await connection.FlushAsync(DocumentId, result.Version, token).ConfigureAwait(false);
		if (recoverySnapshot != null && edit.Files.Count > 0) {
			recoverySnapshot.Version = result.Version;
			recoverySnapshot.Files.Clear();
			foreach (var file in edit.Files) recoverySnapshot.Files.Add(file);
		}
		return result;
	}

	public async Task<DesignerSessionState> RestartPoolAsync(CancellationToken token = default)
	{
		await RecoverAllAsync(connection, true, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered.");
	}
	public async Task<DesignerSessionState> TerminateAndRecoverAsync(CancellationToken token = default)
	{
		var failed = connection;
		lock (clientsGate) foreach (var client in clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed))) { client.recoveredState = null; failed.HostExited -= client.OnConnectionExited; }
		failed.TerminateHost();
		await RecoverAllAsync(failed, false, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered after host termination.");
	}

	static async Task RecoverAllAsync(GtkDesignerHostConnection failed, bool explicitRestart, CancellationToken token)
	{
		await recoveryGate.WaitAsync(token).ConfigureAwait(false);
		try {
			GtkDesignerHostClient[] live;
			lock (clientsGate) live = clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed)).ToArray();
			if (live.Length == 0) return;
			if (explicitRestart && failed.IsAlive) {
				foreach (var client in live) {
					try { await client.TrackSnapshotAsync(token).ConfigureAwait(false); } catch { }
				}
			}
			broker.Invalidate(failed);
			foreach (var client in live) {
				if (client.recoverySnapshot == null) continue;
				var replacement = await broker.AcquireAsync(token).ConfigureAwait(false);
				client.connection.HostExited -= client.OnConnectionExited;
				client.connection = replacement;
				replacement.HostExited += client.OnConnectionExited;
				client.Stamp(client.recoverySnapshot);
				client.recoveredState = await replacement.OpenAsync(client.recoverySnapshot, token).ConfigureAwait(false);
				client.RecoveryCount++;
				client.Recovered?.Invoke(client, client.recoveredState);
			}
		} finally { recoveryGate.Release(); }
	}

	async Task TrackSnapshotAsync(CancellationToken token)
	{
		if (recoverySnapshot == null) return;
		var edit = await connection.FlushAsync(DocumentId, recoverySnapshot.Version, token).ConfigureAwait(false);
		if (edit.Files.Count == 0) return;
		recoverySnapshot.Files.Clear();
		foreach (var file in edit.Files) recoverySnapshot.Files.Add(file);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		connection.HostExited -= OnConnectionExited;
		lock (clientsGate) clients.Remove(this);
		try { connection.CloseDocumentAsync(DocumentId, CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
		broker.Release(connection);
	}

	void OnConnectionExited(object? sender, EventArgs e)
	{
		HostExited?.Invoke(this, EventArgs.Empty);
		_ = RecoverAllAsync(connection, false, CancellationToken.None);
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
		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token) => InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, token);
		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token) => InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, token);
		public Task CloseDocumentAsync(string documentId, CancellationToken token) => InvokeAsync<object>("session/close", new { documentId }, token);
		public Task<DesignerEditSet> FlushAsync(string documentId, long version, CancellationToken token) => InvokeAsync<DesignerEditSet>("session/flush", new { documentId, baseVersion = version }, token);
		public Task<DesignerSessionState> SetPropertyAsync(string documentId, long v, string id, string name, string value, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/set-property", new { documentId, baseVersion = v, elementId = id, propertyName = name, value }, token);
		public Task<DesignerSessionState> AddElementAsync(string documentId, long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/add-element", new { documentId, baseVersion = v, parentId = parent, item, proposedName = name, x, y }, token);
		public Task<DesignerSessionState> DeleteElementsAsync(string documentId, long v, string[] ids, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/delete-elements", new { documentId, baseVersion = v, elementIds = ids }, token);
		public Task<DesignerSessionState> RenameAsync(string documentId, long v, string id, string name, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/rename", new { documentId, baseVersion = v, elementId = id, newName = name }, token);
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/undo", new { documentId, baseVersion = v }, token);
		public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/redo", new { documentId, baseVersion = v }, token);
		public Task PingAsync(CancellationToken token) => InvokeAsync<object>("ping", null!, token);
		public Task ShutdownAsync(CancellationToken token) => InvokeAsync<object>("shutdown", null!, token, TimeSpan.FromSeconds(3));
		public Task<DesignerSessionState> SetEventAsync(string documentId, long v, string id, string e, string h, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/set-event", new { documentId, baseVersion = v, elementId = id, eventName = e, handlerName = h }, t);
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { documentId, baseVersion = v, elementId = id, delta }, t);
		public Task<DesignerHitTestResult> HitTestAsync(string documentId, long v, double x, double y, CancellationToken t) => InvokeAsync<DesignerHitTestResult>("design/hit-test", new { documentId, baseVersion = v, x, y }, t);
		public Task<DesignerSessionState> RenderAsync(string documentId, long version, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/render", new { documentId, baseVersion = version }, token);
	}
}
