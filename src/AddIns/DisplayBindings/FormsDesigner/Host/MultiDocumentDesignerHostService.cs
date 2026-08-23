using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.FormsDesigner.Host;

/// <summary>Connection-level RPC target. Each document owns an independent legacy service,
/// design surface and collectible project load context.</summary>
sealed class MultiDocumentDesignerHostService : IDesignerChildService
{
	const int ProtocolVersion = 2;
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly ConcurrentDictionary<string, DesignerHostService> documents = new(StringComparer.Ordinal);
	string? sessionId;
	bool initialized;

	public MultiDocumentDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
			throw new UnauthorizedAccessException("Invalid designer-host token.");
		if (protocolVersion != ProtocolVersion) throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
		this.sessionId = sessionId;
		initialized = true;
		return new HostHandshake { ProtocolVersion = ProtocolVersion, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	DesignerHostService Get(string documentId)
	{
		if (!initialized || sessionId == null) throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
		return documents.GetOrAdd(documentId, _ => {
			var service = new DesignerHostService(expectedToken);
			service.Initialize(expectedToken, ProtocolVersion, sessionId);
			return service;
		});
	}

	void CheckSession(string requestSessionId)
	{
		if (requestSessionId != sessionId) throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) { CheckSession(snapshot.SessionId); return Get(snapshot.DocumentId).Open(snapshot); }
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) { CheckSession(snapshot.SessionId); return Get(snapshot.DocumentId).Update(snapshot); }
	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion) { CheckSession(sessionId); return Get(documentId).Flush(sessionId, documentId, baseVersion); }
	[JsonRpcMethod("session/close")]
	public void Close(string sessionId, string documentId) { CheckSession(sessionId); if (documents.TryRemove(documentId, out var service)) service.Close(); }
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, int x, int y) => GetChecked(sessionId, documentId).HitTest(sessionId, documentId, baseVersion, x, y);
	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value) => GetChecked(sessionId, documentId).SetProperty(sessionId, documentId, baseVersion, elementId, propertyName, value);
	[JsonRpcMethod("design/reset-property")]
	public DesignerSessionState ResetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName) => GetChecked(sessionId, documentId).ResetProperty(sessionId, documentId, baseVersion, elementId, propertyName);
	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName) => GetChecked(sessionId, documentId).RenameComponent(sessionId, documentId, baseVersion, elementId, newName);
	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName) => GetChecked(sessionId, documentId).SetEvent(sessionId, documentId, baseVersion, elementId, eventName, handlerName);
	[JsonRpcMethod("design/activate-default-event")]
	public DesignerSessionState ActivateDefaultEvent(string sessionId, string documentId, long baseVersion, string elementId) => GetChecked(sessionId, documentId).ActivateDefaultEvent(sessionId, documentId, baseVersion, elementId);
	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string elementId, int x, int y) => GetChecked(sessionId, documentId).AddControl(sessionId, documentId, baseVersion, parentId, item, elementId, x, y);
	[JsonRpcMethod("design/set-bounds")]
	public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, int x, int y, int width, int height) => GetChecked(sessionId, documentId).SetBounds(sessionId, documentId, baseVersion, elementId, x, y, width, height);
	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string elementId) => GetChecked(sessionId, documentId).DeleteComponent(sessionId, documentId, baseVersion, elementId);
	[JsonRpcMethod("design/set-z-order")]
	public DesignerSessionState SetZOrder(string sessionId, string documentId, long baseVersion, string elementId, bool bringToFront) => GetChecked(sessionId, documentId).SetZOrder(sessionId, documentId, baseVersion, elementId, bringToFront);
	[JsonRpcMethod("design/apply-layout")]
	public DesignerSessionState ApplyLayout(string sessionId, string documentId, long baseVersion, string operation, string[] elementIds, int deltaX, int deltaY) => GetChecked(sessionId, documentId).ApplyLayout(sessionId, documentId, baseVersion, operation, elementIds, deltaX, deltaY);

	DesignerHostService GetChecked(string requestSessionId, string documentId) { CheckSession(requestSessionId); return Get(documentId); }

	[JsonRpcMethod("ping")]
	public void Ping() { }
	[JsonRpcMethod("diagnostics/delay")]
	public Task Delay(int milliseconds) => Task.Delay(milliseconds);
	[JsonRpcMethod("shutdown")]
	public void Shutdown()
	{
		foreach (var item in documents.ToArray()) if (documents.TryRemove(item.Key, out var service)) service.Close();
		shutdown.Set();
	}
	public void WaitForShutdown() => shutdown.Wait();
}
