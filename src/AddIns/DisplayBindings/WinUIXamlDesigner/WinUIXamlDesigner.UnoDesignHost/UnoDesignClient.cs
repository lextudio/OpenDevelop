using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Owns the out-of-process design host child: locates its deployed binary, spawns it
/// (same .NET host that is running this process), connects the authenticated loopback
/// control plane and speaks the common designer protocol. One instance per design surface.
/// The process lifecycle (spawn, token handshake, log pump, timeouts, shutdown) comes from
/// <see cref="DesignerHostProcessClient"/>; the Uno-specific method mapping lives here.
/// </summary>
public sealed class UnoDesignClient : DesignerHostProcessClient, IDesignHostClient
{
	readonly string runtimeConfigPath;
	readonly string depsFilePath;
	DesignerCapabilities capabilities;

	UnoDesignClient(string runtimeConfigPath, string depsFilePath, TimeSpan? operationTimeout = null)
		: base(operationTimeout)
	{
		this.runtimeConfigPath = runtimeConfigPath;
		this.depsFilePath = depsFilePath;
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
	/// Spawns the child, waits for it to connect back on a fresh loopback port and to
	/// complete the authenticated handshake, then returns a client that can speak the
	/// design protocol.
	/// </summary>
	/// <param name="runtimeConfigPath">The designed project's runtimeconfig.json, to run the
	/// child inside the project's own dependency graph (its real Uno version and assemblies).
	/// Null falls back to the child's own deployment.</param>
	/// <param name="depsFilePath">The designed project's deps.json, paired with the runtimeconfig.</param>
	public static async Task<UnoDesignClient> StartAsync(string runtimeConfigPath, string depsFilePath, CancellationToken cancellationToken)
	{
		var client = new UnoDesignClient(runtimeConfigPath, depsFilePath);
		await client.StartAsync(cancellationToken).ConfigureAwait(false);
		return client;
	}

	protected override string GetChildDllPath()
		=> LocateChildDll() ?? throw new FileNotFoundException("The Uno design host child is not deployed (AddIns/.../WinUIXamlDesigner/UnoHost/WinUIXamlDesigner.UnoHost.dll).");

	protected override string BuildCommandLine(string childDll, int port, string token)
	{
		var arguments = $"exec \"{childDll}\" --port {port} --token {token}";
		if (File.Exists(runtimeConfigPath) && File.Exists(depsFilePath))
		{
			// Run the child inside the designed project's dependency graph: its deps.json and
			// runtimeconfig.json make Uno and the project's own assemblies (custom controls,
			// converters, muxc types, the project's actual Uno version) resolve from the
			// project's bin. The child loads anything the project does not provide
			// (StreamJsonRpc) from its own deployment folder, and preloads the project's bin
			// assemblies so XamlReader can resolve their types.
			var appBin = Path.GetDirectoryName(runtimeConfigPath);
			arguments = $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsFilePath}\" \"{childDll}\" --port {port} --token {token} --appbin \"{appBin}\"";
		}
		return arguments;
	}

	/// <summary>Uno's headless boot (Application.Start + dispatcher install) is slow; the
	/// handshake must not give up while the child is still initializing.</summary>
	protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(60);

	/// <summary>
	/// Uno's initialize both authenticates and returns the runtime capabilities (one round
	/// trip). The child validates the shared token and protocol version before answering.
	/// </summary>
	protected override async Task OnConnectedAsync(JsonRpc rpc, string token, CancellationToken cancellationToken)
	{
		capabilities = await rpc.InvokeWithParameterObjectAsync<DesignerCapabilities>("initialize",
			new { token, protocolVersion = DesignerProtocol.Version }, cancellationToken)
			.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Capabilities collected during the handshake (runtime version + toolbox catalog).</summary>
	public Task<DesignerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(capabilities);

	public Task<DesignerSessionState> LoadDesignAsync(string xaml, double width, double height, double dpi, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/load",
			new { xaml, width, height, dpi }, cancellationToken);

	public Task<DesignerAppResourcesResult> LoadAppResourcesAsync(string xaml, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerAppResourcesResult>("app/resources",
			new { xaml }, cancellationToken);

	public Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/theme",
			new { theme }, cancellationToken);

	public Task<string> ExportPngAsync(string path, CancellationToken cancellationToken = default)
		=> InvokeAsync<string>("design/export-png",
			new { path }, cancellationToken);

	public Task<DesignerHitTestResult> HitTestAsync(double x, double y, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { x, y }, cancellationToken);

	/// <summary>True while the child process is running and not yet shut down.</summary>
	public bool IsProcessAlive => IsAlive;

	#region IDesignHostClient

	public Task PingAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("ping", null, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("shutdown", null, cancellationToken, TimeSpan.FromSeconds(3));

	#endregion
}
