using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

sealed class GtkDesignerHostClient : IDesignHostClient
{
	static readonly SemaphoreSlim gate = new(1, 1);
	static GtkDesignerHostConnection? shared;
	static int leases;

	readonly GtkDesignerHostConnection connection;
	bool disposed;

	public string DocumentId { get; } = Guid.NewGuid().ToString("N");
	public int ProcessId => connection.ProcessId;
	public bool IsAlive => connection.IsAlive;
	public string ChildLog => connection.ChildLog;
	public string SessionId => connection.SessionId;
	public event EventHandler? HostExited;

	GtkDesignerHostClient(GtkDesignerHostConnection connection)
	{
		this.connection = connection;
		connection.HostExited += OnConnectionExited;
	}

	public static async Task<GtkDesignerHostClient> CreateAsync(CancellationToken token = default)
	{
		await gate.WaitAsync(token).ConfigureAwait(false);
		try {
			if (shared == null || !shared.IsAlive) {
				shared?.Dispose();
				var root = Path.GetDirectoryName(typeof(GtkDesignerHostClient).Assembly.Location)!;
				shared = new GtkDesignerHostConnection(Path.Combine(root, "Host", "GtkDesigner.Host.dll"));
				await shared.StartConnectionAsync(token).ConfigureAwait(false);
				leases = 0;
			}
			leases++;
			return new GtkDesignerHostClient(shared);
		} finally {
			gate.Release();
		}
	}

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default)
	{
		Stamp(snapshot);
		return connection.OpenAsync(snapshot, token);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default)
	{
		Stamp(snapshot);
		return connection.UpdateAsync(snapshot, token);
	}

	void Stamp(DesignerDocumentSnapshot snapshot)
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
	}

	public Task<DesignerEditSet> FlushAsync(long version, CancellationToken token = default) => connection.FlushAsync(DocumentId, version, token);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string name, string value, CancellationToken token = default) => connection.SetPropertyAsync(DocumentId, v, id, name, value, token);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken token = default) => connection.AddElementAsync(DocumentId, v, parent, item, name, x, y, token);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken token = default) => connection.DeleteElementsAsync(DocumentId, v, ids, token);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken token = default) => connection.RenameAsync(DocumentId, v, id, name, token);
	public Task<DesignerSessionState> UndoAsync(long v, CancellationToken token = default) => connection.UndoAsync(DocumentId, v, token);
	public Task<DesignerSessionState> RedoAsync(long v, CancellationToken token = default) => connection.RedoAsync(DocumentId, v, token);
	public Task PingAsync(CancellationToken token = default) => connection.PingAsync(token);
	public Task ShutdownAsync(CancellationToken token = default) => connection.ShutdownAsync(token);
	public void TerminateHost() => connection.TerminateHost();
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => connection.SetEventAsync(DocumentId, v, id, e, h, t);
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => connection.ReorderAsync(DocumentId, v, id, delta, t);
	public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => connection.HitTestAsync(DocumentId, v, x, y, t);

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		connection.HostExited -= OnConnectionExited;
		try { connection.CloseDocumentAsync(DocumentId, CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
		gate.Wait();
		try {
			if (leases > 0) leases--;
			if (leases == 0 && ReferenceEquals(shared, connection)) {
				shared.Dispose();
				shared = null;
			}
		} finally {
			gate.Release();
		}
	}

	void OnConnectionExited(object? sender, EventArgs e) => HostExited?.Invoke(this, EventArgs.Empty);

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
	}
}
