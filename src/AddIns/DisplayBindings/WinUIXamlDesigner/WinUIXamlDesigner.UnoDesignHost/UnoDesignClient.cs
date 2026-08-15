using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Owns the out-of-process design host child: locates its deployed binary, spawns it
/// (same .NET host that is running this process), connects the loopback TCP pipe and
/// speaks the JSON-RPC design protocol. One instance per design surface.
/// </summary>
public sealed class UnoDesignClient : IDisposable
{
	readonly Process process;
	TcpClient tcp;
	JsonRpc rpc;
	readonly StringBuilder childLog = new();
	volatile bool shuttingDown;

	UnoDesignClient(Process process)
	{
		this.process = process;
	}

	void Attach(TcpClient tcp, JsonRpc rpc)
	{
		// The single instance is constructed with the process (so PumpAsync can collect the
		// child's stdout/stderr into childLog before RPC is even possible) and receives the
		// connected pipe afterwards - one instance, one ChildLog, no lost lines.
		this.tcp = tcp;
		this.rpc = rpc;
	}

	/// <summary>Path of the deployed child binary, or null when the addin tree lacks it.</summary>
	public static string? LocateChildDll()
	{
		var dir = Path.GetDirectoryName(typeof(UnoDesignClient).Assembly.Location);
		if (string.IsNullOrEmpty(dir))
			return null;
		var candidate = Path.Combine(dir, "UnoHost", "WinUIXamlDesigner.UnoHost.dll");
		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// Last lines of the child's stdout/stderr, for diagnosing startup or render failures
	/// without restarting the app.
	/// </summary>
	public string ChildLog
	{
		get
		{
			lock (childLog)
				return childLog.ToString();
		}
	}

	/// <summary>
	/// Spawns the child, waits for it to connect back on a fresh loopback port and to
	/// signal readiness, then returns a client that can speak the design protocol.
	/// </summary>
	/// <param name="runtimeConfigPath">The designed project's runtimeconfig.json, to run the
	/// child inside the project's own dependency graph (its real Uno version and assemblies).
	/// Null falls back to the child's own deployment.</param>
	/// <param name="depsFilePath">The designed project's deps.json, paired with the runtimeconfig.</param>
	public static async Task<UnoDesignClient> StartAsync(string runtimeConfigPath, string depsFilePath, CancellationToken cancellationToken)
	{
		var hostDll = LocateChildDll()
			?? throw new FileNotFoundException("The Uno design host child is not deployed (AddIns/.../WinUIXamlDesigner/UnoHost/WinUIXamlDesigner.UnoHost.dll).");

		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;

		var arguments = $"exec \"{hostDll}\" --port {port}";
		var useProjectContext = File.Exists(runtimeConfigPath) && File.Exists(depsFilePath);
		if (useProjectContext)
		{
			// Run the child inside the designed project's dependency graph: its deps.json and
			// runtimeconfig.json make Uno and the project's own assemblies (custom controls,
			// converters, muxc types, the project's actual Uno version) resolve from the
			// project's bin. The child loads anything the project does not provide
			// (StreamJsonRpc) from its own deployment folder, and preloads the project's bin
			// assemblies so XamlReader can resolve their types.
			var appBin = Path.GetDirectoryName(runtimeConfigPath);
			arguments = $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsFilePath}\" \"{hostDll}\" --port {port} --appbin \"{appBin}\"";
		}
		var startInfo = new ProcessStartInfo(FindDotnetHost(), arguments) {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
		var client = new UnoDesignClient(process);
		process.Start();

		var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = client.PumpAsync(process.StandardOutput, process.StandardError, ready);

		TcpClient? tcp = null;
		try
		{
			var accept = await listener.AcceptTcpClientAsync(cancellationToken).AsTask();
			tcp = accept;
			// Readiness is the child's "ready on" line on stderr - RPC before that would
			// queue on the not-yet-pumped dispatcher and time out.
			await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

			var stream = tcp.GetStream();
			var formatter = new SystemTextJsonFormatter();
			var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
			var rpc = new JsonRpc(handler);
			rpc.StartListening();
			client.Attach(tcp, rpc);
			return client;
		}
		catch
		{
			process.Kill(entireProcessTree: true);
			client.Dispose();
			tcp?.Dispose();
			listener.Stop();
			throw;
		}
	}

	async Task PumpAsync(StreamReader stdout, StreamReader stderr, TaskCompletionSource<bool> ready)
	{
		async Task Pump(StreamReader reader, bool signalReady)
		{
			try
			{
				while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
				{
					lock (childLog)
						childLog.AppendLine(line);
					if (signalReady && line.Contains("ready on", StringComparison.Ordinal))
						ready.TrySetResult(true);
				}
			}
			catch
			{
				// Child output ending or a redirect fault is not fatal here.
			}
		}
		await Task.WhenAll(Pump(stdout, signalReady: false), Pump(stderr, signalReady: true)).ConfigureAwait(false);
		if (!ready.Task.IsCompleted)
			ready.TrySetException(new IOException("The design host child exited before becoming ready."));
	}

	/// <summary>
	/// The .NET host to spawn the child with. Reusing the current process's own dotnet
	/// guarantees the child gets the same runtime family this app itself runs on - the
	/// same-version/same-platform rule the out-of-process design host depends on.
	/// </summary>
	static string FindDotnetHost()
	{
		var candidate = Process.GetCurrentProcess().MainModule?.FileName;
		if (candidate != null && Path.GetFileName(candidate).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
			return candidate;
		if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
			return Path.Combine(root, "dotnet");
		return "dotnet";
	}

	public Task<DesignCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<DesignCapabilities>("initialize", null, cancellationToken);

	public Task<DesignSnapshot> LoadDesignAsync(string xaml, double width, double height, double dpi, CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<DesignSnapshot>("design/load",
			new { xaml, width, height, dpi }, cancellationToken);

	public Task<AppResourcesResult> LoadAppResourcesAsync(string xaml, CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<AppResourcesResult>("app/resources",
			new { xaml }, cancellationToken);

	public Task<DesignSnapshot> SetThemeAsync(string theme, CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<DesignSnapshot>("design/theme",
			new { theme }, cancellationToken);

	public Task<string> ExportPngAsync(string path, CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<string>("design/export-png",
			new { path }, cancellationToken);

	public Task<HitTestResult> HitTestAsync(double x, double y, CancellationToken cancellationToken = default)
		=> rpc.InvokeWithParameterObjectAsync<HitTestResult>("design/hit-test", new { x, y }, cancellationToken);

	/// <summary>True while the child process is running and not yet shut down.</summary>
	public bool IsProcessAlive => !shuttingDown && !process.HasExited;

	public void Dispose()
	{
		if (shuttingDown)
			return;
		shuttingDown = true;
		try
		{
			rpc.InvokeAsync("shutdown").GetAwaiter().GetResult();
		}
		catch
		{
			// The child may already be gone; killing below is the backstop.
		}
		try
		{
			rpc.Dispose();
		}
		catch
		{
		}
		try
		{
			if (!process.WaitForExit(5000))
				process.Kill(entireProcessTree: true);
		}
		catch
		{
		}
		try
		{
			tcp.Dispose();
		}
		catch
		{
		}
	}
}
