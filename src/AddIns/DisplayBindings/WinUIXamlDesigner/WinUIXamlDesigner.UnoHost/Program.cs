using System;
using System.Net;
using System.Net.Sockets;
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
			var port = ParsePort(args);

			HeadlessDispatcher.Install();

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
			rpc.AddLocalRpcMethod("initialize", new Func<DesignCapabilities>(Capabilities));
			rpc.AddLocalRpcMethod("design/load", new Func<string, double, double, double, DesignSnapshot>(LoadDesign));
			rpc.AddLocalRpcMethod("design/layout", new Func<double, double, double, DesignSnapshot>(Layout));
			rpc.AddLocalRpcMethod("app/resources", new Func<string, AppResourcesResult>(LoadAppResources));
			rpc.AddLocalRpcMethod("design/hit-test", new Func<double, double, HitTestResult>(HitTest));
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

		static readonly DesignHost host = new(() => null);

		static DesignCapabilities Capabilities()
		{
			try { return host.GetCapabilities(); }
			catch (Exception e) { LogRpcError("initialize", e); throw; }
		}
		static DesignSnapshot LoadDesign(string xaml, double width, double height, double dpi)
		{
			Console.Error.WriteLine($"UnoDesignHost: design/load received ({xaml.Length} chars, {width}x{height})");
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
		static HitTestResult HitTest(double x, double y)
		{
			try { return host.HitTest(x, y); }
			catch (Exception e) { LogRpcError("design/hit-test", e); throw; }
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
