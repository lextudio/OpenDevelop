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

		public Task<DesignerEditSet> FlushAsync(long version, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerEditSet>("session/flush", new { sessionId = SessionId, documentId = DocumentId, version }, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long version, double x, double y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, version, x = Round(x), y = Round(y) }, cancellationToken);

		/// <summary>The WinForms child lays out in integer device units; design-unit coordinates
		/// round here so the wire contract stays unchanged.</summary>
		static int Round(double value) => (int)Math.Round(value);

		public Task<DesignerSessionState> SetPropertyAsync(long version, string componentName, string propertyName, string value, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-property", new { sessionId = SessionId, documentId = DocumentId, version, componentName, propertyName, value }, cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long version, string componentName, string propertyName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/reset-property", new { sessionId = SessionId, documentId = DocumentId, version, componentName, propertyName }, cancellationToken);

		public Task<DesignerSessionState> RenameAsync(long version, string componentName, string newName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/rename", new { sessionId = SessionId, documentId = DocumentId, version, componentName, newName }, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long version, string componentName, string eventName, string handlerName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-event", new { sessionId = SessionId, documentId = DocumentId, version, componentName, eventName, handlerName }, cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/activate-default-event", new { sessionId = SessionId, documentId = DocumentId, version, componentName }, cancellationToken);

		/// <summary>Inserts a control; the WinForms backend needs only the toolbox item's CLR type
		/// name plus the proposed component name.</summary>
		public Task<DesignerSessionState> AddElementAsync(long version, string parentName, DesignerToolboxItemInfo item, string componentName, double x, double y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, version, parentName, controlType = item?.TypeName, componentName, x = Round(x), y = Round(y) }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long version, string componentName, double x, double y, double width, double height, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, version, componentName, x = Round(x), y = Round(y), width = Round(width), height = Round(height) }, cancellationToken);

		/// <summary>Deletes elements one RPC at a time: the child's <c>design/delete-elements</c>
		/// takes a single name, and deletes do not bump the document version, so every call in
		/// the loop validates against the same <paramref name="version"/>. The last child state wins.</summary>
		public async Task<DesignerSessionState> DeleteElementsAsync(long version, string[] elementIds, CancellationToken cancellationToken)
		{
			DesignerSessionState state = null;
			if (elementIds != null) {
				foreach (var elementId in elementIds)
					state = await DeleteComponentAsync(version, elementId, cancellationToken).ConfigureAwait(false);
			}
			return state;
		}

		Task<DesignerSessionState> DeleteComponentAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, version, componentName }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long version, string componentName, bool bringToFront, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-z-order", new { sessionId = SessionId, documentId = DocumentId, version, componentName, bringToFront }, cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long version, string operation, string[] componentNames, double deltaX, double deltaY, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/apply-layout", new { sessionId = SessionId, documentId = DocumentId, version, operation, componentNames, deltaX = Round(deltaX), deltaY = Round(deltaY) }, cancellationToken);

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
