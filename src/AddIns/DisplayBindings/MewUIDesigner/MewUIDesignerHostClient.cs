using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Threading; using System.Threading.Tasks; using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.MewUIDesigner;
sealed class MewUIDesignerHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient, IDesignHostEventBinding
{
	static readonly SharedDesignerHostBroker<Connection> broker = new(c => c.IsAlive, StartConnectionAsync);
	static readonly object clientsGate = new(); static readonly HashSet<MewUIDesignerHostClient> clients = new();
	static readonly SharedDesignerHostRecovery<MewUIDesignerHostClient, Connection> recovery = new(broker, GetAffectedClients, client => client.RecoverySnapshot != null, (client, token) => client.CaptureRecoverySnapshotAsync(client.RecoverySnapshot!.Version, token), (client, replacement, token) => client.RestoreAsync(replacement, token), (client, exception) => client.OnRecoveryFailed(exception));
	Connection connection; DesignerSessionState? recoveredState; bool disposed;
	public string PoolKey => "mewui"; public int RecoveryCount { get; private set; } public static int ActiveLeaseCount { get { lock (clientsGate) return clients.Count; } } public event EventHandler<DesignerSessionState>? Recovered; public event EventHandler<Exception>? RecoveryFailed;
	MewUIDesignerHostClient(Connection connection) : base(connection) { this.connection = connection; connection.HostExited += OnHostExited; lock (clientsGate) clients.Add(this); }
	public static async Task<MewUIDesignerHostClient> CreateAsync(CancellationToken token = default) => new(await broker.AcquireAsync(token).ConfigureAwait(false));
	static async Task<Connection> StartConnectionAsync(CancellationToken token) { var path = Path.Combine(Path.GetDirectoryName(typeof(MewUIDesignerHostClient).Assembly.Location)!, "Host", "MewUIDesigner.Host.dll"); var connection = new Connection(path); await connection.StartConnectionAsync(token).ConfigureAwait(false); return connection; }
	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t = default) => OpenRecoverableAsync(s, t);
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t = default) => UpdateRecoverableAsync(s, t);
	public Task<DesignerEditSet> FlushAsync(long v, CancellationToken t = default) => Document.FlushAsync(v, t);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string p, string value, CancellationToken t = default) => TrackMutationAsync(Document.SetPropertyAsync(v, id, p, value, t), t);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken t = default) => TrackMutationAsync(Document.AddElementAsync(v, parent, item, name, x, y, t), t);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken t = default) => TrackMutationAsync(Document.DeleteElementsAsync(v, ids, t), t);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken t = default) => TrackMutationAsync(Document.RenameAsync(v, id, name, t), t);
	public Task<DesignerSessionState> UndoAsync(long v) => TrackMutationAsync(connection.UndoAsync(DocumentId, v, default), default); public Task<DesignerSessionState> RedoAsync(long v) => TrackMutationAsync(connection.RedoAsync(DocumentId, v, default), default);
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => TrackMutationAsync(Document.SetEventAsync(v, id, e, h, t), t);
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => TrackMutationAsync(connection.ReorderAsync(DocumentId, v, id, delta, t), t);
	public async Task<DesignerSessionState> RestartPoolAsync(CancellationToken token = default) { await recovery.RecoverAllAsync(connection, true, token).ConfigureAwait(false); return recoveredState ?? throw new IOException("MewUI designer document was not recovered."); }
	public async Task<DesignerSessionState> TerminateAndRecoverAsync(CancellationToken token = default)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		var failed = connection;
		lock (clientsGate) foreach (var client in clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed))) { client.recoveredState = null; failed.HostExited -= client.OnHostExited; }
		failed.TerminateHost();
		await recovery.RecoverAllAsync(failed, false, timeout.Token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("MewUI designer document was not recovered after host termination.");
	}
	static MewUIDesignerHostClient[] GetAffectedClients(Connection failed) { lock (clientsGate) return clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed)).ToArray(); }
	async Task RestoreAsync(Connection replacement, CancellationToken token) { connection.HostExited -= OnHostExited; connection = replacement; RebindConnection(replacement); replacement.HostExited += OnHostExited; recoveredState = await Document.OpenAsync(RecoverySnapshot!, token).ConfigureAwait(false); RecoveryCount++; Recovered?.Invoke(this, recoveredState); }
	public void Dispose() { if (disposed) return; disposed = true; connection.HostExited -= OnHostExited; DetachHostConnection(); lock (clientsGate) clients.Remove(this); try { ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { } broker.Release(connection); }
	void OnHostExited(object? sender, EventArgs e) { _ = recovery.RecoverAllAsync(connection, false, CancellationToken.None); }
	void OnRecoveryFailed(Exception exception) => RecoveryFailed?.Invoke(this, exception);
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDll; public Connection(string path) => hostDll = path; public Task StartConnectionAsync(CancellationToken token) => StartAsync(token); protected override string GetChildDllPath() => hostDll;
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/undo", new { sessionId = SessionId, documentId, baseVersion = v }, t); public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/redo", new { sessionId = SessionId, documentId, baseVersion = v }, t);
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { sessionId = SessionId, documentId, baseVersion = v, elementId = id, delta }, t);
	}
}
