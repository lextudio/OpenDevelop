using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.SurfaceHost;
using StreamJsonRpc;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Child;

/// <summary>
/// The DDP method map for the Surface backend. Same method names and signatures as the other WinUI
/// children (UnoHost/DesignRpc.cs); only the render and document methods do work, because a Surface
/// renders and has no document model. Every edit is refused as an ordinary result, so the parent keeps
/// applying edits to its own source model and the shell never sees an RPC failure for them.
/// </summary>
sealed class SurfaceDesignRpc
{
	public const string Runtime = "WinUI.Surface";
	static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

	readonly string? surfaceExe;
	readonly string surfaceArguments;
	readonly string? hostDirectory;
	readonly string? runRoot;
	readonly DesignerDocumentRegistry<SurfaceDesignDocument> documents = new();
	readonly ManualResetEventSlim shutdown = new();

	/// <summary>
	/// Either <paramref name="surfaceExe"/> (launched as is, for the bundled host and tests), or
	/// <paramref name="hostDirectory"/> plus <paramref name="runRoot"/>: the pristine directory of a version-matched host
	/// (a provisioner cache entry), of which every document gets its own copy under the run root because a surface
	/// overwrites its own Surface.pri when it starts. <paramref name="surfaceArguments"/> is appended either way.
	/// </summary>
	public SurfaceDesignRpc(string? surfaceExe, string surfaceArguments, string? hostDirectory = null, string? runRoot = null)
	{
		if (string.IsNullOrWhiteSpace(surfaceExe) == string.IsNullOrWhiteSpace(hostDirectory))
			throw new ArgumentException("Pass exactly one of the surface exe or the matched host directory.");
		if (!string.IsNullOrWhiteSpace(hostDirectory) && string.IsNullOrWhiteSpace(runRoot))
			throw new ArgumentException("A matched host directory needs a run root.");
		this.surfaceExe = surfaceExe;
		this.surfaceArguments = surfaceArguments;
		this.hostDirectory = hostDirectory;
		if (runRoot != null) {
			SweepStaleRunDirectories(runRoot);
			// Each child keeps its run copies under its own process id, so a later child can tell a dead one's leftovers
			// from a live sibling's (several IDE instances, or a pooled child per project, share one run root).
			this.runRoot = Path.Combine(runRoot, Environment.ProcessId.ToString());
		}
	}

	/// <summary>
	/// Removes the per-process folders of children that no longer exist (an IDE killed before its children could close
	/// their documents). A folder is only removed when its name is a process id and no such process is running.
	/// </summary>
	static void SweepStaleRunDirectories(string runRoot)
	{
		if (!Directory.Exists(runRoot)) return;
		foreach (string directory in Directory.GetDirectories(runRoot)) {
			if (!int.TryParse(Path.GetFileName(directory), out int pid) || pid == Environment.ProcessId || IsRunning(pid))
				continue;
			DeleteRun(directory);
		}
	}

	static bool IsRunning(int pid)
	{
		try {
			using var process = System.Diagnostics.Process.GetProcessById(pid);
			return !process.HasExited;
		} catch (ArgumentException) {
			return false;
		}
	}

	public void WaitForShutdown() => shutdown.Wait();

	public void RequestShutdown() => shutdown.Set();

	public void CloseAll()
	{
		documents.CloseAll(Dispose);
		// Every document's copy is gone by now; drop this child's own (empty) folder too.
		if (runRoot != null) DeleteRun(runRoot);
	}

