// The designer RPC surface, shared verbatim by BOTH WinUI child hosts.
//
// Extracted from UnoHost/Program.cs, which is where it grew up: every method here is plain DDP
// plumbing over DesignHost and contains nothing Uno-specific, so the Microsoft WinUI 3 host
// source-links this exact file rather than reimplementing 19 RPC registrations. What genuinely
// differs between the two runtimes is only the bootstrap (Uno drives its own headless pump;
// WinUI's Application.Start owns the thread), and that stays in each host's own Program.cs.
//
// LogPrefix is the one thing a host sets, so stderr diagnostics still name the right child.

using System;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;
using System.Security.Cryptography;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	static class DesignRpc
	{
		public static string LogPrefix = "UnoDesignHost";

		public static void RegisterRpcMethods(JsonRpc rpc, string expectedToken)
		{
			rpc.AddLocalRpcMethod("initialize", new Func<string, int, string, DesignerCapabilities>((token, protocolVersion, sessionId) => Initialize(expectedToken, token, protocolVersion, sessionId)));
			rpc.AddLocalRpcMethod("design/load", new Func<string, double, double, double, DesignerSessionState>(LoadDesign));
			rpc.AddLocalRpcMethod("design/layout", new Func<double, double, double, DesignerSessionState>(Layout));
			rpc.AddLocalRpcMethod("session/open", new Func<string, string, string, double, double, double, DesignerSessionState>(OpenSession));
			rpc.AddLocalRpcMethod("session/update", new Func<string, string, string, double, double, double, long, DesignerSessionState>(UpdateSession));
			rpc.AddLocalRpcMethod("session/flush", new Func<string, string, long, DesignerEditSet>(FlushSession));
			rpc.AddLocalRpcMethod("session/close", new Action<string, string>(CloseSession));
			rpc.AddLocalRpcMethod("design/set-property", new Func<string, string, long, string, string, string, DesignerSessionState>(SetProperty));
			rpc.AddLocalRpcMethod("design/set-event", new Func<string, string, long, string, string, string, DesignerSessionState>(SetEvent));
			rpc.AddLocalRpcMethod("design/add-element", new Func<string, string, long, string, DesignerToolboxItemInfo, double, double, DesignerSessionState>(AddElement));
			rpc.AddLocalRpcMethod("design/set-bounds", new Func<string, string, long, string, double, double, double, double, DesignerSessionState>(SetBounds));
			rpc.AddLocalRpcMethod("design/delete-elements", new Func<string, string, long, string[], DesignerSessionState>(DeleteElements));
			rpc.AddLocalRpcMethod("design/rename", new Func<string, string, long, string, string, DesignerSessionState>(Rename));
			rpc.AddLocalRpcMethod("design/theme", new Func<string, string, string, DesignerSessionState>(SetTheme));
			rpc.AddLocalRpcMethod("app/resources", new Func<string, string, string, DesignerAppResourcesResult>(LoadAppResources));
			rpc.AddLocalRpcMethod("design/hit-test", new Func<string, string, double, double, DesignerHitTestResult>(HitTest));
			rpc.AddLocalRpcMethod("design/export-png", new Func<string, string, string, string>(ExportPng));
			rpc.AddLocalRpcMethod("ping", new Action(Ping));
			rpc.AddLocalRpcMethod("shutdown", new Action(Shutdown));
		}

		static readonly DesignHost capabilityHost = new(() => null);
		static readonly DesignerDocumentRegistry<DesignHost> hosts = new();

		static DesignHost Host(string sessionId, string documentId)
		{
			return hosts.GetOrAdd(sessionId, documentId, () => new DesignHost(() => null));
		}

		/// <summary>
		/// Authenticates the parent with the shared token and validates the protocol version,
		/// then returns the runtime capabilities - one round trip for handshake + capabilities,
		/// matching the common designer protocol's initialize contract.
		/// </summary>
		static DesignerCapabilities Initialize(string expectedToken, string token, int protocolVersion, string sessionId)
		{
			if (string.IsNullOrEmpty(expectedToken) || !CryptographicOperations.FixedTimeEquals(
				Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
				throw new UnauthorizedAccessException("Invalid design-host token.");
			if (protocolVersion != DesignerProtocol.Version)
				throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
			var capabilities = Capabilities();
			hosts.Initialize(sessionId);
			capabilities.SessionId = sessionId;
			return capabilities;
		}

		static DesignerCapabilities Capabilities()
		{
			try { return capabilityHost.GetCapabilities(); }
			catch (Exception e) { LogRpcError("initialize", e); throw; }
		}

		static DesignerSessionState OpenSession(string sessionId, string documentId, string xaml, double width, double height, double dpi)
		{
			Console.Error.WriteLine($"{LogPrefix}: session/open received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
			try { return Host(sessionId, documentId).OpenSession(sessionId, documentId, xaml, width, height, dpi); }
			catch (Exception e) { LogRpcError("session/open", e); throw; }
		}

		static DesignerSessionState UpdateSession(string sessionId, string documentId, string xaml, double width, double height, double dpi, long baseVersion)
		{
			Console.Error.WriteLine($"{LogPrefix}: session/update received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##}, v{baseVersion})");
			try { return Host(sessionId, documentId).UpdateSession(sessionId, documentId, xaml, width, height, dpi, baseVersion); }
			catch (Exception e) { LogRpcError("session/update", e); throw; }
		}

		static DesignerEditSet FlushSession(string sessionId, string documentId, long baseVersion)
		{
			try { return Host(sessionId, documentId).FlushSession(sessionId, documentId, baseVersion); }
			catch (Exception e) { LogRpcError("session/flush", e); throw; }
		}

		static DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
		{
			try { return Host(sessionId, documentId).SetProperty(sessionId, documentId, baseVersion, elementId, propertyName, value); }
			catch (Exception e) { LogRpcError("design/set-property", e); throw; }
		}

		static DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName)
		{
			try { return Host(sessionId, documentId).SetEvent(sessionId, documentId, baseVersion, elementId, eventName, handlerName); }
			catch (Exception e) { LogRpcError("design/set-event", e); throw; }
		}
		static DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, double x, double y)
		{
			try { return Host(sessionId, documentId).AddElement(sessionId, documentId, baseVersion, parentId, item, x, y); }
			catch (Exception e) { LogRpcError("design/add-element", e); throw; }
		}

		static DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double width, double height)
		{
			try { return Host(sessionId, documentId).SetBounds(sessionId, documentId, baseVersion, elementId, x, y, width, height); }
			catch (Exception e) { LogRpcError("design/set-bounds", e); throw; }
		}

		static DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
		{
			try { return Host(sessionId, documentId).DeleteElements(sessionId, documentId, baseVersion, elementIds); }
			catch (Exception e) { LogRpcError("design/delete-elements", e); throw; }
		}

		static DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName)
		{
			try { return Host(sessionId, documentId).Rename(sessionId, documentId, baseVersion, elementId, newName); }
			catch (Exception e) { LogRpcError("design/rename", e); throw; }
		}

		static DesignerSessionState LoadDesign(string xaml, double width, double height, double dpi)
		{
			Console.Error.WriteLine($"{LogPrefix}: design/load received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
			try { return capabilityHost.LoadDesign(xaml, width, height, dpi); }
			catch (Exception e) { LogRpcError("design/load", e); throw; }
		}
		static DesignerSessionState Layout(double width, double height, double dpi)
		{
			try { return capabilityHost.Layout(width, height, dpi); }
			catch (Exception e) { LogRpcError("design/layout", e); throw; }
		}
		static DesignerAppResourcesResult LoadAppResources(string sessionId, string documentId, string xaml)
		{
			try { return Host(sessionId, documentId).LoadAppResources(xaml); }
			catch (Exception e) { LogRpcError("app/resources", e); throw; }
		}
		static DesignerSessionState SetTheme(string sessionId, string documentId, string theme)
		{
			Console.Error.WriteLine($"{LogPrefix}: design/theme received ({theme})");
			try { return Host(sessionId, documentId).SetTheme(theme); }
			catch (Exception e) { LogRpcError("design/theme", e); throw; }
		}
		static DesignerHitTestResult HitTest(string sessionId, string documentId, double x, double y)
		{
			try { return Host(sessionId, documentId).HitTest(sessionId, documentId, x, y); }
			catch (Exception e) { LogRpcError("design/hit-test", e); throw; }
		}
		static string ExportPng(string sessionId, string documentId, string path)
		{
			try { return Host(sessionId, documentId).ExportPng(path); }
			catch (Exception e) { LogRpcError("design/export-png", e); throw; }
		}
		static void CloseSession(string sessionId, string documentId)
		{
			hosts.Remove(sessionId, documentId, host => host.Close());
		}
		static void Ping()
		{
			// Liveness probe; nothing to do - the child answering is the answer.
		}
		public static void Shutdown()
		{
			try {
				hosts.CloseAll(host => host.Close());
				capabilityHost.Shutdown();
			}
			catch (Exception e) { LogRpcError("shutdown", e); throw; }
		}

		static void LogRpcError(string method, Exception e)
		{
			Console.Error.WriteLine($"{LogPrefix}: RPC {method} failed: {e}");
		}
	}
}
