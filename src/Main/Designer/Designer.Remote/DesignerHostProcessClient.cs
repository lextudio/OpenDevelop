// Shared out-of-process designer host client: spawns a child design host, connects the
// authenticated loopback control plane, pumps the child log, and provides the common
// invoke/timeout/dispose lifecycle used by every designer backend (WinForms today, WinUI/Uno
// today, WPF once isolated). Subclasses provide the child dll path, the launch command line
// (e.g. the designed project's runtimeconfig/depsfile) and the per-runtime method mapping.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Owns one isolated designer child process. See doc/technotes/designer-common.md for the
	/// protocol this implements on the host side.
	/// </summary>
	public abstract class DesignerHostProcessClient : IDisposable
	{
		readonly StringBuilder childLog = new StringBuilder();
		readonly TimeSpan operationTimeout;
		Process process = null!;
		TcpClient tcp = null!;
		JsonRpc rpc = null!;
		volatile bool disposing;
		bool started;

		protected DesignerHostProcessClient(TimeSpan? operationTimeout = null)
		{
			this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
		}

		public int ProcessId => process.Id;
		public bool IsAlive => !disposing && started && !process.HasExited;
		public string ChildLog { get { lock (childLog) return childLog.ToString(); } }
		public event EventHandler? HostExited;

		/// <summary>Host-chosen identity for this child process, minted before launch and
		/// confirmed by the child's handshake echo. Stable for the child's life; every
		/// document opened against this child shares it (see designer-common.md's
		/// "Identity and versioning").</summary>
		public string SessionId { get; } = Guid.NewGuid().ToString("N");

		/// <summary>Absolute path of the deployed child host dll.</summary>
		protected abstract string GetChildDllPath();

		/// <summary>
		/// Builds the <c>dotnet exec</c> command line (without the <c>dotnet</c> host itself),
		/// e.g. <c>exec --runtimeconfig &lt;project&gt;.runtimeconfig.json --depsfile
		/// &lt;project&gt;.deps.json host.dll --port N --token T</c>. Subclasses add the
		/// project dependency graph when the runtime supports it.
		/// </summary>
		protected virtual string BuildCommandLine(string childDll, int port, string token)
			=> $"exec \"{childDll}\" --port {port} --token {token}";

		/// <summary>Timeout for the post-connect handshake. Runtimes with a slow boot (e.g.
		/// headless Uno initialization) override this with a longer limit.</summary>
		protected virtual TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(15);

		/// <summary>
		/// Post-connect handshake hook, run once the RPC channel is live. The default
		/// authenticates the child with the shared token and validates the protocol version
		/// and process id. Subclasses may override to adjust the handshake (or to wait for a
		/// readiness line when a runtime cannot reply to initialize immediately).
		/// </summary>
		protected virtual async Task OnConnectedAsync(JsonRpc rpc, string token, CancellationToken cancellationToken)
		{
			var handshake = await rpc.InvokeWithParameterObjectAsync<HostHandshake>("initialize",
				new { token, protocolVersion = DesignerProtocol.Version, sessionId = SessionId }, cancellationToken)
				.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
			if (handshake.ProtocolVersion != DesignerProtocol.Version || handshake.ProcessId != process.Id)
				throw new InvalidDataException("The designer host returned an incompatible handshake.");
			if (handshake.SessionId != SessionId)
				throw new InvalidDataException("The designer host did not echo the expected session id.");
		}

		/// <summary>Spawns the child and completes the authenticated handshake.</summary>
		protected async Task StartAsync(CancellationToken cancellationToken)
		{
			var childDll = GetChildDllPath();
			if (String.IsNullOrEmpty(childDll) || !File.Exists(childDll))
				throw new FileNotFoundException("The out-of-process designer host is not deployed.", childDll);

			var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			using var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;

			var startInfo = new ProcessStartInfo(FindDotnetHost(), BuildCommandLine(childDll, port, token)) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			ConfigureChildProcess(startInfo);
			process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process.Exited += (sender, args) => {
				if (!disposing) HostExited?.Invoke(this, EventArgs.Empty);
			};
			process.Start();
			started = true;
			// Drain stdout/stderr immediately, before waiting on the TCP accept below - the child's
			// pipes are redirected from the moment Start() returns, and their OS buffer is finite.
			// If the child writes enough startup output (WPF/LibreWPF banners, warnings) before it
			// gets around to connecting, and nothing is reading those pipes yet, the child blocks
			// on its own Console.Write and never connects - while this host sits waiting on
			// AcceptTcpClientAsync below. Starting the pumps here, rather than only after a
			// successful handshake, removes that deadlock window entirely.
			_ = PumpAsync(process.StandardOutput);
			_ = PumpAsync(process.StandardError);

			TcpClient? tcp = null;
			JsonRpc? rpc = null;
			try {
				tcp = await listener.AcceptTcpClientAsync(cancellationToken).AsTask()
					.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
				var stream = tcp.GetStream();
				var handler = new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter());
				rpc = new JsonRpc(handler);
				rpc.StartListening();
				this.tcp = tcp;
				this.rpc = rpc;
				await OnConnectedAsync(rpc, token, cancellationToken).ConfigureAwait(false);
			} catch {
				rpc?.Dispose();
				tcp?.Dispose();
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
				process.Dispose();
				started = false;
				throw;
			}
		}

		/// <summary>Allows a designer to supply runtime-specific child environment settings.</summary>
		protected virtual void ConfigureChildProcess(ProcessStartInfo startInfo) { }

		/// <summary>Invokes a JSON-RPC method on the child with the shared timeout; a timeout
		/// terminates the host (it can no longer be trusted to be responsive).</summary>
		protected Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
		{
			if (!IsAlive)
				throw new IOException("The designer host is not running.");
			return InvokeCoreAsync<T>(method, arguments, cancellationToken, timeout ?? operationTimeout);
		}

		async Task<T> InvokeCoreAsync<T>(string method, object arguments, CancellationToken cancellationToken, TimeSpan timeout)
		{
			try {
				return await rpc.InvokeWithParameterObjectAsync<T>(method, arguments, cancellationToken)
					.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
			} catch (TimeoutException) {
				TerminateHost();
				throw new TimeoutException($"The designer host did not complete '{method}' within {timeout}.");
			}
		}

		/// <summary>Kills the child process tree.</summary>
		public void TerminateHost()
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}

		async Task PumpAsync(StreamReader reader)
		{
			try {
				while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) {
					lock (childLog) childLog.AppendLine(line);
				}
			} catch { }
		}

		static string FindDotnetHost()
		{
			var current = Process.GetCurrentProcess().MainModule?.FileName;
			if (!String.IsNullOrEmpty(current) && Path.GetFileName(current).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
				return current;
			var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
			return !String.IsNullOrEmpty(root) ? Path.Combine(root, "dotnet") : "dotnet";
		}

		public void Dispose()
		{
			if (disposing) return;
			disposing = true;
			try {
				if (started && !process.HasExited)
					rpc.InvokeAsync("shutdown").Wait(TimeSpan.FromSeconds(3));
			} catch { }
			try { rpc?.Dispose(); } catch { }
			try { tcp?.Dispose(); } catch { }
			try {
				if (started && !process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
			} catch { }
			try { process?.Dispose(); } catch { }
		}
	}
}
