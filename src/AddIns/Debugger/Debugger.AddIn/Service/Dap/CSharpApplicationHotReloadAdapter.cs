// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the
// Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
// and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Project.HotReload;

namespace Debugger.AddIn.HotReload
{
	/// <summary>
	/// Edit and Continue for ordinary C# projects under SharpDbg. WPF and WinUI projects keep
	/// their own XAML adapters; this adapter fills the gap for console apps, libraries, services
	/// and other managed projects where the Roslyn workspace itself is the source of the delta.
	/// </summary>
	public sealed class CSharpApplicationHotReloadAdapter : IApplicationHotReloadAdapter
	{
		public string Framework => "C#";

		public bool CanHandle(HotReloadLaunchContext context, out string diagnostic)
		{
			diagnostic = null;
			if (context == null || !context.WithDebugger) {
				diagnostic = "C# Edit and Continue needs the SharpDbg debugger.";
				return false;
			}
			var project = context.Project;
			if (project == null) {
				diagnostic = "No startup project is selected.";
				return false;
			}
			if (!string.Equals(project.Language, "C#", StringComparison.OrdinalIgnoreCase)) {
				diagnostic = $"The startup project is not a C# project ({project.Language}).";
				return false;
			}

			// WPF and WinUI have their own in-process XAML agents. Claiming them here would replace
			// the visual-tree reload with a code-only delta, so leave them to their adapters.
			var framework = XamlFrameworkDetector.DetectProjectFile(project.FileName);
			if (framework.Kind is XamlFrameworkKind.Wpf or XamlFrameworkKind.WinUI) {
				diagnostic = $"The startup project is handled by the {framework.Kind} Hot Reload adapter.";
				return false;
			}

			// Mobile launches have their own debugger plumbing and target frameworks. The Roslyn
			// EnC path here is for desktop/console managed launches.
			var targetFramework = ProjectTargetFrameworkService.GetActiveTargetFramework(project);
			if (targetFramework != null && IsMobileTargetFramework(targetFramework)) {
				diagnostic = $"C# Edit and Continue is not enabled for the mobile target '{targetFramework}'.";
				return false;
			}

			if (project is MSBuildBasedProject msbuildProject) {
				if (string.Equals(msbuildProject.GetEvaluatedProperty("UseMaui"), "true", StringComparison.OrdinalIgnoreCase)) {
					diagnostic = "C# Edit and Continue is not enabled for MAUI projects.";
					return false;
				}
				// Edit and Continue only works on unoptimized code. Say so before launch rather than
				// letting every Apply fail with the debugger's ruder-edit message.
				if (string.Equals(msbuildProject.GetEvaluatedProperty("Optimize"), "true", StringComparison.OrdinalIgnoreCase)) {
					diagnostic = "C# Edit and Continue is not available for optimized (Release) builds.";
					return false;
				}
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
					HotReloadChangeKind.CodeDelta,
					HotReloadChangeKind.RestartRequired,
				},
				reportsStatePreservation: false);
		}

		static bool IsMobileTargetFramework(string targetFramework)
		{
			return targetFramework.IndexOf("-android", StringComparison.OrdinalIgnoreCase) >= 0
				|| targetFramework.IndexOf("-ios", StringComparison.OrdinalIgnoreCase) >= 0
				|| targetFramework.IndexOf("-maccatalyst", StringComparison.OrdinalIgnoreCase) >= 0
				|| targetFramework.IndexOf("-tvos", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		public Task<IHotReloadSession> StartAsync(HotReloadLaunchContext context, ProcessStartInfo startInfo,
			CancellationToken token)
		{
			if (startInfo == null)
				throw new ArgumentNullException(nameof(startInfo));

			// WindowsDebugger only opts SharpDbg into ICorDebug module updates for launches that
			// explicitly asked for Hot Reload. This marker is that request for a plain C# launch.
			startInfo.Environment["OD_HOTRELOAD_ENC"] = "1";
			return Task.FromResult<IHotReloadSession>(new CSharpHotReloadSession(this, GetCapabilities(context)));
		}
	}
}
