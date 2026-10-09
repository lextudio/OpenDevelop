using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

sealed class GtkDesignerHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient, IDesignHostEventBinding, IDesignHostHitTesting, IDesignHostPropertyReset
{
	static readonly SharedDesignerHostBroker<GtkDesignerHostConnection> broker = new(
		connection => connection.IsAlive, StartConnectionAsync);
	static readonly object clientsGate = new();
	static readonly HashSet<GtkDesignerHostClient> clients = new();
	static readonly SharedDesignerHostRecovery<GtkDesignerHostClient, GtkDesignerHostConnection> recovery = new(
		broker, GetAffectedClients, client => client.RecoverySnapshot != null,
		(client, token) => client.CaptureRecoverySnapshotAsync(client.RecoverySnapshot!.Version, token),
		(client, replacement, token) => client.RestoreAsync(replacement, token),
		(client, exception) => client.OnRecoveryFailed(exception));

	GtkDesignerHostConnection connection;
	DesignerSessionState? recoveredState;
	bool disposed;

	public string PoolKey => "gtk4";
	public static int ActiveLeaseCount { get { lock (clientsGate) return clients.Count; } }
	public int RecoveryCount { get; private set; }
	public event EventHandler<DesignerSessionState>? Recovered;
	public event EventHandler<Exception>? RecoveryFailed;

	GtkDesignerHostClient(GtkDesignerHostConnection connection) : base(connection)
	{
		this.connection = connection;
		connection.HostExited += OnConnectionExited;
		lock (clientsGate) clients.Add(this);
	}

	public static async Task<GtkDesignerHostClient> CreateAsync(CancellationToken token = default)
	{
		return new GtkDesignerHostClient(await broker.AcquireAsync(token).ConfigureAwait(false));
	}

	static async Task<GtkDesignerHostConnection> StartConnectionAsync(CancellationToken token)
	{
		string? gtkBin = null;
		if (OperatingSystem.IsWindows() && (gtkBin = GtkRuntimeLocator.FindWindowsBinDirectory()) == null)
			throw new GtkRuntimeMissingException("The GTK 4 runtime (libgtk-4-1.dll) was not found, so the GTK designer cannot start.\n\n"
				+ GtkRuntimeLocator.InstallInstructions());
		var root = Path.GetDirectoryName(typeof(GtkDesignerHostClient).Assembly.Location)!;
		var hostDll = Path.Combine(root, "Host", "GtkDesigner.Host.dll");
		if (OperatingSystem.IsMacOS()) {
			var warning = GtkRuntimeLocator.PrepareMacOsLibraries(Path.GetDirectoryName(hostDll)!);
			if (warning.Length > 0)
				throw new GtkRuntimeMissingException("The GTK 4 runtime could not be located. " + warning);
		}
		var connection = new GtkDesignerHostConnection(hostDll, gtkBin);
		try {
			await connection.StartConnectionAsync(token).ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException && IsMissingGtk(ex)) {
			// Off Windows there is no pre-check (GTK comes from the system loader paths), so the
			// child's own DllNotFoundException is what reveals a missing runtime.
			throw new GtkRuntimeMissingException("The GTK 4 runtime could not be loaded, so the GTK designer cannot start.\n\n"
				+ GtkRuntimeLocator.InstallInstructions() + "\n\nHost output:\n" + ex.Message);
		}
		return connection;
	}

	static bool IsMissingGtk(Exception ex) => ex.ToString().Contains("DllNotFoundException", StringComparison.Ordinal)
		&& ex.ToString().Contains("gtk", StringComparison.OrdinalIgnoreCase);

