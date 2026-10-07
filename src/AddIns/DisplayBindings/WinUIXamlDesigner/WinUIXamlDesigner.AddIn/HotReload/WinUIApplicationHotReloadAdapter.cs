using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Project.HotReload;

namespace ICSharpCode.WinUIXamlDesigner.HotReload
{
	/// <summary>
	/// Hot Reload for Windows App SDK (WinUI 3) applications. Same shape as WPF: the agent is
	/// injected with DOTNET_STARTUP_HOOKS and speaks the WPF agent's pipe protocol, so the session is
	/// shared. Inside the application the agent has WinUI load a native XAML Diagnostics TAP and
	/// patches the live tree through it (doc/technotes/hot-reload.md, "WinUI 3: XAML Diagnostics
	/// agent"); no debugger is involved. Contributed by this addin to
	/// /SharpDevelop/HotReload/Adapters (WinUIXamlDesigner.addin).
	/// </summary>
	public sealed class WinUIApplicationHotReloadAdapter : IApplicationHotReloadAdapter
	{
		public string Framework => "WinUI";

		public bool CanHandle(HotReloadLaunchContext context, out string diagnostic)
		{
			diagnostic = null;
			var project = context?.Project;
			if (project == null) {
				diagnostic = "No startup project is selected.";
				return false;
			}

			var framework = XamlFrameworkDetector.DetectProjectFile(project.FileName);
			// Only Microsoft's WinUI: Uno and ProGPU also use WinUI markup but are different runtimes
			// with no XAML Diagnostics endpoint.
			if (framework.Kind != XamlFrameworkKind.WinUI || framework.Runtime != XamlRuntimeKind.MicrosoftWinUI) {
				diagnostic = $"The startup project is not a Windows App SDK WinUI project ({framework.Kind}: {framework.Evidence}).";
				return false;
			}
			if (!XamlFrameworkDetector.IsRuntimeSupportedOnThisOS(framework.Runtime)) {
				diagnostic = "WinUI applications only run on Windows.";
				return false;
			}
			if (LocateAgent() == null) {
				diagnostic = "The WinUI Hot Reload agent was not found in the WinUI designer addin.";
				return false;
			}
			return true;
		}

		public HotReloadCapabilities GetCapabilities(HotReloadLaunchContext context)
		{
			return new HotReloadCapabilities(
				HotReloadChangeDelivery.IdePushesEdits,
				requiresSavedFile: false,
				supportsUnsavedBuffer: true,
				// Attribute values are patched in place; a changed child list is rebuilt from the
				// markup. Resources and anything needing generated code come back restart-required.
				supportedChanges: new[] { HotReloadChangeKind.Property, HotReloadChangeKind.Subtree },
				reportsStatePreservation: false);
		}

		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
			CancellationToken token)
		{
			if (startInfo == null)
				throw new ArgumentNullException(nameof(startInfo));

			var agent = LocateAgent()
				?? throw new InvalidOperationException("The WinUI Hot Reload agent was not found in the WinUI designer addin.");

			// Per-launch, unguessable: the pipe is an unauthenticated local endpoint. (Short for the
			// same reason as WPF's, even though WinUI only runs on Windows: one naming rule.)
			var pipeName = "odhr" + Guid.NewGuid().ToString("N").Substring(0, 16);

			var existingHooks = startInfo.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var hooks) ? hooks : null;
			startInfo.Environment["DOTNET_STARTUP_HOOKS"] =
				string.IsNullOrEmpty(existingHooks) ? agent : existingHooks + Path.PathSeparator + agent;
			startInfo.Environment["WINUI_HOTRELOAD_PIPE"] = pipeName;
			// Without it WinUI records no SrcInfo, and an edited element cannot be found in the live tree.
			startInfo.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1";

			var logFile = Path.Combine(Path.GetTempPath(), "od-winui-hotreload-" + pipeName + ".log");
			startInfo.Environment["WINUI_HOTRELOAD_LOG"] = logFile;

			LoggingService.Info($"WinUI Hot Reload: agent '{agent}' on pipe '{pipeName}', log '{logFile}'.");
			return Task.FromResult<IHotReloadSession>(
				new AgentPipeHotReloadSession(this, GetCapabilities(context), pipeName, logFile));
		}

		/// <summary>
		/// HotReload/winui/ inside this addin, with the native TAPs under tap/&lt;arch&gt;/ beside it.
		/// The agent is AnyCPU; it picks the TAP matching the application process at run time, so
		/// x86, x64 and ARM64 applications are all served by this one path.
		/// </summary>
		internal static string LocateAgent()
		{
			// For a host that is not the IDE - a test runner above all.
			var configured = Environment.GetEnvironmentVariable("OD_WINUI_HOTRELOAD_AGENT");
			if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
				return configured;

			var candidate = Path.Combine(Path.GetDirectoryName(typeof(WinUIApplicationHotReloadAdapter).Assembly.Location) ?? "", "HotReload", "winui", "WinUIHotReload.Agent.dll");
			return File.Exists(candidate) ? candidate : null;
		}
	}
}
