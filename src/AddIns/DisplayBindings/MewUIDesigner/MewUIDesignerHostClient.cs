using System; using System.IO; using System.Threading; using System.Threading.Tasks; using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.MewUIDesigner;
sealed class MewUIDesignerHostClient : IDesignHostClient
{
	static readonly SemaphoreSlim gate = new(1, 1); static Connection? shared; static int leases;
	readonly Connection connection; bool disposed; public string DocumentId { get; } = Guid.NewGuid().ToString("N");
	public int ProcessId => connection.ProcessId; public bool IsAlive => connection.IsAlive; public string ChildLog => connection.ChildLog; public string SessionId => connection.SessionId; public event EventHandler? HostExited;
	MewUIDesignerHostClient(Connection connection) { this.connection = connection; connection.HostExited += OnHostExited; }
	public static async Task<MewUIDesignerHostClient> CreateAsync(CancellationToken token = default) { await gate.WaitAsync(token).ConfigureAwait(false); try { if (shared == null || !shared.IsAlive) { shared?.Dispose(); var path = Path.Combine(Path.GetDirectoryName(typeof(MewUIDesignerHostClient).Assembly.Location)!, "Host", "MewUIDesigner.Host.dll"); shared = new Connection(path); await shared.StartConnectionAsync(token).ConfigureAwait(false); leases = 0; } leases++; return new MewUIDesignerHostClient(shared); } finally { gate.Release(); } }
	void Stamp(DesignerDocumentSnapshot s) { s.SessionId = SessionId; s.DocumentId = DocumentId; }
	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t = default) { Stamp(s); return connection.OpenAsync(s, t); }
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t = default) { Stamp(s); return connection.UpdateAsync(s, t); }
	public Task<DesignerEditSet> FlushAsync(long v, CancellationToken t = default) => connection.FlushAsync(DocumentId, v, t);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string p, string value, CancellationToken t = default) => connection.SetPropertyAsync(DocumentId, v, id, p, value, t);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken t = default) => connection.AddElementAsync(DocumentId, v, parent, item, name, x, y, t);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken t = default) => connection.DeleteElementsAsync(DocumentId, v, ids, t);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken t = default) => connection.RenameAsync(DocumentId, v, id, name, t);
	public Task<DesignerSessionState> UndoAsync(long v) => connection.UndoAsync(DocumentId, v, default); public Task<DesignerSessionState> RedoAsync(long v) => connection.RedoAsync(DocumentId, v, default);
	public Task PingAsync(CancellationToken t = default) => connection.PingAsync(t); public Task ShutdownAsync(CancellationToken t = default) => connection.ShutdownAsync(t); public void TerminateHost() => connection.TerminateHost();
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => throw new NotSupportedException(); public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException(); public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => connection.ReorderAsync(DocumentId, v, id, delta, t);
	public void Dispose() { if (disposed) return; disposed = true; connection.HostExited -= OnHostExited; try { connection.CloseDocumentAsync(DocumentId, CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { } gate.Wait(); try { if (leases > 0) leases--; if (leases == 0 && ReferenceEquals(shared, connection)) { shared.Dispose(); shared = null; } } finally { gate.Release(); } }
	void OnHostExited(object? sender, EventArgs e) => HostExited?.Invoke(this, EventArgs.Empty);
	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDll; public Connection(string path) => hostDll = path; public Task StartConnectionAsync(CancellationToken token) => StartAsync(token); protected override string GetChildDllPath() => hostDll;
		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/open", new { snapshot = s }, t); public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/update", new { snapshot = s }, t);
		public Task CloseDocumentAsync(string documentId, CancellationToken t) => InvokeAsync<object>("session/close", new { documentId }, t);
		public Task<DesignerEditSet> FlushAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerEditSet>("session/flush", new { documentId, baseVersion = v }, t);
		public Task<DesignerSessionState> SetPropertyAsync(string documentId, long v, string id, string p, string value, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/set-property", new { documentId, baseVersion = v, elementId = id, propertyName = p, value }, t);
		public Task<DesignerSessionState> AddElementAsync(string documentId, long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/add-element", new { documentId, baseVersion = v, parentId = parent, item, proposedName = name, x, y }, t);
		public Task<DesignerSessionState> DeleteElementsAsync(string documentId, long v, string[] ids, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/delete-elements", new { documentId, baseVersion = v, elementIds = ids }, t);
		public Task<DesignerSessionState> RenameAsync(string documentId, long v, string id, string name, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/rename", new { documentId, baseVersion = v, elementId = id, newName = name }, t);
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/undo", new { documentId, baseVersion = v }, t); public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/redo", new { documentId, baseVersion = v }, t);
		public Task PingAsync(CancellationToken t) => InvokeAsync<object>("ping", null!, t); public Task ShutdownAsync(CancellationToken t) => InvokeAsync<object>("shutdown", null!, t, TimeSpan.FromSeconds(3));
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { documentId, baseVersion = v, elementId = id, delta }, t);
	}
}
