using System.Net;
using System.Net.Sockets;
using StreamJsonRpc;

namespace ICSharpCode.FormsDesigner.Host;

static class Program
{
	static int Main(string[] args)
	{
		var port = GetArgument(args, "--port");
		var token = GetArgument(args, "--token");
		if (!Int32.TryParse(port, out var portNumber) || String.IsNullOrEmpty(token))
			return 2;

		using var tcp = new TcpClient();
		tcp.Connect(IPAddress.Loopback, portNumber);
		var service = new DesignerHostService(token);
		using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(tcp.GetStream(), tcp.GetStream(), new SystemTextJsonFormatter()));
		rpc.AddLocalRpcTarget(service);
		rpc.StartListening();
		Console.Error.WriteLine($"FormsDesigner.Host: ready on {portNumber}");
		service.WaitForShutdown();
		return 0;
	}

	static string? GetArgument(string[] args, string name)
	{
		for (var index = 0; index + 1 < args.Length; index++)
			if (args[index] == name) return args[index + 1];
		return null;
	}
}
