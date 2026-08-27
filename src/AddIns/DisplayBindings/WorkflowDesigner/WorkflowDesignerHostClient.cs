using System; using System.Collections.Generic; using System.IO; using System.Threading; using System.Threading.Tasks; using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.WorkflowDesigner;

/// <summary>Owns one shared WorkflowDesigner.Host connection. Modeled directly on
/// MewUIDesignerHostClient's shared-broker shape (designer-common.md). CoreWF activity trees are
/// plain CLR objects, while versioned XAML snapshots provide undo/redo and resilient reopen.</summary>
sealed class WorkflowDesignerHostClient : DesignerDocumentHostClient, IDesignHostClient
{
	static readonly SharedDesignerHostBroker<Connection> broker = new(c => c.IsAlive, StartConnectionAsync);
	readonly Connection connection;
	bool disposed;

	WorkflowDesignerHostClient(Connection connection) : base(connection) => this.connection = connection;

	public static async Task<WorkflowDesignerHostClient> CreateAsync(CancellationToken token = default)
		=> new(await broker.AcquireAsync(token).ConfigureAwait(false));

	static async Task<Connection> StartConnectionAsync(CancellationToken token)
	{
		var path = Path.Combine(Path.GetDirectoryName(typeof(WorkflowDesignerHostClient).Assembly.Location)!, "Host", "WorkflowDesigner.Host.dll");
		var connection = new Connection(path);
		await connection.StartConnectionAsync(token).ConfigureAwait(false);
		return connection;
	}

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default) => Document.OpenAsync(snapshot, token);
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default) => Document.UpdateAsync(snapshot, token);
	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken token = default) => Document.FlushAsync(baseVersion, token);
	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken token = default)
		=> Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, token);
	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken token = default)
		=> Document.RenameAsync(baseVersion, elementId, newName, token);
	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken token = default)
		=> Document.AddElementAsync(baseVersion, parentId, item, proposedName, x, y, token);
	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken token = default)
		=> Document.DeleteElementsAsync(baseVersion, elementIds, token);
	public Task<DesignerSessionState> UndoAsync(long baseVersion, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("design/undo", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, token);
	public Task<DesignerSessionState> RedoAsync(long baseVersion, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("design/redo", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, token);
	public Task<List<WorkflowArgumentInfo>> GetArgumentsAsync(CancellationToken token = default) => connection.InvokeAsync<List<WorkflowArgumentInfo>>("workflow/get-arguments", new { sessionId = SessionId, documentId = DocumentId }, token);
	public Task<DesignerSessionState> AddArgumentAsync(long baseVersion, string name, string typeName, string defaultValue, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("workflow/add-argument", new { sessionId = SessionId, documentId = DocumentId, baseVersion, name, typeName, defaultValue }, token);
	public Task<DesignerSessionState> RemoveArgumentAsync(long baseVersion, string name, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("workflow/remove-argument", new { sessionId = SessionId, documentId = DocumentId, baseVersion, name }, token);
	public Task<DesignerSessionState> UpdateArgumentAsync(long baseVersion, string oldName, string newName, string typeName, string defaultValue, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("workflow/update-argument", new { sessionId = SessionId, documentId = DocumentId, baseVersion, oldName, newName, typeName, defaultValue }, token);
	public Task<List<WorkflowVariableInfo>> GetVariablesAsync(CancellationToken token = default) => connection.InvokeAsync<List<WorkflowVariableInfo>>("workflow/get-variables", new { sessionId = SessionId, documentId = DocumentId }, token);
	public Task<DesignerSessionState> AddVariableAsync(long baseVersion, string name, string typeName, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("workflow/add-variable", new { sessionId = SessionId, documentId = DocumentId, baseVersion, name, typeName }, token);
	public Task<DesignerSessionState> RemoveVariableAsync(long baseVersion, string name, CancellationToken token = default) => connection.InvokeAsync<DesignerSessionState>("workflow/remove-variable", new { sessionId = SessionId, documentId = DocumentId, baseVersion, name }, token);


	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => throw new NotSupportedException();

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		try { base.ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
		broker.Release(connection);
	}

	sealed class Connection : DesignerHostProcessClient
	{
		readonly string hostDll;
		public Connection(string path) => hostDll = path;
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDll;
		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/open", new { snapshot = s }, t);
		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot s, CancellationToken t) => InvokeAsync<DesignerSessionState>("session/update", new { snapshot = s }, t);
		public Task CloseDocumentAsync(string documentId, CancellationToken t) => InvokeAsync<object>("session/close", new { documentId }, t);
		public Task<DesignerEditSet> FlushAsync(string documentId, long v, CancellationToken t) => InvokeAsync<DesignerEditSet>("session/flush", new { documentId, baseVersion = v }, t);
		public Task<DesignerSessionState> SetPropertyAsync(string documentId, long v, string id, string p, string value, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/set-property", new { documentId, baseVersion = v, elementId = id, propertyName = p, value }, t);
		public Task<DesignerSessionState> RenameAsync(string documentId, long v, string id, string name, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/rename", new { documentId, baseVersion = v, elementId = id, newName = name }, t);
		public Task<DesignerSessionState> AddElementAsync(string documentId, long v, string parentId, DesignerToolboxItemInfo item, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/add-element", new { documentId, baseVersion = v, parentId, item }, t);
		public Task<DesignerSessionState> DeleteElementsAsync(string documentId, long v, string[] ids, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/delete-elements", new { documentId, baseVersion = v, elementIds = ids }, t);
		public Task PingAsync(CancellationToken t) => InvokeAsync<object>("ping", null!, t);
	}
}
