// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// DevFlow diagnostics for the fused scene editor.
//
// The scene renders into a separate native child window (SDL/Cocoa) layered over the WPF tree, so
// the host's screenshot path cannot see it - it captures the WPF visual tree, and on this LibreWPF
// build it cannot capture at all (wpfgfx_cor3 is a Windows-only native library). That leaves
// automated checks with no way to observe the viewport, which this action fills: it reports whether
// the real controller is running, the overlay's exact screen rectangle (so synthetic OS-level
// pointer events can be aimed at it), and whether that window holds key/main status - the open
// question for keyboard input, since a Cocoa child window added via addChildWindow does not take
// key focus by default.

using System;
using System.Globalization;
using System.Text.Json;

using ICSharpCode.SharpDevelop;

using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.StrideGameStudio
{
	[DevFlowUIThread]
	public static class StrideGameStudioDevFlowActions
	{
		[DevFlowAction("od.stride.scene-status", Description = "Inspect the fused Stride scene editor overlay")]
		public static string SceneStatus()
		{
			var viewport = StrideSceneEditorViewport.Current;
			if (viewport == null)
				return "{\"running\":false}";

			return viewport.DescribeForDevFlow();
		}

		[DevFlowAction("od.stride.simulate-gesture", Description = "Drive a camera gesture through the simulated input devices (diagnostic)")]
		public static string SimulateGesture(string gesture)
		{
			var viewport = StrideSceneEditorViewport.Current;
			if (viewport == null)
				return "{\"running\":false}";

			return viewport.SimulateCameraGesture(gesture ?? "rotate");
		}

		[DevFlowAction("od.stride.make-titled", Description = "Restore a titled style mask on the scene overlay (diagnostic)")]
		public static string MakeTitled()
		{
			var viewport = StrideSceneEditorViewport.Current;
			if (viewport == null)
				return "{\"running\":false}";

			viewport.MakeTitledForDiagnostics();
			return viewport.DescribeForDevFlow();
		}

		[DevFlowAction("od.stride.detach-overlay", Description = "Detach the scene overlay from the host child-window relationship (diagnostic)")]
		public static string DetachOverlay()
		{
			var viewport = StrideSceneEditorViewport.Current;
			if (viewport == null)
				return "{\"running\":false}";

			viewport.DetachOverlay();
			return viewport.DescribeForDevFlow();
		}

		[DevFlowAction("od.stride.reassert-overlay", Description = "Re-order the scene overlay to front and make it key")]
		public static string ReassertOverlay()
		{
			var viewport = StrideSceneEditorViewport.Current;
			if (viewport == null)
				return "{\"running\":false}";

			viewport.ReassertOverlay();
			return viewport.DescribeForDevFlow();
		}

		[DevFlowAction("od.stride.ensure-launcher", Description = "Resolve (or generate) the startable launcher project for the open Stride game solution and make it the startup project, so od.run-project can run the game")]
		public static string EnsureLauncher()
		{
			var result = StrideLauncherService.EnsureLauncher(SD.ProjectService.CurrentSolution);
			return JsonSerializer.Serialize(new {
				success = result.Success,
				status = result.Status,
				gameProjectName = result.GameProjectName,
				launcherProjectName = result.LauncherProjectName,
				launcherProjectPath = result.LauncherProjectPath,
				generated = result.Generated,
				addedToSolution = result.AddedToSolution,
				setAsStartupProject = result.SetAsStartupProject,
				error = result.Error
			});
		}
	}
}