	public void Register(JsonRpc rpc, string expectedToken)
	{
		rpc.AddLocalRpcMethod("initialize", new Func<string, int, string, HostHandshake>((token, protocolVersion, sessionId) => Initialize(expectedToken, token, protocolVersion, sessionId)));
		rpc.AddLocalRpcMethod("session/open", new Func<DesignerDocumentRequest, DesignerSessionState>(Open));
		rpc.AddLocalRpcMethod("session/update", new Func<DesignerDocumentRequest, DesignerSessionState>(Update));
		rpc.AddLocalRpcMethod("session/flush", new Func<string, string, long, DesignerEditSet>(Flush));
		rpc.AddLocalRpcMethod("session/close", new Action<string, string>(Close));
		rpc.AddLocalRpcMethod("design/set-property", new Func<string, string, long, string, string, string, DesignerSessionState>((s, d, v, e, p, val) => Refuse(s, d, "design/set-property")));
		rpc.AddLocalRpcMethod("design/set-event", new Func<string, string, long, string, string, string, DesignerSessionState>((s, d, v, e, ev, h) => Refuse(s, d, "design/set-event")));
		rpc.AddLocalRpcMethod("design/add-element", new Func<string, string, long, string, DesignerToolboxItemInfo, string, double, double, DesignerDropTarget, DesignerSessionState>((s, d, v, p, i, n, x, y, t) => Refuse(s, d, "design/add-element")));
		rpc.AddLocalRpcMethod("design/set-bounds", new Func<string, string, long, string, double, double, double, double, DesignerSessionState>((s, d, v, e, x, y, w, h) => Refuse(s, d, "design/set-bounds")));
		rpc.AddLocalRpcMethod("design/delete-elements", new Func<string, string, long, string[], DesignerSessionState>((s, d, v, e) => Refuse(s, d, "design/delete-elements")));
		rpc.AddLocalRpcMethod("design/rename", new Func<string, string, long, string, string, DesignerSessionState>((s, d, v, e, n) => Refuse(s, d, "design/rename")));
		// The surface takes its theme from SURFACE_THEME at launch (SetTheme is a logged no-op upstream), so a
		// theme change needs a relaunch; until the adapter does that it is refused rather than pretended.
		rpc.AddLocalRpcMethod("design/theme", new Func<string, string, long, string, DesignerSessionState>((s, d, v, t) => Refuse(s, d, "design/theme")));
		rpc.AddLocalRpcMethod("design/go-to-state", new Func<string, string, long, string, string, DesignerSessionState>((s, d, v, g, st) => Refuse(s, d, "design/go-to-state")));
		rpc.AddLocalRpcMethod("app/resources", new Func<string, string, string, DesignerAppResourcesResult>(AppResources));
		rpc.AddLocalRpcMethod("design/hit-test", new Func<string, string, long, double, double, DesignerHitTestResult>(HitTest));
		rpc.AddLocalRpcMethod("design/export-png", new Func<string, string, string, string>(ExportPng));
		rpc.AddLocalRpcMethod("ping", new Action(() => { }));
		rpc.AddLocalRpcMethod("shutdown", new Action(() => {
			CloseAll();
			// Releasing Main from inside the handler lets the process exit before the reply is flushed, which the
			// parent sees as a lost connection on a graceful shutdown. Let the reply go out first.
			_ = Task.Run(async () => { await Task.Delay(200); RequestShutdown(); });
		}));
	}

	HostHandshake Initialize(string expectedToken, string token, int protocolVersion, string sessionId)
	{
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		documents.Initialize(sessionId);
		return new HostHandshake {
			ProtocolVersion = DesignerProtocol.Version,
			Runtime = Runtime,
			ProcessId = Environment.ProcessId,
			SessionId = sessionId,
			Capabilities = new DesignerCapabilities { Runtime = Runtime, SessionId = sessionId },
		};
	}

	DesignerSessionState Open(DesignerDocumentRequest request)
	{
		var snapshot = request.Snapshot;
		var document = documents.GetOrAdd(snapshot.SessionId, snapshot.DocumentId, () => Launch(snapshot));
		return Guard("session/open", () => document.OpenAsync(request).GetAwaiter().GetResult());
	}

	DesignerSessionState Update(DesignerDocumentRequest request)
	{
		var snapshot = request.Snapshot;
		var document = documents.Get(snapshot.SessionId, snapshot.DocumentId);
		return Guard("session/update", () => document.UpdateAsync(request).GetAwaiter().GetResult());
	}

