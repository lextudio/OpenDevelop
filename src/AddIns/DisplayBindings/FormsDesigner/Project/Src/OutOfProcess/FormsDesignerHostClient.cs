using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>Owns one isolated WinForms designer child process.</summary>
	public sealed class FormsDesignerHostClient : DesignerHostProcessClient, IDesignHostClient,
		IDesignHostPropertyReset, IDesignHostDefaultEvent, IDesignHostLayout
	{
		readonly string runtimeConfigPath;
		readonly string depsFilePath;
		readonly string hostDllPath;

		/// <summary>Identifies the single document this client opens against its child.
		/// One process/one document per host today (designer-common.md's starting point);
		/// stable for the client's life.</summary>
		public string DocumentId { get; } = Guid.NewGuid().ToString("N");

		FormsDesignerHostClient(string runtimeConfigPath, string depsFilePath, string hostDllPath, TimeSpan? operationTimeout)
			: base(operationTimeout)
		{
			this.runtimeConfigPath = runtimeConfigPath;
			this.depsFilePath = depsFilePath;
			this.hostDllPath = hostDllPath;
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
			var client = new FormsDesignerHostClient(runtimeConfigPath, depsFilePath, hostDllPath ?? LocateChildDll(), operationTimeout);
			await client.StartAsync(cancellationToken).ConfigureAwait(false);
			return client;
		}

		protected override string GetChildDllPath() => hostDllPath;

		protected override string BuildCommandLine(string childDll, int port, string token)
		{
			var arguments = $"exec \"{childDll}\" --port {port} --token {token}";
			if (File.Exists(runtimeConfigPath) && File.Exists(depsFilePath))
				arguments = $"exec --runtimeconfig \"{runtimeConfigPath}\" --depsfile \"{depsFilePath}\" \"{childDll}\" --port {port} --token {token}";
			return arguments;
		}

		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
		{
			snapshot.SessionId = SessionId;
			snapshot.DocumentId = DocumentId;
			return InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);
		}

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
		{
			snapshot.SessionId = SessionId;
			snapshot.DocumentId = DocumentId;
			return InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);
		}

		public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerEditSet>("session/flush", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x = Round(x), y = Round(y) }, cancellationToken);

		/// <summary>The WinForms child lays out in integer device units; design-unit coordinates
		/// round here so the wire contract stays unchanged.</summary>
		static int Round(double value) => (int)Math.Round(value);

		public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, value }, cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long baseVersion, string elementId, string propertyName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/reset-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName }, cancellationToken);

		public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/rename", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, newName }, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-event", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, eventName, handlerName }, cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long baseVersion, string elementId, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/activate-default-event", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		/// <summary>Inserts a control; the WinForms backend needs only the toolbox item's CLR type
		/// name plus the proposed component name.</summary>
		public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string elementId, double x, double y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, elementId, x = Round(x), y = Round(y) }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x = Round(x), y = Round(y), width = Round(width), height = Round(height) }, cancellationToken);

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
			=> InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long baseVersion, string elementId, bool bringToFront, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-z-order", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, bringToFront }, cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long baseVersion, string operation, string[] elementIds, double deltaX, double deltaY, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/apply-layout", new { sessionId = SessionId, documentId = DocumentId, baseVersion, operation, elementIds, deltaX = Round(deltaX), deltaY = Round(deltaY) }, cancellationToken);

		public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
			=> InvokeAsync<object>("diagnostics/delay", new { milliseconds }, cancellationToken, TimeSpan.FromMilliseconds(250));

		#region IDesignHostClient

		public Task PingAsync(CancellationToken cancellationToken = default)
			=> InvokeAsync<object>("ping", null, cancellationToken);

		public Task ShutdownAsync(CancellationToken cancellationToken = default)
			=> InvokeAsync<object>("shutdown", null, cancellationToken, TimeSpan.FromSeconds(3));

		#endregion
	}
}
