using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

sealed class GtkDesignerHostClient : DesignerHostProcessClient, IDesignHostClient
{
	readonly string hostDll;
	public string DocumentId { get; } = Guid.NewGuid().ToString("N");
	GtkDesignerHostClient(string hostDll) => this.hostDll = hostDll;
	public static async Task<GtkDesignerHostClient> CreateAsync(CancellationToken token = default)
	{
		var root = Path.GetDirectoryName(typeof(GtkDesignerHostClient).Assembly.Location)!;
		var path = Path.Combine(root, "Host", "GtkDesigner.Host.dll");
		var client = new GtkDesignerHostClient(path); await client.StartAsync(token).ConfigureAwait(false); return client;
	}
	protected override string GetChildDllPath() => hostDll;
	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default) { Stamp(snapshot); return InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, token); }
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default) { Stamp(snapshot); return InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, token); }
	void Stamp(DesignerDocumentSnapshot snapshot) { snapshot.SessionId = SessionId; snapshot.DocumentId = DocumentId; }
	public Task<DesignerEditSet> FlushAsync(long version, CancellationToken token = default) => InvokeAsync<DesignerEditSet>("session/flush", new { baseVersion = version }, token);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string name, string value, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/set-property", new { baseVersion = v, elementId = id, propertyName = name, value }, token);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/add-element", new { baseVersion = v, parentId = parent, item, proposedName = name, x, y }, token);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/delete-elements", new { baseVersion = v, elementIds = ids }, token);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/rename", new { baseVersion = v, elementId = id, newName = name }, token);
	public Task<DesignerSessionState> UndoAsync(long v, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/undo", new { baseVersion = v }, token);
	public Task<DesignerSessionState> RedoAsync(long v, CancellationToken token = default) => InvokeAsync<DesignerSessionState>("design/redo", new { baseVersion = v }, token);
	public Task PingAsync(CancellationToken token = default) => InvokeAsync<object>("ping", null!, token);
	public Task ShutdownAsync(CancellationToken token = default) => InvokeAsync<object>("shutdown", null!, token, TimeSpan.FromSeconds(3));
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerSessionState> SetBoundsAsync(long v, string id, double x, double y, double w, double h, CancellationToken t = default) => throw new NotSupportedException();
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => throw new NotSupportedException();
}
