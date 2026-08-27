using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>Owns one isolated WinForms designer child process.</summary>
	public sealed class FormsDesignerHostClient : DesignerDocumentHostClient, IDesignHostClient,
		IDesignHostPropertyReset, IDesignHostDefaultEvent, IDesignHostLayout
	{
		static readonly SharedDesignerHostPool<CompatibilityKey, Connection> sharedPool = new(
			(_, connection) => connection.IsAlive,
			async (key, token) => {
				var connection = new Connection(key.RuntimeConfigPath, key.DepsFilePath, key.HostDllPath, key.OperationTimeout);
				await connection.StartConnectionAsync(token).ConfigureAwait(false);
				return connection;
			});
		readonly Connection connection;
		readonly CompatibilityKey poolKey;
		readonly bool shared;
		bool disposed;

		FormsDesignerHostClient(Connection connection, CompatibilityKey poolKey, bool shared) : base(connection)
		{
			this.connection = connection;
			this.poolKey = poolKey;
			this.shared = shared;
		}

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
			=> Document.OpenAsync(snapshot, cancellationToken);

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> Document.UpdateAsync(snapshot, cancellationToken);

		public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken)
			=> Document.FlushAsync(baseVersion, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x = Round(x), y = Round(y) }, cancellationToken);

		/// <summary>The WinForms child lays out in integer device units; design-unit coordinates
		/// round here so the wire contract stays unchanged.</summary>
		static int Round(double value) => (int)Math.Round(value);

		public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken)
			=> Document.SetPropertyAsync(baseVersion, elementId, propertyName, value, cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/reset-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName }, cancellationToken);

		public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken)
			=> Document.RenameAsync(baseVersion, elementId, newName, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken)
			=> Document.SetEventAsync(baseVersion, elementId, eventName, handlerName, cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/activate-default-event", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		/// <summary>Inserts a control; the WinForms backend needs only the toolbox item's CLR type
		/// name plus the proposed component name.</summary>
		public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string elementId, double x, double y, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, elementId, x = Round(x), y = Round(y) }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x = Round(x), y = Round(y), width = Round(width), height = Round(height) }, cancellationToken);

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
			return state;
		}

		Task<DesignerSessionState> DeleteComponentAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long baseVersion, string elementId, bool bringToFront, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/set-z-order", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, bringToFront }, cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long baseVersion, string operation, string[] elementIds, double deltaX, double deltaY, CancellationToken cancellationToken)
			=> connection.InvokeAsync<DesignerSessionState>("design/apply-layout", new { sessionId = SessionId, documentId = DocumentId, baseVersion, operation, elementIds, deltaX = Round(deltaX), deltaY = Round(deltaY) }, cancellationToken);

		public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
			=> connection.InvokeAsync<object>("diagnostics/delay", new { milliseconds }, cancellationToken, TimeSpan.FromMilliseconds(250));

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
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
			public Task StartConnectionAsync(CancellationToken token) => StartAsync(token);
			protected override string GetChildDllPath() => hostDllPath;
			protected override string BuildCommandLine(string childDll, int port, string token)
			{
				var arguments = $"exec \"{childDll}\" --port {port} --token {token}";
				if (File.Exists(runtimeConfigPath) && File.Exists(depsFilePath))
					arguments = $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsFilePath}\" \"{childDll}\" --port {port} --token {token}";
				return arguments;
			}
		}
	}
}
