using System;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamJsonRpc;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	class Program
	{
		static int Main(string[] args)
		{
			InstallOwnDependencyResolver();
			return Run(args);
		}

		/// <summary>
		/// The JIT resolves this method's assembly references (StreamJsonRpc) lazily on its
		/// first call - after the resolver hook below is installed, unlike Main itself, whose
		/// body would be JITted before a single line could run.
		/// </summary>
		static void InstallOwnDependencyResolver()
		{
			// AppContext.BaseDirectory points at the deps.json's location when running inside
			// the designed project's dependency graph - the host's own deployment is where the
			// host assembly itself lives.
			var ownDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
			AssemblyLoadContext.Default.Resolving += (_, name) =>
			{
				var candidate = Path.Combine(ownDir, name.Name + ".dll");
				return File.Exists(candidate)
					? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
					: null;
			};
		}

		static int Run(string[] args)
		{
			var port = ParsePort(args);
			var appBin = ParseArgument(args, "--appbin");
			var expectedToken = ParseArgument(args, "--token");

			HeadlessDispatcher.Install();

			PreloadProjectAssemblies(appBin);

			// Connect BEFORE Application.Start: Application.Start installs Uno's
			// SynchronizationContext, whose continuations are posted to the dispatcher
			// queue - and that queue is only pumped once HeadlessDispatcher.Run starts
			// below. Any await on this thread in between would deadlock.
			using var tcp = new TcpClient();
			using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
			{
				try
				{
					tcp.Connect(IPAddress.Loopback, port);
				}
				catch (OperationCanceledException)
				{
					Console.Error.WriteLine("UnoDesignHost: timed out connecting to parent port " + port);
					return 1;
				}
				catch (SocketException e)
				{
					Console.Error.WriteLine("UnoDesignHost: cannot connect to parent port " + port + ": " + e.Message);
					return 1;
				}
			}

			Application.Start(args2 => _ = new HostApp());
			Console.Error.WriteLine("UnoDesignHost: Application.Start returned");

			var stream = tcp.GetStream();
			var formatter = new SystemTextJsonFormatter();
			var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
			var rpc = new JsonRpc(handler);
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
			rpc.StartListening();
			Console.Error.WriteLine("UnoDesignHost: listening");

			Console.Error.WriteLine("UnoDesignHost: ready on " + port);

			// The dispatcher pump runs the Uno UI thread; the RPC callbacks marshal
			// into it. This thread exits when shutdown is requested.
			HeadlessDispatcher.Run();

			rpc.Dispose();
			return 0;
		}

		static int ParsePort(string[] args)
		{
			for (var i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == "--port" && int.TryParse(args[i + 1], out var port))
				{
					return port;
				}
			}
			return 0;
		}

		static string ParseArgument(string[] args, string name)
		{
			for (var i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == name)
				{
					return args[i + 1];
				}
			}
			return null;
		}

		/// <summary>
		/// Preloads the designed project's output assemblies so XamlReader's type resolution
		/// (which scans the loaded assemblies) can find the project's custom controls,
		/// converters and library types. Runs after the dependency resolver hook so the loads
		/// resolve through the project's deps; assemblies that fail to load are skipped.
		/// </summary>
		static void PreloadProjectAssemblies(string appBin)
		{
			if (string.IsNullOrEmpty(appBin) || !Directory.Exists(appBin))
			{
				return;
			}
			foreach (var dll in Directory.EnumerateFiles(appBin, "*.dll"))
			{
				try
				{
					AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
				}
				catch
				{
					// Not a loadable managed assembly (native lib, incompatible build) - skip.
				}
			}
		}

		static readonly DesignHost capabilityHost = new(() => null);
		static readonly ConcurrentDictionary<string, DesignHost> hosts = new(StringComparer.Ordinal);
		static string initializedSessionId;

		static DesignHost Host(string sessionId, string documentId)
		{
			if (sessionId != initializedSessionId) throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
			return hosts.GetOrAdd(documentId, _ => new DesignHost(() => null));
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
			initializedSessionId = sessionId;
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
			Console.Error.WriteLine($"UnoDesignHost: session/open received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
			try { return Host(sessionId, documentId).OpenSession(sessionId, documentId, xaml, width, height, dpi); }
			catch (Exception e) { LogRpcError("session/open", e); throw; }
		}

		static DesignerSessionState UpdateSession(string sessionId, string documentId, string xaml, double width, double height, double dpi, long baseVersion)
		{
			Console.Error.WriteLine($"UnoDesignHost: session/update received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##}, v{baseVersion})");
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
			Console.Error.WriteLine($"UnoDesignHost: design/load received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
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
			Console.Error.WriteLine($"UnoDesignHost: design/theme received ({theme})");
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
			if (sessionId != initializedSessionId) throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
			if (hosts.TryRemove(documentId, out var host)) host.Close();
		}
		static void Ping()
		{
			// Liveness probe; nothing to do - the child answering is the answer.
		}
		static void Shutdown()
		{
			try {
				foreach (var item in hosts.ToArray()) if (hosts.TryRemove(item.Key, out var host)) host.Close();
				capabilityHost.Shutdown();
			}
			catch (Exception e) { LogRpcError("shutdown", e); throw; }
		}

		static void LogRpcError(string method, Exception e)
		{
			Console.Error.WriteLine($"UnoDesignHost: RPC {method} failed: {e}");
		}
	}

	class HostApp : Application
	{
		public HostApp()
		{
			Resources.MergedDictionaries.Add(new XamlControlsResources());
		}
	}
}