	public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default, DesignerViewport? viewport = null)
	{
		return OpenRecoverableAsync(snapshot, token, viewport);
	}

	public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken token = default, DesignerViewport? viewport = null)
	{
		return UpdateRecoverableAsync(snapshot, token, viewport);
	}

	public Task<DesignerEditSet> FlushAsync(long version, CancellationToken token = default) => Document.FlushAsync(version, token);
	public Task<DesignerSessionState> SetPropertyAsync(long v, string id, string name, string value, CancellationToken token = default) => TrackMutationAsync(Document.SetPropertyAsync(v, id, name, value, token), token);
	public Task<DesignerSessionState> AddElementAsync(long v, string parent, DesignerToolboxItemInfo item, string name, double x, double y, DesignerDropTarget dropTarget = null, CancellationToken token = default) => TrackMutationAsync(Document.AddElementAsync(v, parent, item, name, x, y, dropTarget, token), token);
	public Task<DesignerSessionState> DeleteElementsAsync(long v, string[] ids, CancellationToken token = default) => TrackMutationAsync(Document.DeleteElementsAsync(v, ids, token), token);
	/// <summary>The companion wiring source and handler signatures for this document's signals.</summary>
	public Task<GtkSignalWiringResult> SignalWiringAsync(string uiFileName, string? namespaceName, string className, CancellationToken token = default)
		=> connection.InvokeAsync<GtkSignalWiringResult>("gtk/signal-wiring", new { sessionId = SessionId, documentId = DocumentId, uiFileName, namespaceName, className }, token);
	public Task<DesignerSessionState> ResetPropertyAsync(long v, string id, string name, CancellationToken token = default) => TrackMutationAsync(connection.ResetPropertyAsync(DocumentId, v, id, name, token), token);
	public Task<DesignerSessionState> RenameAsync(long v, string id, string name, CancellationToken token = default) => TrackMutationAsync(Document.RenameAsync(v, id, name, token), token);
	public Task<DesignerSessionState> UndoAsync(long v, CancellationToken token = default) => TrackMutationAsync(connection.UndoAsync(DocumentId, v, token), token);
	public Task<DesignerSessionState> RedoAsync(long v, CancellationToken token = default) => TrackMutationAsync(connection.RedoAsync(DocumentId, v, token), token);
	public Task<DesignerSessionState> SetEventAsync(long v, string id, string e, string h, CancellationToken t = default) => TrackMutationAsync(Document.SetEventAsync(v, id, e, h, t), t);
	public Task<DesignerSessionState> ReorderAsync(long v, string id, int delta, CancellationToken t = default) => TrackMutationAsync(connection.ReorderAsync(DocumentId, v, id, delta, t), t);
	public Task<DesignerHitTestResult> HitTestAsync(long v, double x, double y, CancellationToken t = default) => Document.HitTestAsync(v, x, y, t);
	public Task<DesignerSessionState> RenderAsync(long version, CancellationToken token = default) => connection.RenderAsync(DocumentId, version, token);

	public async Task<DesignerSessionState> RestartPoolAsync(CancellationToken token = default)
	{
		await recovery.RecoverAllAsync(connection, true, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered.");
	}
	public async Task<DesignerSessionState> TerminateAndRecoverAsync(CancellationToken token = default)
	{
		var failed = connection;
		lock (clientsGate) foreach (var client in clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed))) { client.recoveredState = null; failed.HostExited -= client.OnConnectionExited; }
		failed.TerminateHost();
		await recovery.RecoverAllAsync(failed, false, token).ConfigureAwait(false);
		return recoveredState ?? throw new IOException("GTK designer document was not recovered after host termination.");
	}

	static GtkDesignerHostClient[] GetAffectedClients(GtkDesignerHostConnection failed)
	{
		lock (clientsGate) return clients.Where(c => !c.disposed && ReferenceEquals(c.connection, failed)).ToArray();
	}

	async Task RestoreAsync(GtkDesignerHostConnection replacement, CancellationToken token)
	{
		connection.HostExited -= OnConnectionExited;
		connection = replacement;
		RebindConnection(replacement);
		replacement.HostExited += OnConnectionExited;
		recoveredState = await Document.OpenAsync(RecoverySnapshot!, RecoveryViewport, token).ConfigureAwait(false);
		RecoveryCount++;
		Recovered?.Invoke(this, recoveredState);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		connection.HostExited -= OnConnectionExited;
		DetachHostConnection();
		lock (clientsGate) clients.Remove(this);
		try { ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
		broker.Release(connection);
	}

	void OnConnectionExited(object? sender, EventArgs e)
	{
		_ = recovery.RecoverAllAsync(connection, false, CancellationToken.None);
	}

	void OnRecoveryFailed(Exception exception) => RecoveryFailed?.Invoke(this, exception);

	sealed class GtkDesignerHostConnection : DesignerHostProcessClient
	{
		readonly string hostDll;
		readonly string? gtkBin;
		public GtkDesignerHostConnection(string hostDll, string? gtkBin) { this.hostDll = hostDll; this.gtkBin = gtkBin; }
		public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
		protected override string GetChildDllPath() => hostDll;
		protected override void ConfigureChildProcess(ProcessStartInfo startInfo)
		{
			// Gir.Core resolves libgtk-4-1.dll and its dependencies through the normal DLL search,
			// so the located GTK bin folder goes first on the child's PATH - without touching the
			// user's own PATH.
			if (OperatingSystem.IsWindows() && gtkBin != null) {
				var existing = startInfo.Environment.TryGetValue("PATH", out var path) ? path : null;
				startInfo.Environment["PATH"] = string.IsNullOrEmpty(existing) ? gtkBin : gtkBin + Path.PathSeparator + existing;
			}
			if (OperatingSystem.IsMacOS()) {
				// Homebrew marks gtk4 and libadwaita keg-only (macOS ships GTK3), so their dylibs land in
				// <prefix>/opt/<formula>/lib and are never linked into <prefix>/lib. Pointing dyld at
				// <prefix>/lib alone therefore finds nothing, and the host still dies with
				// DllNotFoundException even after a perfectly successful `brew install gtk4` - so the opt
				// directories have to be named explicitly. DYLD_FALLBACK_LIBRARY_PATH is set alongside
				// DYLD_LIBRARY_PATH because libadwaita loads gtk4's own dependencies, and those go
				// through the fallback search.
				var directories = new List<string>();
				foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
					foreach (var candidate in new[] { "lib", "opt/gtk4/lib", "opt/libadwaita/lib", "opt/glib/lib", "opt/pango/lib", "opt/cairo/lib" }) {
						var full = Path.Combine(prefix, candidate);
						if (Directory.Exists(full) && !directories.Contains(full))
							directories.Add(full);
					}
				if (directories.Count > 0) {
					var libraries = string.Join(Path.PathSeparator, directories);
					foreach (var name in new[] { "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" }) {
						var existing = startInfo.Environment.TryGetValue(name, out var value) ? value : null;
						startInfo.Environment[name] = string.IsNullOrEmpty(existing)
							? libraries
							: libraries + Path.PathSeparator + existing;
					}
				}
				startInfo.Environment["LSUIElement"] = "1";
				startInfo.Environment["LSBackgroundOnly"] = "1";
			}
		}
		public Task<DesignerSessionState> UndoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/undo", new { sessionId = SessionId, documentId, baseVersion = v }, token);
		public Task<DesignerSessionState> RedoAsync(string documentId, long v, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/redo", new { sessionId = SessionId, documentId, baseVersion = v }, token);
		public Task<DesignerSessionState> ReorderAsync(string documentId, long v, string id, int delta, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reorder", new { sessionId = SessionId, documentId, baseVersion = v, elementId = id, delta }, t);
		public Task<DesignerSessionState> ResetPropertyAsync(string documentId, long v, string id, string name, CancellationToken t) => InvokeAsync<DesignerSessionState>("design/reset-property", new { sessionId = SessionId, documentId, baseVersion = v, elementId = id, propertyName = name }, t);
		public Task<DesignerSessionState> RenderAsync(string documentId, long version, CancellationToken token) => InvokeAsync<DesignerSessionState>("design/render", new { sessionId = SessionId, documentId, baseVersion = version }, token);
	}
}
