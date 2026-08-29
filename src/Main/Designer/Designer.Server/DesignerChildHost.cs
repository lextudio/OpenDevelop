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
// WinUIXamlDesigner.UnoHost uses the explicit-method overload below: it retains its Uno dispatcher
// pump and RPC map while sharing connection, ready, disconnect and exit handling.

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
		/// <summary>Unblocks <see cref="WaitForShutdown"/> when the parent transport vanishes
		/// before it can send the graceful shutdown RPC.</summary>
		void OnParentDisconnected();
	}

	/// <summary>Runs a designer child process end to end: connect back to the parent's listening
	/// socket, wrap it in a JsonRpc channel, register <paramref name="createService"/>'s target,
	/// and block until it signals shutdown. Returns the process exit code <c>Main</c> should
	/// return (2 for a malformed command line, 0 on a clean shutdown).</summary>
	public static class DesignerChildHost
	{
		/// <summary>
		/// Variant for runtimes that need their own dispatcher loop or explicit RPC-method
		/// registration (currently Uno). It owns the transport, ready signal, disconnect
		/// handling and exception-to-exit-code policy; the runtime supplies only its RPC map
		/// and UI-thread wait/shutdown hooks.
		/// </summary>
		public static int Run(string[] args, string readyMessagePrefix,
			Action<JsonRpc, string> registerMethods, Action waitForShutdown,
			Action onParentDisconnected, Action? afterShutdown = null, Action? afterConnect = null)
		{
			var port = GetArgument(args, "--port");
			var token = GetArgument(args, "--token");
			if (!int.TryParse(port, out var portNumber) || string.IsNullOrEmpty(token))
				return 2;
			try
			{
				using var tcp = new TcpClient();
				tcp.Connect(IPAddress.Loopback, portNumber);
				afterConnect?.Invoke();
				using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(tcp.GetStream(), tcp.GetStream(), new SystemTextJsonFormatter()));
				registerMethods(rpc, token);
				rpc.StartListening();
				Console.Error.WriteLine($"{readyMessagePrefix}: ready on {portNumber}");
				_ = rpc.Completion.ContinueWith(_ => onParentDisconnected(),
					System.Threading.CancellationToken.None,
					System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
					System.Threading.Tasks.TaskScheduler.Default);
				waitForShutdown();
				afterShutdown?.Invoke();
				return 0;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine($"{readyMessagePrefix}: fatal host error: {exception}");
				return 1;
			}
		}

		/// <param name="rpcSynchronizationContext">When supplied, every incoming RPC invocation is
		/// dispatched onto this context instead of the thread pool. Required by hosts whose UI
		/// objects have real thread affinity AND need a running message pump - Microsoft WinForms
		/// designers create genuine HWNDs (BehaviorService's AdornerWindow), so servicing a
		/// session/open on an arbitrary pool thread leaves those windows without a pump and the
		/// call never returns. Left null by hosts that own their entry thread instead (GTK, WPF).</param>
		public static int Run(string[] args, string readyMessagePrefix,
			Func<string, IDesignerChildService> createService, Action? afterShutdown = null,
			System.Threading.SynchronizationContext? rpcSynchronizationContext = null)
		{
			var port = GetArgument(args, "--port");
			var token = GetArgument(args, "--token");
			if (!int.TryParse(port, out var portNumber) || string.IsNullOrEmpty(token))
				return 2;
			try
			{
				return RunCore(portNumber, token, readyMessagePrefix, createService, afterShutdown,
					rpcSynchronizationContext);
			}
			catch (Exception exception)
			{
				// A child host is an implementation detail of the IDE. Letting a transport or
				// shutdown exception escape Main makes CoreCLR call abort() on macOS, which shows
				// an alarming crash dialog for a disposable background process. Preserve the full
				// diagnostic on stderr and report failure with an ordinary process exit instead.
				Console.Error.WriteLine($"{readyMessagePrefix}: fatal host error: {exception}");
				return 1;
			}
		}

		static int RunCore(int portNumber, string token, string readyMessagePrefix,
			Func<string, IDesignerChildService> createService, Action? afterShutdown,
			System.Threading.SynchronizationContext? rpcSynchronizationContext = null)
		{
			using var tcp = new TcpClient();
			tcp.Connect(IPAddress.Loopback, portNumber);
			var service = createService(token);
			using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(tcp.GetStream(), tcp.GetStream(), new SystemTextJsonFormatter()));
			rpc.AddLocalRpcTarget(service);
			// Must be assigned before StartListening: StreamJsonRpc captures it when it begins
			// dispatching, so setting it afterwards would race the first inbound request.
			if (rpcSynchronizationContext != null)
				rpc.SynchronizationContext = rpcSynchronizationContext;
			rpc.StartListening();
			Console.Error.WriteLine($"{readyMessagePrefix}: ready on {portNumber}");
			// A parent killed by the OS cannot send the explicit shutdown RPC. Waiting only on the
			// service event leaves a permanent PPID=1 designer host. Treat transport completion as
			// an equal terminal condition so every shared child follows its parent out.
			// Keep WaitForShutdown on the entry thread: GTK uses it to pump MainContext and WPF
			// has an equivalent dispatcher affinity. Transport completion merely releases that
			// runtime-owned loop; it must not move the loop to TaskScheduler.Default.
			_ = rpc.Completion.ContinueWith(_ => service.OnParentDisconnected(),
				System.Threading.CancellationToken.None,
				System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
				System.Threading.Tasks.TaskScheduler.Default);
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
