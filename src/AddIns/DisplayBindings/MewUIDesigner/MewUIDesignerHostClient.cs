using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Threading; using System.Threading.Tasks; using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.MewUIDesigner;
sealed class MewUIDesignerHostClient : IDesignHostClient
{
	static readonly SharedDesignerHostBroker<Connection> broker = new(c => c.IsAlive, StartConnectionAsync);
	static readonly SemaphoreSlim recoveryGate = new(1, 1); static readonly object clientsGate = new(); static readonly HashSet<MewUIDesignerHostClient> clients = new();
	Connection connection; readonly DesignerDocumentRpcClient document; DesignerDocumentSnapshot? recoverySnapshot; DesignerSessionState? recoveredState; bool disposed; public string DocumentId { get; } = Guid.NewGuid().ToString("N");
	public int ProcessId => connection.ProcessId; public bool IsAlive => connection.IsAlive; public string ChildLog => connection.ChildLog; public string SessionId => connection.SessionId; public event EventHandler? HostExited;
	public string PoolKey => "mewui"; public int RecoveryCount { get; private set; } public static int ActiveLeaseCount { get { lock (clientsGate) return clients.Count; } } public event EventHandler<DesignerSessionState>? Recovered;
	MewUIDesignerHostClient(Connection connection) { this.connection = connection; document = new DesignerDocumentRpcClient(connection, SessionId, DocumentId); connection.HostExited += OnHostExited; lock (clientsGate) clients.Add(this); }
	public static async Task<MewUIDesignerHostClient> CreateAsync(CancellationToken token = default) => new(await broker.AcquireAsync(token).ConfigureAwait(false));
	static async Task<Connection> StartConnectionAsync(CancellationToken token) { var path = Path.Combine(Path.GetDirectoryName(typeof(MewUIDesignerHostClient).Assembly.Location)!, "Host", "MewUIDesigner.Host.dll"); var connection = new Connection(path); await connection.StartConnectionAsync(token).ConfigureAwait(false); return connection; }
	void Stamp(DesignerDocumentSnapshot s) { s.SessionId = SessionId; s.DocumentId = DocumentId; }
	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t = default) { recoverySnapshot = s; return document.OpenAsync(s, t); }
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t = default) { recoverySnapshot = s; return document.UpdateAsync(s, t); }
	public Task<DesignerEditSet> FlushAsync(long v, CancellationToken t = default) => document.FlushAsync(v, t);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string p, string value, CancellationToken t = default) => TrackAsync(document.SetPropertyAsync(v, id, p, value, t), t);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken t = default) => TrackAsync(document.AddElementAsync(v, parent, item, name, x, y, t), t);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken t = default) => TrackAsync(document.DeleteElementsAsync(v, ids, t), t);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken t = default) => TrackAsync(document.RenameAsync(v, id, name, t), t);
	public Task<DesignerSessionState> UndoAsync(long v) => TrackAsync(connection.UndoAsync(DocumentId, v, default), default); public Task<DesignerSessionState> RedoAsync(long v) => TrackAsync(connection.RedoAsync(DocumentId, v, default), default);
	public Task PingAsync(CancellationToken t = default) => connection.PingAsync(t); public Task ShutdownAsync(CancellationToken t = default) => connection.ShutdownAsync(t); public void TerminateHost() => connection.TerminateHost();
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => TrackAsync(document.SetEventAsync(v, id, e, h, t), t); public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException(); public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => TrackAsync(connection.ReorderAsync(DocumentId, v, id, delta, t), t);
	async Task<DesignerSessionState> TrackAsync(Task<DesignerSessionState> operation, CancellationToken token) { var result = await operation.ConfigureAwait(false); await CaptureAsync(result.Version, token).ConfigureAwait(false); return result; }
	async Task CaptureAsync(long version, CancellationToken token) { if (recoverySnapshot == null) return; var edit = await document.FlushAsync(version, token).ConfigureAwait(false); recoverySnapshot.Version = version; recoverySnapshot.Files.Clear(); foreach (var file in edit.Files) recoverySnapshot.Files.Add(file); }
	public async Task<DesignerSessionState> RestartPoolAsync(CancellationToken token = default) { await RecoverAllAsync(connection, true, token).ConfigureAwait(false); return recoveredState ?? throw new IOException("MewUI designer document was not recovered."); }
	public async Task<DesignerSessionState> TerminateAndRecoverAsync(CancellationToken token = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		var failed = connection;
		lock (clientsGate) foreach (var client in clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed))) { client.recoveredState = null; failed.HostExited -= client.OnHostExited; }
		failed.TerminateHost();
		await RecoverAllAsync(failed, false, timeout.Token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("MewUI designer document was not recovered after host termination.");
	}
	static async Task RecoverAllAsync(Connection failed, bool capture, CancellationToken token) { await recoveryGate.WaitAsync(token).ConfigureAwait(false); try { MewUIDesignerHostClient[] live; lock (clientsGate) live = clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed)).ToArray(); if (live.Length == 0) return; if (capture && failed.IsAlive) foreach (var client in live) try { await client.CaptureAsync(client.recoverySnapshot?.Version ?? 0, token).ConfigureAwait(false); } catch { } broker.Invalidate(failed); foreach (var client in live) { if (client.recoverySnapshot == null) continue; var replacement = await broker.AcquireAsync(token).ConfigureAwait(false); client.connection.HostExited -= client.OnHostExited; client.connection = replacement; client.document.ReplaceConnection(replacement); replacement.HostExited += client.OnHostExited; client.recoveredState = await client.document.OpenAsync(client.recoverySnapshot, token).ConfigureAwait(false); client.RecoveryCount++; client.Recovered?.Invoke(client, client.recoveredState); } } finally { recoveryGate.Release(); } }
	public void Dispose() { if (disposed) return; disposed = true; connection.HostExited -= OnHostExited; lock (clientsGate) clients.Remove(this); try { document.CloseAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { } broker.Release(connection); }
	void OnHostExited(object? sender, EventArgs e) { HostExited?.Invoke(this, EventArgs.Empty); _ = RecoverAllAsync(connection, false, CancellationToken.None); }
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDll; public Connection(string path) => hostDll = path; public Task StartConnectionAsync(CancellationToken token) => StartAsync(token); protected override string GetChildDllPath() => hostDll;
		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/open", new { snapshot = s }, t); public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/update", new { snapshot = s }, t);
		public Task CloseDocumentAsync(string documentId, CancellationToken t) => InvokeAsync<object>("session/close", new { documentId }, t);
		public Task<DesignerEditSet> FlushAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerEditSet>("session/flush", new { documentId, baseVersion = v }, t);
		public Task<DesignerSessionState> SetPropertyAsync(string documentId, long v, string id, string p, string value, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/set-property", new { documentId, baseVersion = v, elementId = id, propertyName = p, value }, t);
		public Task<DesignerSessionState> SetEventAsync(string documentId, long v, string id, string e, string h, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/set-event", new { documentId, baseVersion = v, elementId = id, eventName = e, handlerName = h }, t);
		public Task<DesignerSessionState> AddElementAsync(string documentId, long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/add-element", new { documentId, baseVersion = v, parentId = parent, item, proposedName = name, x, y }, t);
		public Task<DesignerSessionState> DeleteElementsAsync(string documentId, long v, string[] ids, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/delete-elements", new { documentId, baseVersion = v, elementIds = ids }, t);
		public Task<DesignerSessionState> RenameAsync(string documentId, long v, string id, string name, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/rename", new { documentId, baseVersion = v, elementId = id, newName = name }, t);
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/undo", new { documentId, baseVersion = v }, t); public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/redo", new { documentId, baseVersion = v }, t);
		public Task PingAsync(CancellationToken t) => InvokeAsync<object>("ping", null!, t); public Task ShutdownAsync(CancellationToken t) => InvokeAsync<object>("shutdown", null!, t, TimeSpan.FromSeconds(3));
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { documentId, baseVersion = v, elementId = id, delta }, t);
	}
}
