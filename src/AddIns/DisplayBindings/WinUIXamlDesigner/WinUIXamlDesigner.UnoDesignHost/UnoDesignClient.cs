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
public sealed class UnoDesignClient : DesignerHostProcessClient, IDesignHostClient, IDesignHostTheme, IDesignHostExport, IDesignHostAppResources
{
	readonly string runtimeConfigPath;
	readonly string depsFilePath;
	readonly string? hostDllPath;
	DesignerCapabilities capabilities;

	/// <summary>Identifies the single document this client opens against its child. One
	/// process/one document per host today (designer-common.md's starting point); stable for
	/// the client's life.</summary>
	public string DocumentId { get; } = Guid.NewGuid().ToString("N");

	UnoDesignClient(string runtimeConfigPath, string depsFilePath, string? hostDllPath, TimeSpan? operationTimeout = null)
		: base(operationTimeout)
	{
		this.runtimeConfigPath = runtimeConfigPath;
		this.depsFilePath = depsFilePath;
		this.hostDllPath = hostDllPath;
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
	/// <param name="hostDllPath">Explicit path to the child host dll, overriding the production
	/// deployment lookup. Null falls back to <see cref="LocateChildDll"/> - used by tests that
	/// point directly at a build-output copy of the child.</param>
	public static async Task<UnoDesignClient> StartAsync(string runtimeConfigPath, string depsFilePath, CancellationToken cancellationToken, string? hostDllPath = null)
	{
		var client = new UnoDesignClient(runtimeConfigPath, depsFilePath, hostDllPath);
		await client.StartAsync(cancellationToken).ConfigureAwait(false);
		return client;
	}

	protected override string GetChildDllPath()
		=> hostDllPath ?? LocateChildDll() ?? throw new FileNotFoundException("The Uno design host child is not deployed (AddIns/.../WinUIXamlDesigner/UnoHost/WinUIXamlDesigner.UnoHost.dll).");

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
			new { token, protocolVersion = DesignerProtocol.Version, sessionId = SessionId }, cancellationToken)
			.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
		if (capabilities.SessionId != SessionId)
			throw new InvalidOperationException("The design host echoed an unexpected session id during handshake.");
	}

	/// <summary>Capabilities collected during the handshake (runtime version + toolbox catalog).</summary>
	public Task<DesignerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
		=> Task.FromResult(capabilities);

	// Viewport (surface size and DPI) is presentation state, deliberately kept out of the
	// document snapshot: the DDP document/model protocol must not carry presenter-specific
	// state. The presenter pushes it here with SetViewport and the client folds it into the
	// session/open|update wire object the Uno child expects.
	double viewportWidth = 1280;
	double viewportHeight = 720;
	double viewportDpi = 1.0;

	/// <summary>Sets the surface size/DPI used by the next open/update (not an RPC).</summary>
	public void SetViewport(double width, double height, double dpi)
	{
		viewportWidth = width;
		viewportHeight = height;
		viewportDpi = dpi;
	}

	/// <summary>Picks the document text out of a snapshot: the primary file when named,
	/// else the first source file, else whatever the snapshot carries.</summary>
	static string PrimaryText(DesignerDocumentSnapshot snapshot)
	{
		if (snapshot?.Files == null || snapshot.Files.Count == 0)
			return "";
		DesignerSourceFileSnapshot file = null;
		if (!string.IsNullOrEmpty(snapshot.PrimaryFileName))
			file = snapshot.Files.Find(f => string.Equals(f.FileName, snapshot.PrimaryFileName, StringComparison.OrdinalIgnoreCase));
		file ??= snapshot.Files.Find(f => f.Kind == "Source") ?? snapshot.Files[0];
		return file.Text ?? "";
	}

	/// <summary>First load for a session (session/open) - the initial render of a document.</summary>
	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("session/open",
			new { sessionId = SessionId, documentId = DocumentId, xaml = PrimaryText(snapshot), width = viewportWidth, height = viewportHeight, dpi = viewportDpi }, cancellationToken);

	/// <summary>Subsequent full-document push for an already-open session (session/update) -
	/// theme reloads, size-preset changes and any other full re-render after the first load.</summary>
	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("session/update",
			new { sessionId = SessionId, documentId = DocumentId, xaml = PrimaryText(snapshot), width = viewportWidth, height = viewportHeight, dpi = viewportDpi, baseVersion = snapshot?.Version ?? 0 }, cancellationToken);

	/// <summary>Stub: this host holds no independent child-side edit buffer, so this reports
	/// the current XAML as the sole file - lands the wire shape now.</summary>
	public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerEditSet>("session/flush",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion }, cancellationToken);

	/// <summary>Applies a single property change directly to the live element and re-renders,
	/// without re-running the full XAML parse/load path.</summary>
	public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/set-property",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, value }, cancellationToken);

	/// <summary>Validates the element/event names exist; no live code-behind instance exists in
	/// this design host, so no real wiring happens yet.</summary>
	public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/set-event",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, eventName, handlerName }, cancellationToken);

	/// <summary>Parses the toolbox item's XAML template and inserts it as a child of the named
	/// parent element, then re-renders without re-running the full document XAML parse.
	/// <paramref name="proposedName"/> is ignored: this markup backend derives the element name
	/// from the parsed XAML (which already carries x:Name).</summary>
	public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/add-element",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, x, y }, cancellationToken);

	/// <summary>Sets an element's width/height directly, and its Canvas position when its
	/// parent is a Canvas, then re-renders.</summary>
	public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/set-bounds",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x, y, width, height }, cancellationToken);

	/// <summary>Removes each named element from its Panel parent, then re-renders.</summary>
	public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/delete-elements",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds }, cancellationToken);

	/// <summary>Renames the live element, then re-renders.</summary>
	public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/rename",
			new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, newName }, cancellationToken);

	public Task<DesignerAppResourcesResult> SetAppResourcesAsync(string xaml, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerAppResourcesResult>("app/resources",
			new { xaml }, cancellationToken);

	public Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerSessionState>("design/theme",
			new { theme }, cancellationToken);

	public Task<string> ExportPngAsync(string path, CancellationToken cancellationToken = default)
		=> InvokeAsync<string>("design/export-png",
			new { path }, cancellationToken);

	/// <summary>Maps a surface point to the element chain under it. <paramref name="baseVersion"/>
	/// is accepted for protocol uniformity but not sent: the Uno child validates session/document
	/// identity on this call, not a per-call document version.</summary>
	public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
		=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, x, y }, cancellationToken);

	/// <summary>True while the child process is running and not yet shut down.</summary>
	public bool IsProcessAlive => IsAlive;

	#region IDesignHostClient

	public Task PingAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("ping", null, cancellationToken);

	public Task ShutdownAsync(CancellationToken cancellationToken = default)
		=> InvokeAsync<object>("shutdown", null, cancellationToken, TimeSpan.FromSeconds(3));

	#endregion
}
