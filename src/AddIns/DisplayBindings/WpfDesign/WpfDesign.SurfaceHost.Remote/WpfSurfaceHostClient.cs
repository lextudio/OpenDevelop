using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

/// <summary>Host-side DDP client for the WPF out-of-process design host, alongside
/// FormsDesignerHostClient/UnoDesignClient (see doc/technotes/designer-common.md's adapter
/// seam). Lives in its own WPF-free Remote project, mirroring FormsDesigner.Remote/
/// WinUIXamlDesigner.UnoDesignHost.Remote, so it can be referenced both by tests and - once
/// WpfViewContent.cs actually cuts over - by WpfDesign.AddIn without pulling in the child's own
/// WPF/designer-engine dependencies. No IDE-side caller exists yet (WpfDesign.AddIn is still
/// fully in-process); this class is otherwise real, not a test double.</summary>
public sealed class WpfSurfaceHostClient : DesignerHostProcessClient, IDesignHostClient
{
	readonly string hostDllPath;

	public string DocumentId { get; } = Guid.NewGuid().ToString("N");

	WpfSurfaceHostClient(string hostDllPath, TimeSpan? operationTimeout)
		: base(operationTimeout)
	{
		this.hostDllPath = hostDllPath;
	}

	/// <summary>Finds the deployed child under this assembly's own "Host" subfolder, matching
	/// <c>FormsDesignerHostClient.LocateChildDll</c> exactly - <c>WpfDesign.SurfaceHost.csproj</c>'s
	/// own <c>DeployToAddIns</c> target copies its build output there, next to the deployed
	/// <c>WpfDesign.AddIn</c>/this Remote assembly.</summary>
	public static string? LocateChildDll()
	{
		var directory = Path.GetDirectoryName(typeof(WpfSurfaceHostClient).Assembly.Location);
		if (string.IsNullOrEmpty(directory))
			return null;
		var path = Path.Combine(directory, "Host", "WpfDesign.SurfaceHost.dll");
		return File.Exists(path) ? path : null;
	}

	public static async Task<WpfSurfaceHostClient> StartAsync(string? hostDllPath, CancellationToken cancellationToken, TimeSpan? operationTimeout = null)
	{
		hostDllPath ??= LocateChildDll() ?? throw new InvalidOperationException(
			"Could not locate WpfDesign.SurfaceHost.dll under this assembly's Host subfolder.");
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
