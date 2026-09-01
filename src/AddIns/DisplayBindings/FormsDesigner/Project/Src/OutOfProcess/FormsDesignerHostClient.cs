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

		public static string LocateChildDll()
		{
			var directory = Path.GetDirectoryName(typeof(FormsDesignerHostClient).Assembly.Location);
			if (String.IsNullOrEmpty(directory))
				return null;
			var useMicrosoft = string.Equals(Environment.GetEnvironmentVariable("OD_FORMS_RUNTIME"), "microsoft", StringComparison.OrdinalIgnoreCase);
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

		public static string SelectedBackend => string.Equals(Environment.GetEnvironmentVariable("OD_FORMS_RUNTIME"), "microsoft", StringComparison.OrdinalIgnoreCase)
			? "WinForms" : "LibreWinForms";

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
