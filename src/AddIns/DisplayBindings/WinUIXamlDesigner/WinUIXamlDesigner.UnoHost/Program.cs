using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;
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
			rpc.AddLocalRpcMethod("initialize", new Func<string, int, string, DesignCapabilities>((token, protocolVersion, sessionId) => Initialize(expectedToken, token, protocolVersion, sessionId)));
			rpc.AddLocalRpcMethod("design/load", new Func<string, double, double, double, DesignSnapshot>(LoadDesign));
			rpc.AddLocalRpcMethod("design/layout", new Func<double, double, double, DesignSnapshot>(Layout));
			rpc.AddLocalRpcMethod("session/open", new Func<string, string, string, double, double, double, DesignSnapshot>(OpenSession));
			rpc.AddLocalRpcMethod("session/update", new Func<string, string, string, double, double, double, long, DesignSnapshot>(UpdateSession));
			rpc.AddLocalRpcMethod("session/flush", new Func<string, string, long, DesignEditSet>(FlushSession));
			rpc.AddLocalRpcMethod("design/set-property", new Func<string, string, long, string, string, string, DesignSnapshot>(SetProperty));
			rpc.AddLocalRpcMethod("design/set-event", new Func<string, string, long, string, string, string, DesignSnapshot>(SetEvent));
			rpc.AddLocalRpcMethod("design/add-element", new Func<string, string, long, string, string, double, double, DesignSnapshot>(AddElement));
			rpc.AddLocalRpcMethod("design/set-bounds", new Func<string, string, long, string, double, double, double, double, DesignSnapshot>(SetBounds));
			rpc.AddLocalRpcMethod("design/delete-elements", new Func<string, string, long, string[], DesignSnapshot>(DeleteElements));
			rpc.AddLocalRpcMethod("design/rename", new Func<string, string, long, string, string, DesignSnapshot>(Rename));
			rpc.AddLocalRpcMethod("design/theme", new Func<string, DesignSnapshot>(SetTheme));
			rpc.AddLocalRpcMethod("app/resources", new Func<string, AppResourcesResult>(LoadAppResources));
			rpc.AddLocalRpcMethod("design/hit-test", new Func<string, string, double, double, HitTestResult>(HitTest));
			rpc.AddLocalRpcMethod("design/export-png", new Func<string, string>(ExportPng));
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

		static readonly DesignHost host = new(() => null);

		/// <summary>
		/// Authenticates the parent with the shared token and validates the protocol version,
		/// then returns the runtime capabilities - one round trip for handshake + capabilities,
		/// matching the common designer protocol's initialize contract.
		/// </summary>
		static DesignCapabilities Initialize(string expectedToken, string token, int protocolVersion, string sessionId)
		{
			if (string.IsNullOrEmpty(expectedToken) || !CryptographicOperations.FixedTimeEquals(
				Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
				throw new UnauthorizedAccessException("Invalid design-host token.");
			if (protocolVersion != DesignProtocol.Version)
				throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
			var capabilities = Capabilities();
			capabilities.SessionId = sessionId;
			return capabilities;
		}

		static DesignCapabilities Capabilities()
		{
			try { return host.GetCapabilities(); }
			catch (Exception e) { LogRpcError("initialize", e); throw; }
		}

		static DesignSnapshot OpenSession(string sessionId, string documentId, string xaml, double width, double height, double dpi)
		{
			Console.Error.WriteLine($"UnoDesignHost: session/open received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
			try { return host.OpenSession(sessionId, documentId, xaml, width, height, dpi); }
			catch (Exception e) { LogRpcError("session/open", e); throw; }
		}

		static DesignSnapshot UpdateSession(string sessionId, string documentId, string xaml, double width, double height, double dpi, long version)
		{
			Console.Error.WriteLine($"UnoDesignHost: session/update received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##}, v{version})");
			try { return host.UpdateSession(sessionId, documentId, xaml, width, height, dpi, version); }
			catch (Exception e) { LogRpcError("session/update", e); throw; }
		}

		static DesignEditSet FlushSession(string sessionId, string documentId, long version)
		{
			try { return host.FlushSession(sessionId, documentId, version); }
			catch (Exception e) { LogRpcError("session/flush", e); throw; }
		}

		static DesignSnapshot SetProperty(string sessionId, string documentId, long version, string elementName, string propertyName, string value)
		{
			try { return host.SetProperty(sessionId, documentId, version, elementName, propertyName, value); }
			catch (Exception e) { LogRpcError("design/set-property", e); throw; }
		}

		static DesignSnapshot SetEvent(string sessionId, string documentId, long version, string elementName, string eventName, string handlerName)
		{
			try { return host.SetEvent(sessionId, documentId, version, elementName, eventName, handlerName); }
			catch (Exception e) { LogRpcError("design/set-event", e); throw; }
		}
		static DesignSnapshot AddElement(string sessionId, string documentId, long version, string parentName, string itemXaml, double x, double y)
		{
			try { return host.AddElement(sessionId, documentId, version, parentName, itemXaml, x, y); }
			catch (Exception e) { LogRpcError("design/add-element", e); throw; }
		}

		static DesignSnapshot SetBounds(string sessionId, string documentId, long version, string elementName, double x, double y, double width, double height)
		{
			try { return host.SetBounds(sessionId, documentId, version, elementName, x, y, width, height); }
			catch (Exception e) { LogRpcError("design/set-bounds", e); throw; }
		}

		static DesignSnapshot DeleteElements(string sessionId, string documentId, long version, string[] elementNames)
		{
			try { return host.DeleteElements(sessionId, documentId, version, elementNames); }
			catch (Exception e) { LogRpcError("design/delete-elements", e); throw; }
		}

		static DesignSnapshot Rename(string sessionId, string documentId, long version, string elementName, string newName)
		{
			try { return host.Rename(sessionId, documentId, version, elementName, newName); }
			catch (Exception e) { LogRpcError("design/rename", e); throw; }
		}

		static DesignSnapshot LoadDesign(string xaml, double width, double height, double dpi)
		{
			Console.Error.WriteLine($"UnoDesignHost: design/load received ({xaml.Length} chars, {width}x{height} @ dpi {dpi:0.##})");
			try { return host.LoadDesign(xaml, width, height, dpi); }
			catch (Exception e) { LogRpcError("design/load", e); throw; }
		}
		static DesignSnapshot Layout(double width, double height, double dpi)
		{
			try { return host.Layout(width, height, dpi); }
			catch (Exception e) { LogRpcError("design/layout", e); throw; }
		}
		static AppResourcesResult LoadAppResources(string xaml)
		{
			try { return host.LoadAppResources(xaml); }
			catch (Exception e) { LogRpcError("app/resources", e); throw; }
		}
		static DesignSnapshot SetTheme(string theme)
		{
			Console.Error.WriteLine($"UnoDesignHost: design/theme received ({theme})");
			try { return host.SetTheme(theme); }
			catch (Exception e) { LogRpcError("design/theme", e); throw; }
		}
		static HitTestResult HitTest(string sessionId, string documentId, double x, double y)
		{
			try { return host.HitTest(sessionId, documentId, x, y); }
			catch (Exception e) { LogRpcError("design/hit-test", e); throw; }
		}
		static string ExportPng(string path)
		{
			try { return host.ExportPng(path); }
			catch (Exception e) { LogRpcError("design/export-png", e); throw; }
		}
		static void Ping()
		{
			// Liveness probe; nothing to do - the child answering is the answer.
		}
		static void Shutdown()
		{
			try { host.Shutdown(); }
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
