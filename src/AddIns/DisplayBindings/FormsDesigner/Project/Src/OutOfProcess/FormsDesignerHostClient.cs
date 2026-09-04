using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>The WinForms implementation required by a design project.</summary>
	public enum FormsDesignerBackend
	{
		LibreWinForms,
		MicrosoftWinForms
	}

	/// <summary>Owns one isolated WinForms designer child process.</summary>
	public sealed class FormsDesignerHostClient : RecoverableDesignerDocumentHostClient, IDesignHostClient,
		IDesignHostPropertyReset, IDesignHostEventBinding, IDesignHostBounds, IDesignHostHitTesting,
		IDesignHostDefaultEvent, IDesignHostLayout
	{
		static readonly SharedDesignerHostPool<CompatibilityKey, Connection> sharedPool = new(
			(_, connection) => connection.IsAlive,
			async (key, token) => {
				var connection = new Connection(key.RuntimeConfigPath, key.DepsFilePath, key.HostDllPath, key.OperationTimeout);
				await connection.StartConnectionAsync(token).ConfigureAwait(false);
				return connection;
			});
		static readonly object clientsGate = new();
		static readonly HashSet<FormsDesignerHostClient> clients = new();
		static readonly Dictionary<CompatibilityKey, SharedDesignerHostRecovery<FormsDesignerHostClient, Connection>> recoveries = new();
		Connection connection;
		readonly CompatibilityKey poolKey;
		readonly bool shared;
		bool disposed;

		FormsDesignerHostClient(Connection connection, CompatibilityKey poolKey, bool shared) : base(connection)
		{
			this.connection = connection;
			this.poolKey = poolKey;
			this.shared = shared;
			if (shared) {
				connection.HostExited += OnConnectionExited;
				lock (clientsGate) clients.Add(this);
			}
		}

		public int RecoveryCount { get; private set; }
		public event EventHandler<DesignerSessionState>? Recovered;
		/// <summary>Raised when this document cannot be reopened while sibling documents recover.</summary>
		public event EventHandler<Exception>? RecoveryFailed;

		/// <summary>
		/// Selects the child runtime from the evaluated project property. A process-wide
		/// override is retained solely for diagnostics and explicit test runs; it must not
		/// decide the normal project route because Microsoft and Libre projects can be open
		/// in the same IDE process.
		/// </summary>
		/// <param name="targetFramework">
		/// The project's evaluated TargetFramework (e.g. "net9.0-windows10.0.17763.0"), used only
		/// when neither an explicit override nor <paramref name="useMicrosoftDesktopRuntime"/> picks
		/// a backend. Per doc/technotes/winforms-designer.md ("host selection is explicit by target
		/// framework and platform"), LibreWinForms exists so WinForms design still works on macOS,
		/// where no real System.Windows.Forms implementation exists at all - it was never meant to be
		/// the default on Windows. A project whose TFM targets Windows specifically is an ordinary
		/// desktop app - virtually every real-world WinForms project a user opens, including ones
		/// with no idea OpenDevelop's bespoke UseMicrosoftDesktopRuntime property exists - and should
		/// use the real, already-installed Microsoft WinForms rather than the portable fork.
		/// </param>
		public static FormsDesignerBackend ResolveBackend(string useMicrosoftDesktopRuntime, string runtimeOverride = null, string targetFramework = null)
		{
			var selectedOverride = runtimeOverride ?? Environment.GetEnvironmentVariable("OD_FORMS_RUNTIME");
			if (string.Equals(selectedOverride, "microsoft", StringComparison.OrdinalIgnoreCase))
				return FormsDesignerBackend.MicrosoftWinForms;
			if (string.Equals(selectedOverride, "libre", StringComparison.OrdinalIgnoreCase))
				return FormsDesignerBackend.LibreWinForms;
			if (bool.TryParse(useMicrosoftDesktopRuntime, out var useMicrosoft))
				return useMicrosoft ? FormsDesignerBackend.MicrosoftWinForms : FormsDesignerBackend.LibreWinForms;
			if (OperatingSystem.IsWindows() && TargetsWindowsPlatform(targetFramework))
				return FormsDesignerBackend.MicrosoftWinForms;
			return FormsDesignerBackend.LibreWinForms;
		}

		static bool TargetsWindowsPlatform(string targetFramework)
			=> !string.IsNullOrEmpty(targetFramework) &&
			   targetFramework.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) >= 0;

		public static string GetBackendName(FormsDesignerBackend backend)
			=> backend == FormsDesignerBackend.MicrosoftWinForms ? "WinForms" : "LibreWinForms";

		public static string LocateChildDll()
		{
			return LocateChildDll(ResolveBackend(""));
		}

		public static string LocateChildDll(FormsDesignerBackend backend)
		{
			var directory = Path.GetDirectoryName(typeof(FormsDesignerHostClient).Assembly.Location);
			if (String.IsNullOrEmpty(directory))
				return null;
			var useMicrosoft = backend == FormsDesignerBackend.MicrosoftWinForms;
			var path = useMicrosoft
				? Path.Combine(directory, "MicrosoftHost", "MicrosoftFormsDesigner.Host.dll")
				: Path.Combine(directory, "Host", "FormsDesigner.Host.dll");
			var resolved = File.Exists(path) ? path : null;
			WriteHostDiagnostic($"LocateChildDll backend={(useMicrosoft ? "microsoft" : "libre")} assemblyDirectory={directory} candidate={path} exists={resolved != null}");
			return resolved;
		}

		internal static void WriteHostDiagnostic(string message)
		{
			try {
				File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpenDevelop.FormsDesigner.host.log"),
					$"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
			} catch {
				// Diagnostics must never make the designer unavailable.
			}
		}

		public static string SelectedBackend => GetBackendName(ResolveBackend(""));

		public static async Task<FormsDesignerHostClient> StartAsync(
			string runtimeConfigPath,
			string depsFilePath,
			CancellationToken cancellationToken,
			string hostDllPath = null,
			TimeSpan? operationTimeout = null)
		{
			var connection = new Connection(runtimeConfigPath, depsFilePath, hostDllPath ?? LocateChildDll(), operationTimeout);
			await connection.StartConnectionAsync(cancellationToken).ConfigureAwait(false);
			return new FormsDesignerHostClient(connection, null, false);
		}

		public static async Task<FormsDesignerHostClient> AcquireSharedAsync(string runtimeConfigPath,
			string depsFilePath, CancellationToken cancellationToken, string hostDllPath = null,
			TimeSpan? operationTimeout = null)
		{
			var host = Path.GetFullPath(hostDllPath ?? LocateChildDll());
			var key = new CompatibilityKey(Normalize(runtimeConfigPath), Normalize(depsFilePath), host,
				operationTimeout ?? TimeSpan.FromSeconds(30), RuntimeInformation.ProcessArchitecture);
			return new FormsDesignerHostClient(await sharedPool.AcquireAsync(key, cancellationToken).ConfigureAwait(false), key, true);
		}

		static string Normalize(string path) => String.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);

		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> OpenRecoverableAsync(snapshot, cancellationToken);

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> UpdateRecoverableAsync(snapshot, cancellationToken);

		public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken)
			=> Document.FlushAsync(baseVersion, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x = Round(x), y = Round(y) }, cancellationToken);

		/// <summary>The WinForms child lays out in integer device units; design-unit coordinates
		/// round here so the wire contract stays unchanged.</summary>
		static int Round(double value) => (int)Math.Round(value);

		public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken)
			=> TrackMutationAsync(Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/reset-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName }, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken)
			=> TrackMutationAsync(Document.RenameAsync(baseVersion, elementId, newName, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken)
			=> TrackMutationAsync(Document.SetEventAsync(baseVersion, elementId, eventName, handlerName, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/activate-default-event", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken), cancellationToken);

		/// <summary>Inserts a control; the WinForms backend needs only the toolbox item's CLR type
		/// name plus the proposed component name.</summary>
		public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string elementId, double x, double y, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, elementId, x = Round(x), y = Round(y) }, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x = Round(x), y = Round(y), width = Round(width), height = Round(height) }, cancellationToken), cancellationToken);

		/// <summary>Deletes elements one RPC at a time: the child's <c>design/delete-elements</c>
		/// takes a single name, and deletes do not bump the document version, so every call in
		/// the loop validates against the same <paramref name="baseVersion"/>. The last child state wins.</summary>
		public async Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken)
		{
			DesignerSessionState state = null;
			if (elementIds != null) {
				foreach (var elementId in elementIds)
					state = await DeleteComponentAsync(baseVersion, elementId, cancellationToken).ConfigureAwait(false);
			}
			return state == null ? null : await TrackMutationAsync(Task.FromResult(state), cancellationToken).ConfigureAwait(false);
		}

		Task<DesignerSessionState> DeleteComponentAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long baseVersion, string elementId, bool bringToFront, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/set-z-order", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, bringToFront }, cancellationToken), cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long baseVersion, string operation, string[] elementIds, double deltaX, double deltaY, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/apply-layout", new { sessionId = SessionId, documentId = DocumentId, baseVersion, operation, elementIds, deltaX = Round(deltaX), deltaY = Round(deltaY) }, cancellationToken), cancellationToken);

		public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
			=> connection.InvokeAsync<object>("diagnostics/delay", new { milliseconds }, cancellationToken, TimeSpan.FromMilliseconds(250));

		/// <summary>Lists the smart-tag (DesignerActionList) items for a component - the popup
		/// shown from the chevron button VS draws at a selected component's top-right corner.
		/// Microsoft backend only; the Libre host returns <c>Accepted=false</c>.</summary>
		public Task<DesignerSmartTagActions> ListSmartTagActionsAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSmartTagActions>("design/list-smart-tag-actions", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		/// <summary>Invokes a <c>DesignerActionMethodItem</c> found by (listIndex, itemIndex) from
		/// the most recent <see cref="ListSmartTagActionsAsync"/> call for the same element.</summary>
		public Task<DesignerSessionState> InvokeSmartTagMethodAsync(long baseVersion, string elementId, int listIndex, int itemIndex, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/invoke-smart-tag-method", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, listIndex, itemIndex }, cancellationToken), cancellationToken);

		/// <summary>Creates and appends a new ToolStripItem to a ToolStrip/StatusStrip/MenuStrip
		/// (or to a submenu's DropDownItems when <paramref name="parentItemId"/> is non-empty) -
		/// the "insert new item" chevron VS draws past a selected strip's last item.</summary>
		public Task<DesignerSessionState> AddToolStripItemAsync(long baseVersion, string elementId, string itemTypeName, string parentItemId, string newItemId, CancellationToken cancellationToken)
			=> TrackMutationAsync(connection.InvokeAsync<DesignerSessionState>("design/add-toolstrip-item", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, itemTypeName, parentItemId = parentItemId ?? "", newItemId }, cancellationToken), cancellationToken);

		readonly Dictionary<string, string> typeIconCache = new(StringComparer.Ordinal);

		/// <summary>The real WinForms toolbox icon (16x16 PNG, base64) for a CLR type name - the
		/// same embedded-resource lookup (System.Drawing.ToolboxBitmapAttribute.GetImageFromResource)
		/// real Visual Studio's Toolbox/smart-tag/insert-item UI uses, not a VS chrome icon.
		/// Cached client-side per type name (in addition to the host's own per-session cache) so
		/// popups with many rows of the same handful of types (Button/Label/Separator/...) make
		/// one round trip per type, not one per row. Read-only - never goes through
		/// TrackMutationAsync/ExecuteRemoteEdit.</summary>
		public async Task<string> GetTypeIconAsync(string typeName, CancellationToken cancellationToken)
		{
			if (typeIconCache.TryGetValue(typeName, out var cached)) return cached;
			var result = await connection.InvokeAsync<DesignerTypeIconResult>("design/get-type-icon",
				new { sessionId = SessionId, documentId = DocumentId, typeName }, cancellationToken).ConfigureAwait(false);
			var png = result.Accepted ? result.PngBase64 : "";
			typeIconCache[typeName] = png;
			return png;
		}

		static SharedDesignerHostRecovery<FormsDesignerHostClient, Connection> RecoveryFor(CompatibilityKey key)
		{
			lock (clientsGate) {
				if (!recoveries.TryGetValue(key, out var recovery)) {
					recovery = new SharedDesignerHostRecovery<FormsDesignerHostClient, Connection>(
						sharedPool.GetBroker(key), failed => GetAffectedClients(key, failed),
						client => client.RecoverySnapshot != null,
						(client, token) => client.CaptureRecoverySnapshotAsync(client.RecoverySnapshot!.Version, token),
						(client, replacement, token) => client.RestoreAsync(replacement, token),
						(client, exception) => client.OnRecoveryFailed(exception));
					recoveries.Add(key, recovery);
				}
				return recovery;
			}
		}

		static FormsDesignerHostClient[] GetAffectedClients(CompatibilityKey key, Connection failed)
		{
			lock (clientsGate) return clients.Where(client => !client.disposed && Equals(client.poolKey, key)
				&& ReferenceEquals(client.connection, failed)).ToArray();
		}

		async Task RestoreAsync(Connection replacement, CancellationToken cancellationToken)
		{
			connection.HostExited -= OnConnectionExited;
			connection = replacement;
			RebindConnection(replacement);
			replacement.HostExited += OnConnectionExited;
			var state = await Document.OpenAsync(RecoverySnapshot!, cancellationToken).ConfigureAwait(false);
			RecoveryCount++;
			Recovered?.Invoke(this, state);
		}

		void OnConnectionExited(object? sender, EventArgs e)
		{
			if (shared && !disposed)
				_ = RecoveryFor(poolKey).RecoverAllAsync(connection, false, CancellationToken.None);
		}

		void OnRecoveryFailed(Exception exception)
		{
			WriteHostDiagnostic($"Document recovery failed document={DocumentId} exception={exception}");
			RecoveryFailed?.Invoke(this, exception);
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			if (shared) {
				connection.HostExited -= OnConnectionExited;
				DetachHostConnection();
				lock (clientsGate) clients.Remove(this);
			}
			try { ShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(3)); } catch { }
			if (shared) sharedPool.Release(poolKey, connection); else connection.Dispose();
		}

		sealed record CompatibilityKey(string RuntimeConfigPath, string DepsFilePath,
			string HostDllPath, TimeSpan OperationTimeout, Architecture Architecture);

		sealed class Connection : DesignerHostProcessClient
		{
			readonly string runtimeConfigPath;
			readonly string depsFilePath;
			readonly string hostDllPath;
			public Connection(string runtimeConfigPath, string depsFilePath, string hostDllPath, TimeSpan? operationTimeout) : base(operationTimeout)
			{
				this.runtimeConfigPath = runtimeConfigPath;
				this.depsFilePath = depsFilePath;
				this.hostDllPath = hostDllPath;
			}
			public async Task StartConnectionAsync(CancellationToken token)
			{
				WriteHostDiagnostic($"Starting child host dll={hostDllPath ?? "<null>"} runtimeconfig={runtimeConfigPath} deps={depsFilePath}");
				try {
					await StartAsync(token).ConfigureAwait(false);
					WriteHostDiagnostic($"Child host connected dll={hostDllPath ?? "<null>"}");
				} catch (Exception ex) {
					WriteHostDiagnostic($"Child host failed dll={hostDllPath ?? "<null>"} exception={ex}");
					throw;
				}
			}
			protected override string GetChildDllPath() => hostDllPath;
			protected override string BuildCommandLine(string childDll, int port, string token)
				=> new DesignerHostLaunchSpec { RuntimeConfigPath = runtimeConfigPath, DepsFilePath = depsFilePath }
					.BuildCommandLine(childDll, port, token);
		}
	}
}
