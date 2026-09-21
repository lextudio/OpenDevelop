using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.SharpDevelop.Project.HotReload.Wpf
{
	/// <summary>
	/// Hot Reload for WPF applications, including LibreWPF on macOS/Linux.
	/// The agent is injected with DOTNET_STARTUP_HOOKS and applies changes in the target process
	/// using WPF's diagnostic APIs; OpenDevelop only sends XAML over a private pipe and never
	/// touches the running application's visual tree itself.
	/// </summary>
	public sealed class WpfApplicationHotReloadAdapter : IApplicationHotReloadAdapter
	{
		public string Framework => "WPF";

		public bool CanHandle(HotReloadLaunchContext context, out string diagnostic)
		{
			diagnostic = null;
			var project = context?.Project;
			if (project == null) {
				diagnostic = "No startup project is selected.";
				return false;
			}

			// Use the repo's single routing authority rather than a private UseWPF check: it reads
			// the project file itself (SDK, packages, properties) and already knows how to tell WPF
			// from WinUI and Uno. A hand-rolled GetEvaluatedProperty("UseWPF") test got this wrong -
			// it answered "not a WPF project" for a project whose csproj plainly sets UseWPF.
			var framework = XamlFrameworkDetector.DetectProjectFile(project.FileName);
			if (framework.Kind != XamlFrameworkKind.Wpf) {
				diagnostic = $"The startup project is not a WPF project ({framework.Kind}: {framework.Evidence}).";
				return false;
			}

			// Both LibreWPF and Microsoft WPF use WPF markup, but each needs the agent built
			// against its own runtime. Route on the detected runtime, and refuse when the matching
			// agent is not deployed rather than hand a Microsoft WPF debuggee the portable agent.
			if (LocateAgent(framework.Runtime) == null) {
				diagnostic = $"The {Describe(framework.Runtime)} Hot Reload agent was not found next to the IDE.";
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
				supportedChanges: new[] {
					HotReloadChangeKind.Property,
					HotReloadChangeKind.Subtree,
					HotReloadChangeKind.Resource,
					HotReloadChangeKind.FullDocument,
				},
				reportsStatePreservation: false);
		}

		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
			CancellationToken token)
		{
			if (startInfo == null)
				throw new ArgumentNullException(nameof(startInfo));

			var runtime = RuntimeOf(context);
			var agent = LocateAgent(runtime)
				?? throw new InvalidOperationException(
					$"The {Describe(runtime)} Hot Reload agent was not found next to the IDE.");

			// Unguessable per-launch name: the agent's pipe is an unauthenticated local endpoint,
			// so the name is the only thing keeping another process off this session.
			// It must also stay SHORT. On Unix a named pipe is a domain socket at
			// <TMPDIR>/CoreFxPipe_<name>, and the whole path is limited to 104 characters. macOS
			// already spends ~48 of those on $TMPDIR alone, so a descriptive prefix plus a full
			// 32-character GUID overflows it - and the failure surfaces inside the target process
			// as an ArgumentOutOfRangeException on its listener thread, i.e. as an agent that
			// simply never answers, with nothing wrong on this side to look at.
			var pipeName = "odhr" + Guid.NewGuid().ToString("N").Substring(0, 16);

			// DOTNET_STARTUP_HOOKS is a path list; preserve anything the launch already set.
			var existingHooks = startInfo.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var hooks) ? hooks : null;
			startInfo.Environment["DOTNET_STARTUP_HOOKS"] =
				string.IsNullOrEmpty(existingHooks) ? agent : existingHooks + Path.PathSeparator + agent;
			startInfo.Environment["WPF_HOTRELOAD_PIPE"] = pipeName;
			// Without this WPF records no {Uri, line, column} for the objects it creates, and the
			// agent cannot map an edit back to a live element.
			startInfo.Environment["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1";

			var logFile = Path.Combine(Path.GetTempPath(), "od-wpf-hotreload-" + pipeName + ".log");
			startInfo.Environment["WPF_HOTRELOAD_LOG"] = logFile;

			LoggingService.Info($"WPF Hot Reload: agent '{agent}' on pipe '{pipeName}', log '{logFile}'.");
			return Task.FromResult<IHotReloadSession>(
				new WpfHotReloadSession(this, GetCapabilities(context), pipeName, logFile));
		}

		/// <summary>
		/// The runtime this launch targets. Selection only needs the project; the test path (and a
		/// manual launch) has none, so default to the portable agent - the one a macOS/Linux host
		/// can actually run. On Windows a Microsoft.NET.Sdk + UseWPF project is detected as
		/// <see cref="XamlRuntimeKind.MicrosoftWpf"/> and gets the Windows Desktop agent instead.
		/// </summary>
		static XamlRuntimeKind RuntimeOf(HotReloadLaunchContext context)
		{
			var project = context?.Project;
			if (project == null)
				return XamlRuntimeKind.LibreWpf;
			return XamlFrameworkDetector.DetectProjectFile(project.FileName).Runtime;
		}

		static string SubdirectoryFor(XamlRuntimeKind runtime)
			=> runtime == XamlRuntimeKind.MicrosoftWpf ? "microsoft" : "librewpf";

		static string Describe(XamlRuntimeKind runtime)
			=> runtime == XamlRuntimeKind.MicrosoftWpf ? "Microsoft WPF" : "LibreWPF";

		/// <summary>
		/// The agent ships next to the IDE, one build per runtime under HotReload/&lt;runtime&gt;/.
		/// It is a plain assembly loaded by the target runtime, so it only has to exist on disk - it
		/// is never loaded into the IDE itself.
		/// </summary>
		internal static string LocateAgent(XamlRuntimeKind runtime)
		{
			// OD_WPF_HOTRELOAD_AGENT first so a host that is not the IDE - a test runner, above all
			// - can point at a specific deployed agent instead of needing a copy of its own.
			var configured = Environment.GetEnvironmentVariable("OD_WPF_HOTRELOAD_AGENT");
			if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
				return configured;

			var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			foreach (var candidate in new[] {
				Path.Combine(baseDirectory, "HotReload", SubdirectoryFor(runtime), "WpfHotReload.Agent.dll"),
				// Pre-variant layout, kept so an older deployment still resolves.
				Path.Combine(baseDirectory, "HotReload", "WpfHotReload.Agent.dll"),
				Path.Combine(baseDirectory, "WpfHotReload.Agent.dll"),
			}) {
				if (File.Exists(candidate))
					return candidate;
			}
			return null;
		}
	}
}
