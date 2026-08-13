// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the WinForms designer's
// runtime state (drag a toolbox item from the shared WpfToolbox onto a WinForms DesignSurface)
// without a native UI automation pipeline. See WpfDesignDevFlowActions.cs for the WPF equivalent.

using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.FormsDesigner.DevFlow
{
	[DevFlowUIThread]
	public static class FormsDesignerDevFlowActions
	{
		/// <summary>
		/// Finds the active file's WinForms designer view among its
		/// <see cref="IWorkbenchWindow.ViewContents"/> and makes it the active tab - mirrors
		/// WpfDesignDevFlowActions.FindWpfViewContent, since FormsDesignerViewContent is likewise a
		/// secondary view alongside the primary code editor and only mounts its DesignSurface into
		/// the live visual tree once actually switched to.
		/// </summary>
		static FormsDesignerViewContent FindFormsDesignerViewContent()
		{
			var window = SD.Workbench.ActiveWorkbenchWindow;
			if (window == null)
				return null;

			for (int i = 0; i < window.ViewContents.Count; i++) {
				if (window.ViewContents[i] is FormsDesignerViewContent formsView) {
					// Only switch if it isn't already the active view - repeatedly calling
					// SwitchView on an already-active view is not a pure no-op: it re-hosts the
					// ToolsPad's content (since ActiveViewContentChanged fires again), which resets
					// the shared WPF toolbox ListBox's scroll offset back to the top. Callers that
					// poll this (e.g. od.forms-designer.status in a drop-detection retry loop) would
					// otherwise silently scroll the toolbox out from under an in-flight drag.
					if (window.ActiveViewContent != formsView)
						window.SwitchView(i);
					return formsView;
				}
			}

			return null;
		}

		/// <summary>
		/// A toolbox drop onto a container control (e.g. a Panel) parents the new control under
		/// THAT container, not directly under the root Form - real WinForms designers nest
		/// controls this way too. Enumerate the whole tree so a drop like that is actually visible
		/// here, not just direct children of the root.
		/// </summary>
		static System.Collections.Generic.IEnumerable<Control> EnumerateControlsRecursively(Control root)
		{
			foreach (Control child in root.Controls) {
				yield return child;
				foreach (Control descendant in EnumerateControlsRecursively(child))
					yield return descendant;
			}
		}

		[DevFlowAction("od.forms-designer.status", Description = "Inspect the active WinForms designer view: whether the DesignSurface loaded and the set of named controls on the root component")]
		public static string GetDesignerStatus()
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.Host == null)
				return JsonSerializer.Serialize(new { designerLoaded = false });

			var root = viewContent.Host.RootComponent as Control;
			var controlNames = root != null ? EnumerateControlsRecursively(root).Select(c => c.Name).ToArray() : Array.Empty<string>();

			return JsonSerializer.Serialize(new {
				designerLoaded = true,
				rootComponentType = viewContent.Host.RootComponent?.GetType().Name,
				controlNames
			});
		}

		[DevFlowAction("od.forms-designer.query-control-screen-bounds", Description = "Get a named control's on-screen bounds within the active WinForms DesignSurface, translated from the embedded WinForms control tree to WPF screen coordinates via Control.PointToScreen - used to drive a synthetic mouse drag (press/drag-move/release via cliclick) onto it, mirroring od.wpf-designer.query-element-screen-bounds for the WPF Design canvas")]
		public static string QueryControlScreenBounds(string controlName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.Host == null)
				return JsonSerializer.Serialize(new { success = false, error = "WinForms designer is not loaded" });

			var root = viewContent.Host.RootComponent as Control;
			var control = root?.Controls.Cast<Control>().FirstOrDefault(c => c.Name == controlName);
			if (control == null)
				return JsonSerializer.Serialize(new { success = false, error = "Control not found: " + controlName });

			var topLeft = control.PointToScreen(System.Drawing.Point.Empty);
			return JsonSerializer.Serialize(new {
				success = true,
				x = (double)topLeft.X,
				y = (double)topLeft.Y,
				width = (double)control.Width,
				height = (double)control.Height
			});
		}
	}
}
