// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The generic child-process bootstrap: FormsDesigner.Host/Program.cs and
// WpfDesign.SurfaceHost/Program.cs used to each hand-roll the identical ~15 lines of "parse
// --port/--token, connect back to the parent's listening socket, wrap the stream in a JsonRpc
// channel, register the RPC target, start listening, wait for shutdown" boilerplate - this class
// is that boilerplate, extracted once. It mirrors DesignerHostProcessClient (this project's own
// client-side counterpart, which does the listening/dialing-out from the IDE side) but for the
// child's side of the same handshake.
//
// WinUIXamlDesigner.UnoHost does NOT use this: its child process registers RPC methods
// individually (AddLocalRpcMethod per method, not AddLocalRpcTarget(service)) and runs its own
// UI dispatcher pump instead of a plain WaitHandle, so forcing it onto this exact shape would
// obscure real differences rather than removing incidental duplication - see designer-common.md.

using System;
using System.Net;
using System.Net.Sockets;
using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Implemented by a child host's RPC target so <see cref="DesignerChildHost.Run"/>
	/// knows when to return control to <c>Main</c> - both <c>DesignerHostService</c> (WinForms)
	/// and <c>WpfSurfaceHostService</c> (WPF) already exposed this exact method before this type
	/// existed; this interface just names the shape they already shared.</summary>
	public interface IDesignerChildService
	{
		/// <summary>Blocks the calling thread until the host requests shutdown (an RPC call, a
		/// parent disconnect, or equivalent) - <c>Main</c> returns once this returns.</summary>
		void WaitForShutdown();
	}

	/// <summary>Runs a designer child process end to end: connect back to the parent's listening
	/// socket, wrap it in a JsonRpc channel, register <paramref name="createService"/>'s target,
	/// and block until it signals shutdown. Returns the process exit code <c>Main</c> should
	/// return (2 for a malformed command line, 0 on a clean shutdown).</summary>
	public static class DesignerChildHost
	{
		public static int Run(string[] args, string readyMessagePrefix,
			Func<string, IDesignerChildService> createService, Action? afterShutdown = null)
		{
			var port = GetArgument(args, "--port");
			var token = GetArgument(args, "--token");
			if (!int.TryParse(port, out var portNumber) || string.IsNullOrEmpty(token))
				return 2;

			using var tcp = new TcpClient();
			tcp.Connect(IPAddress.Loopback, portNumber);
			var service = createService(token);
			using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(tcp.GetStream(), tcp.GetStream(), new SystemTextJsonFormatter()));
			rpc.AddLocalRpcTarget(service);
			rpc.StartListening();
			Console.Error.WriteLine($"{readyMessagePrefix}: ready on {portNumber}");
			service.WaitForShutdown();
			afterShutdown?.Invoke();
			return 0;
		}

		public static string? GetArgument(string[] args, string name)
		{
			for (var index = 0; index + 1 < args.Length; index++)
				if (args[index] == name) return args[index + 1];
			return null;
		}
	}
}
