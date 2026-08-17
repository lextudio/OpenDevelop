using System;
using System.Net;
using System.Net.Sockets;
using StreamJsonRpc;

namespace ICSharpCode.WpfDesign.SurfaceHost;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		var port = GetArgument(args, "--port");
		var token = GetArgument(args, "--token");
		if (!int.TryParse(port, out var portNumber) || string.IsNullOrEmpty(token))
			return 2;

		var dispatcher = new WpfHeadlessDispatcher();

		using var tcp = new TcpClient();
		tcp.Connect(IPAddress.Loopback, portNumber);
		var service = new WpfSurfaceHostService(token, dispatcher);
		using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(tcp.GetStream(), tcp.GetStream(), new SystemTextJsonFormatter()));
		rpc.AddLocalRpcTarget(service);
		rpc.StartListening();
		Console.Error.WriteLine($"WpfDesign.SurfaceHost: ready on {portNumber}");
		service.WaitForShutdown();
		dispatcher.Shutdown();
		return 0;
	}

	static string? GetArgument(string[] args, string name)
	{
		for (var index = 0; index + 1 < args.Length; index++)
			if (args[index] == name) return args[index + 1];
		return null;
	}
}
