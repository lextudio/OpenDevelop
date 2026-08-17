using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace WpfDesign.SurfaceHost.Tests;

/// <summary>Owns one isolated WPF SurfaceHost child process. Test-only client: the real
/// production client (once WpfDesign.AddIn actually moves out-of-process) would live alongside
/// FormsDesignerHostClient/UnoDesignClient, but this Phase 0 slice has no IDE-side caller yet.</summary>
public sealed class WpfSurfaceHostClient : DesignerHostProcessClient, IDesignHostClient
{
	readonly string hostDllPath;

	public string DocumentId { get; } = Guid.NewGuid().ToString("N");

	WpfSurfaceHostClient(string hostDllPath, TimeSpan? operationTimeout)
		: base(operationTimeout)
	{
		this.hostDllPath = hostDllPath;
	}

	public static async Task<WpfSurfaceHostClient> StartAsync(string hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		var client = new WpfSurfaceHostClient(hostDllPath, operationTimeout);
		await client.StartAsync(cancellationToken).ConfigureAwait(false);
		return client;
	}

	protected override string GetChildDllPath() => hostDllPath;

	protected override string BuildCommandLine(string childDll, int port, string token)
		=> $"exec \"{childDll}\" --port {port} --token {token}";

	// WPF startup (Dispatcher thread + assembly JIT) can be slow the first time.
	protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
		return InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
	{
		snapshot.SessionId = SessionId;
		snapshot.DocumentId = DocumentId;
		return InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);
	}

	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerEditSet>("session/flush", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, cancellationToken);

	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/set-property", new { baseVersion, elementId, propertyName, value }, cancellationToken);

	public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
		=> throw new NotSupportedException("design/set-event is not implemented by this Phase 0 slice.");

	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/add-element", new { baseVersion, parentId, item, proposedName, x, y }, cancellationToken);

	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/set-bounds", new { baseVersion, elementId, x, y, width, height }, cancellationToken);

	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/delete-elements", new { baseVersion, elementIds }, cancellationToken);

	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/rename", new { baseVersion, elementId, newName }, cancellationToken);

	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x, y }, cancellationToken);

	public Task PingAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("ping", null!, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("shutdown", null!, cancellationToken, TimeSpan.FromSeconds(3));
}