	SurfaceDesignDocument Launch(DesignerDocumentSnapshot snapshot)
	{
		string exe = surfaceExe ?? "";
		string arguments = surfaceArguments;
		string? run = null;
		if (hostDirectory != null) {
			run = SurfaceLaunch.PrepareRunCopy(hostDirectory, runRoot!);
			exe = Path.Combine(run, "Surface.exe");
			arguments = (SurfaceLaunch.MatchedHostArguments(run) + " " + surfaceArguments).Trim();
		}
		Console.Error.WriteLine($"{Runtime}: starting surface for {snapshot.DocumentId}: {exe} {arguments}");
		try {
			var host = SurfaceProcessHost.StartAsync(exe, arguments, StartTimeout).GetAwaiter().GetResult();
			Console.Error.WriteLine($"{Runtime}: surface ready (WASDK {host.Connection.Ready.Wasdk})");
			return new SurfaceDesignDocument(host, run == null ? null : () => DeleteRun(run)) {
				// The element tree costs a native-mode excursion per accepted render; OD_SURFACE_TREE=0 turns it off.
				BuildTree = Environment.GetEnvironmentVariable("OD_SURFACE_TREE") != "0",
			};
		} catch {
			DeleteRun(run);
			throw;
		}
	}

	/// <summary>Removes a document's private run copy; a file the OS still holds just stays until the next cleanup.</summary>
	static void DeleteRun(string? run)
	{
		if (run == null) return;
		// A just-killed surface can still hold a file for a moment after its process has exited.
		for (int attempt = 1; ; attempt++) {
			try {
				Directory.Delete(run, true);
				return;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				if (attempt == 10) {
					Console.Error.WriteLine($"{Runtime}: could not remove {run}: {e.Message}");
					return;
				}
				Thread.Sleep(300);
			}
		}
	}

	/// <summary>
	/// A surface that dies or stops answering is reported as a rejected state carrying its stderr tail, not as a
	/// transport failure: the shell shows it in the designer and can offer a reload, and the child stays up.
	/// </summary>
	DesignerSessionState Guard(string method, Func<DesignerSessionState> render)
	{
		try {
			return render();
		} catch (Exception e) when (e is SurfaceConnectionFaultedException or SurfaceProtocolException or IOException or TimeoutException) {
			Console.Error.WriteLine($"{Runtime}: {method} failed: {e}");
			return new DesignerSessionState {
				Accepted = false,
				Error = "The WinUI surface stopped responding: " + (e.InnerException?.Message ?? e.Message),
			};
		}
	}

	static DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
		// Nothing is held back: the parent's source model is the only document model.
		=> new() { SessionId = sessionId, DocumentId = documentId, BaseVersion = baseVersion };

	void Close(string sessionId, string documentId) => documents.Remove(sessionId, documentId, Dispose);

	DesignerSessionState Refuse(string sessionId, string documentId, string operation)
		=> documents.Get(sessionId, documentId).Refuse(sessionId, documentId, operation);

	static DesignerAppResourcesResult AppResources(string sessionId, string documentId, string xaml)
		// A surface merges the project's resources itself (from the matched host and --user-dll), so there is
		// nothing for the parent to deliver; saying so explicitly keeps the app/resources step from failing.
		=> new() { Success = true };

	/// <summary>
	/// A click on the canvas, answered by the surface's own PickAt. Only a named element is a hit (see
	/// <see cref="SurfaceDesignDocument.HitTestAsync"/>); a lost connection is a miss, not an RPC failure, so a click never errors.
	/// </summary>
	DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y)
	{
		var document = documents.Get(sessionId, documentId);
		try {
			return document.HitTestAsync(x, y).GetAwaiter().GetResult();
		} catch (SurfaceConnectionFaultedException e) {
			Console.Error.WriteLine($"{Runtime}: design/hit-test failed: {e.Message}");
			return new DesignerHitTestResult { Hit = false };
		}
	}

	string ExportPng(string sessionId, string documentId, string path)
		=> documents.Get(sessionId, documentId).ExportPng(path);

	static void Dispose(SurfaceDesignDocument document)
	{
		try {
			document.DisposeAsync().AsTask().GetAwaiter().GetResult();
		} catch (Exception e) {
			Console.Error.WriteLine($"{Runtime}: closing a surface failed: {e.Message}");
		}
	}
}
