using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>Owns one isolated WinForms designer child process.</summary>
	public sealed class FormsDesignerHostClient : DesignerHostProcessClient, IDesignHostClient
	{
		readonly string runtimeConfigPath;
		readonly string depsFilePath;
		readonly string hostDllPath;

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
			=> InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);

		public Task<DesignerEditSet> FlushAsync(long version, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerEditSet>("session/flush", new { version }, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long version, int x, int y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerHitTestResult>("design/hit-test", new { version, x, y }, cancellationToken);

		public Task<DesignerSessionState> SetPropertyAsync(long version, string componentName, string propertyName, string value, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-property", new { version, componentName, propertyName, value }, cancellationToken);

		public Task<DesignerSessionState> ResetPropertyAsync(long version, string componentName, string propertyName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/reset-property", new { version, componentName, propertyName }, cancellationToken);

		public Task<DesignerSessionState> RenameComponentAsync(long version, string componentName, string newName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/rename-component", new { version, componentName, newName }, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long version, string componentName, string eventName, string handlerName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-event", new { version, componentName, eventName, handlerName }, cancellationToken);

		public Task<DesignerSessionState> ActivateDefaultEventAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/activate-default-event", new { version, componentName }, cancellationToken);

		public Task<DesignerSessionState> AddControlAsync(long version, string parentName, string controlType, string componentName, int x, int y, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/add-control", new { version, parentName, controlType, componentName, x, y }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long version, string componentName, int x, int y, int width, int height, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-bounds", new { version, componentName, x, y, width, height }, cancellationToken);

		public Task<DesignerSessionState> DeleteComponentAsync(long version, string componentName, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/delete-component", new { version, componentName }, cancellationToken);

		public Task<DesignerSessionState> SetZOrderAsync(long version, string componentName, bool bringToFront, CancellationToken cancellationToken)
			=> InvokeAsync<DesignerSessionState>("design/set-z-order", new { version, componentName, bringToFront }, cancellationToken);

		public Task<DesignerSessionState> ApplyLayoutAsync(long version, string operation, string[] componentNames, CancellationToken cancellationToken,
			int deltaX = 0, int deltaY = 0)
			=> InvokeAsync<DesignerSessionState>("design/apply-layout", new { version, operation, componentNames, deltaX, deltaY }, cancellationToken);

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
