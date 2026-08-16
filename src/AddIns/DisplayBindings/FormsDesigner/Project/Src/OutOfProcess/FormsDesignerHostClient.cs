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

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>Owns one isolated WinForms designer child process.</summary>
	public sealed class FormsDesignerHostClient : IDisposable
	{
		readonly Process process;
		readonly TcpClient tcp;
		readonly JsonRpc rpc;
		readonly TimeSpan operationTimeout;
		readonly StringBuilder childLog = new StringBuilder();
		bool disposing;

		FormsDesignerHostClient(Process process, TcpClient tcp, JsonRpc rpc, TimeSpan operationTimeout)
		{
			this.process = process;
			this.tcp = tcp;
			this.rpc = rpc;
			this.operationTimeout = operationTimeout;
			process.Exited += (sender, args) => {
				if (!disposing) HostExited?.Invoke(this, EventArgs.Empty);
			};
		}

		public int ProcessId => process.Id;
		public bool IsAlive => !disposing && !process.HasExited;
		public string ChildLog { get { lock (childLog) return childLog.ToString(); } }
		public event EventHandler HostExited;

		public static string LocateChildDll()
		{
			var directory = Path.GetDirectoryName(typeof(FormsDesignerHostClient).Assembly.Location);
			if (String.IsNullOrEmpty(directory))
				return null;
			var path = Path.Combine(directory, "Host", "FormsDesigner.Host.dll");
			return File.Exists(path) ? path : null;
		}

		public static async Task<FormsDesignerHostClient> StartAsync(
			string runtimeConfigPath,
			string depsFilePath,
			CancellationToken cancellationToken,
			string hostDllPath = null,
			TimeSpan? operationTimeout = null)
		{
			var hostDll = hostDllPath ?? LocateChildDll();
			if (String.IsNullOrEmpty(hostDll) || !File.Exists(hostDll))
				throw new FileNotFoundException("The out-of-process WinForms designer host is not deployed.", hostDll);
			using var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;
			var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			var arguments = $"exec \"{hostDll}\" --port {port} --token {token}";
			if (File.Exists(runtimeConfigPath) && File.Exists(depsFilePath))
				arguments = $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsFilePath}\" \"{hostDll}\" --port {port} --token {token}";

			var startInfo = new ProcessStartInfo(FindDotnetHost(), arguments) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process.Start();
			TcpClient tcp = null;
			JsonRpc rpc = null;
			try {
				tcp = await listener.AcceptTcpClientAsync(cancellationToken).AsTask()
					.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
				var stream = tcp.GetStream();
				var handler = new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter());
				rpc = new JsonRpc(handler);
				rpc.StartListening();
				var client = new FormsDesignerHostClient(process, tcp, rpc, operationTimeout ?? TimeSpan.FromSeconds(30));
				_ = client.PumpAsync(process.StandardOutput);
				_ = client.PumpAsync(process.StandardError);
				var handshake = await rpc.InvokeWithParameterObjectAsync<HostHandshake>("initialize",
					new { token, protocolVersion = FormsDesignerProtocol.Version }, cancellationToken)
					.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
				if (handshake.ProtocolVersion != FormsDesignerProtocol.Version || handshake.ProcessId != process.Id)
					throw new InvalidDataException("The WinForms designer host returned an incompatible handshake.");
				return client;
			} catch {
				rpc?.Dispose();
				tcp?.Dispose();
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
				process.Dispose();
				throw;
			}
		}

		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);

		public Task<DesignerEditSet> FlushAsync(long version, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerEditSet>("session/flush", new { version }, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long version, int x, int y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { version, x, y }, cancellationToken);

		public Task<DesignerSessionState> SetPropertyAsync(long version, string componentName, string propertyName, string value, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-property", new { version, componentName, propertyName, value }, cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long version, string componentName, string propertyName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/reset-property", new { version, componentName, propertyName }, cancellationToken);

		public Task<DesignerSessionState> RenameComponentAsync(long version, string componentName, string newName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/rename-component", new { version, componentName, newName }, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long version, string componentName, string eventName, string handlerName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-event", new { version, componentName, eventName, handlerName }, cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/activate-default-event", new { version, componentName }, cancellationToken);

		public Task<DesignerSessionState> AddControlAsync(long version, string parentName, string controlType, string componentName, int x, int y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/add-control", new { version, parentName, controlType, componentName, x, y }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long version, string componentName, int x, int y, int width, int height, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-bounds", new { version, componentName, x, y, width, height }, cancellationToken);

		public Task<DesignerSessionState> DeleteComponentAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/delete-component", new { version, componentName }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long version, string componentName, bool bringToFront, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-z-order", new { version, componentName, bringToFront }, cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long version, string operation, string[] componentNames, CancellationToken cancellationToken,
			int deltaX = 0, int deltaY = 0)
			=> InvokeAsync<DesignerSessionState>("design/apply-layout", new { version, operation, componentNames, deltaX, deltaY }, cancellationToken);

		public void TerminateHost()
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
		}

		public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
			=> InvokeAsync<object>("diagnostics/delay", new { milliseconds }, cancellationToken, TimeSpan.FromMilliseconds(250));

		async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
		{
			if (!IsAlive)
				throw new IOException("The WinForms designer host is not running.");
			try {
				return await rpc.InvokeWithParameterObjectAsync<T>(method, arguments, cancellationToken)
					.WaitAsync(timeout ?? operationTimeout, cancellationToken).ConfigureAwait(false);
			} catch (TimeoutException) {
				TerminateHost();
				throw new TimeoutException($"The WinForms designer host did not complete '{method}' within {timeout ?? operationTimeout}.");
			}
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
			try { if (!process.HasExited) rpc.InvokeAsync("shutdown").Wait(TimeSpan.FromSeconds(3)); } catch { }
			try { rpc.Dispose(); } catch { }
			try { tcp.Dispose(); } catch { }
			try {
				if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
			} catch { }
			process.Dispose();
		}
	}
}
