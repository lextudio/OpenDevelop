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
	readonly DesignerDocumentRegistry<DesignerHostService> documents = new();

	public MultiDocumentDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		documents.Initialize(sessionId);
		return new HostHandshake { ProtocolVersion = ProtocolVersion, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	DesignerHostService Get(string requestSessionId, string documentId)
	{
		return documents.GetOrAdd(requestSessionId, documentId, () => {
			var service = new DesignerHostService(expectedToken);
			service.Initialize(expectedToken, ProtocolVersion, requestSessionId);
			return service;
		});
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Get(snapshot.SessionId, snapshot.DocumentId).Open(snapshot);
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => GetChecked(snapshot.SessionId, snapshot.DocumentId).Update(snapshot);
	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion) => GetChecked(sessionId, documentId).Flush(sessionId, documentId, baseVersion);
	[JsonRpcMethod("session/close")]
	public void Close(string sessionId, string documentId) => documents.Remove(sessionId, documentId, service => service.Close());
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, int x, int y) => GetChecked(sessionId, documentId).HitTest(sessionId, documentId, baseVersion, x, y);
	[JsonRpcMethod("design/set-selection")]
	public DesignerSessionState SetSelection(string sessionId, string documentId, long baseVersion, string[] elementIds) => GetChecked(sessionId, documentId).SetSelection(sessionId, documentId, baseVersion, elementIds);
	[JsonRpcMethod("design/select-tab")]
	public DesignerSessionState SelectTab(string sessionId, string documentId, long baseVersion, string elementId, int tabIndex) => GetChecked(sessionId, documentId).SelectTab(sessionId, documentId, baseVersion, elementId, tabIndex);
	[JsonRpcMethod("design/hit-test-popup")]
	public DesignerSessionState HitTestPopupAndSelect(string sessionId, string documentId, long baseVersion, string ownerElementId, int x, int y) => GetChecked(sessionId, documentId).HitTestPopupAndSelect(sessionId, documentId, baseVersion, ownerElementId, x, y);
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
	[JsonRpcMethod("design/list-smart-tag-actions")]
	public DesignerSmartTagActions ListSmartTagActions(string sessionId, string documentId, long baseVersion, string elementId) => GetChecked(sessionId, documentId).ListSmartTagActions(sessionId, documentId, baseVersion, elementId);
	[JsonRpcMethod("design/invoke-smart-tag-method")]
	public DesignerSessionState InvokeSmartTagMethod(string sessionId, string documentId, long baseVersion, string elementId, int listIndex, int itemIndex) => GetChecked(sessionId, documentId).InvokeSmartTagMethod(sessionId, documentId, baseVersion, elementId, listIndex, itemIndex);
	[JsonRpcMethod("design/list-verbs")]
	public DesignerVerbs ListVerbs(string sessionId, string documentId, long baseVersion, string elementId) => GetChecked(sessionId, documentId).ListVerbs(sessionId, documentId, baseVersion, elementId);
	[JsonRpcMethod("design/invoke-verb")]
	public DesignerSessionState InvokeVerb(string sessionId, string documentId, long baseVersion, string elementId, int verbIndex) => GetChecked(sessionId, documentId).InvokeVerb(sessionId, documentId, baseVersion, elementId, verbIndex);
	[JsonRpcMethod("design/invoke-menu-command")]
	public DesignerSessionState InvokeMenuCommand(string sessionId, string documentId, long baseVersion, string commandGuid, int commandId) => GetChecked(sessionId, documentId).InvokeMenuCommand(sessionId, documentId, baseVersion, commandGuid, commandId);
	[JsonRpcMethod("design/add-toolstrip-item")]
	public DesignerSessionState AddToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, string itemTypeName, string parentItemId, string newItemId) => GetChecked(sessionId, documentId).AddToolStripItem(sessionId, documentId, baseVersion, elementId, itemTypeName, parentItemId, newItemId);
	[JsonRpcMethod("design/reorder-toolstrip-item")]
	public DesignerSessionState ReorderToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, int targetIndex) => GetChecked(sessionId, documentId).ReorderToolStripItem(sessionId, documentId, baseVersion, elementId, targetIndex);
	[JsonRpcMethod("design/get-type-icon")]
	public DesignerTypeIconResult GetTypeIcon(string sessionId, string documentId, string typeName) => GetChecked(sessionId, documentId).GetTypeIcon(typeName);

	DesignerHostService GetChecked(string requestSessionId, string documentId) => documents.Get(requestSessionId, documentId);

	[JsonRpcMethod("ping")]
	public void Ping() { }
	[JsonRpcMethod("diagnostics/delay")]
	public Task Delay(int milliseconds) => Task.Delay(milliseconds);
	[JsonRpcMethod("shutdown")]
	public void Shutdown()
	{
		documents.CloseAll(service => service.Close());
		shutdown.Set();
	}
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();
}
