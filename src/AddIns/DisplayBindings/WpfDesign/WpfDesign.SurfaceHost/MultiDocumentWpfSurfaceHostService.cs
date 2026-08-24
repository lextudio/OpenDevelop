using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WpfDesign.SurfaceHost;

sealed class MultiDocumentWpfSurfaceHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly WpfHeadlessDispatcher dispatcher;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly ConcurrentDictionary<string, WpfSurfaceHostService> documents = new(StringComparer.Ordinal);
	string? sessionId;
	bool initialized;

	public MultiDocumentWpfSurfaceHostService(string expectedToken, WpfHeadlessDispatcher dispatcher)
	{
		this.expectedToken = expectedToken;
		this.dispatcher = dispatcher;
	}

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
			throw new UnauthorizedAccessException("Invalid designer-host token.");
		if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
		this.sessionId = sessionId;
		initialized = true;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	WpfSurfaceHostService Get(string documentId)
	{
		if (!initialized || sessionId == null) throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
		return documents.GetOrAdd(documentId, _ => {
			var service = new WpfSurfaceHostService(expectedToken, dispatcher);
			service.Initialize(expectedToken, DesignerProtocol.Version, sessionId);
			return service;
		});
	}

	WpfSurfaceHostService Checked(string requestSessionId, string documentId)
	{
		if (requestSessionId != sessionId) throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
		return Get(documentId);
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Checked(snapshot.SessionId, snapshot.DocumentId).Open(snapshot);
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Checked(snapshot.SessionId, snapshot.DocumentId).Update(snapshot);
	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion) => Checked(sessionId, documentId).Flush(sessionId, documentId, baseVersion);
	[JsonRpcMethod("session/close")]
	public void Close(string sessionId, string documentId) { CheckedSession(sessionId); if (documents.TryRemove(documentId, out var service)) service.Close(); }
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y) => Checked(sessionId, documentId).HitTest(sessionId, documentId, baseVersion, x, y);
	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value) => Checked(sessionId, documentId).SetProperty(baseVersion, elementId, propertyName, value);
	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y) => Checked(sessionId, documentId).AddElement(baseVersion, parentId, item, proposedName, x, y);
	[JsonRpcMethod("design/set-bounds")]
	public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double width, double height) => Checked(sessionId, documentId).SetBounds(baseVersion, elementId, x, y, width, height);
	[JsonRpcMethod("design/query-grid-guides")]
	public DesignerGridGuides QueryGridGuides(string sessionId, string documentId, long baseVersion, string elementId) => Checked(sessionId, documentId).QueryGridGuides(baseVersion, elementId);
	[JsonRpcMethod("design/set-grid-track-size")]
	public DesignerSessionState SetGridTrackSize(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index, double pixels) => Checked(sessionId, documentId).SetGridTrackSize(baseVersion, elementId, isRow, index, pixels);
	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds) => Checked(sessionId, documentId).DeleteElements(baseVersion, elementIds);
	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName) => Checked(sessionId, documentId).Rename(baseVersion, elementId, newName);
	[JsonRpcMethod("design/theme")]
	public DesignerSessionState SetTheme(string sessionId, string documentId, long baseVersion, string theme) => Checked(sessionId, documentId).SetTheme(baseVersion, theme);

	void CheckedSession(string requestSessionId) { if (requestSessionId != sessionId) throw new UnauthorizedAccessException("The request's session id does not match this designer host."); }
	[JsonRpcMethod("ping")]
	public void Ping() { }
	[JsonRpcMethod("shutdown")]
	public void Shutdown()
	{
		foreach (var item in documents.ToArray()) if (documents.TryRemove(item.Key, out var service)) service.Close();
		shutdown.Set();
	}
	public void WaitForShutdown() => shutdown.Wait();
}
