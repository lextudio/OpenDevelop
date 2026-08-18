// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the WinForms designer's
// runtime state (drag a toolbox item from the shared WpfToolbox onto the out-of-process design
// surface) without a native UI automation pipeline. See WpfDesignDevFlowActions.cs for the WPF
// equivalent.

using System;
using System.Linq;
using System.Text.Json;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
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
		/// controls this way too. The child process reports the whole component tree (see
		/// DesignerSessionState.Components), so parent-side enumeration is not needed anymore.
		/// </summary>

		[DevFlowAction("od.forms-designer.surface-geometry", Description = "Report the WinForms design surface geometry (rendered form bounds, selection outline bounds, resize-handle position, selected element bounds) in screen coordinates - the smoke probe for the resize-drag invariant that selection and handle always track the rendered form")]
		public static string GetSurfaceGeometry()
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.RemoteSurfaceGeometry is not { } g)
				return JsonSerializer.Serialize(new { available = false });
			return JsonSerializer.Serialize(DesignerSurfaceGeometryProbe.ToJson(g));
		}

		[DevFlowAction("od.forms-designer.outline-status", Description = "Inspect the WinForms designer's Document Outline pad: whether the shared outline control is mounted and visible, and the element tree it currently shows")]
		public static string GetOutlineStatus()
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { present = false, visible = false });

			var outline = viewContent.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			if (outline == null)
				return JsonSerializer.Serialize(new { present = false, visible = false });

			var root = outline.Items.Count > 0
				? (outline.Items[0] as System.Windows.Controls.TreeViewItem)?.Tag as ICSharpCode.SharpDevelop.Designer.Remote.DesignerElementNode
				: null;
			var nodes = new System.Collections.Generic.List<object>();
			void Collect(ICSharpCode.SharpDevelop.Designer.Remote.DesignerElementNode node)
			{
				nodes.Add(new { name = node.Name, type = node.Type });
				foreach (var child in node.Children)
					Collect(child);
			}
			if (root != null)
				Collect(root);
			return JsonSerializer.Serialize(new {
				present = true,
				visible = outline.IsVisible,
				root = root == null ? null : root.Name,
				rootType = root?.Type,
				nodes = nodes.ToArray()
			});
		}

		[DevFlowAction("od.forms-designer.outline-select", Description = "Select a control in the WinForms designer's Document Outline (routes through the same selection path as a surface click) and report the resulting selection")]
		public static string OutlineSelect(string name)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { success = false, error = "no designer view" });
			var outline = viewContent.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			if (outline == null)
				return JsonSerializer.Serialize(new { success = false, error = "no outline control" });
			outline.SelectNodeById(name);
			return JsonSerializer.Serialize(new {
				success = true,
				selected = viewContent.RemoteDesignerSelectedComponent
			});
		}

		[DevFlowAction("od.forms-designer.status", Description = "Inspect the active WinForms designer view: whether the out-of-process DesignSurface loaded and the set of named controls on the root component")]
		public static string GetDesignerStatus()
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { designerLoaded = false });

			if (!viewContent.IsRemoteDesignerLoaded)
				return JsonSerializer.Serialize(new { designerLoaded = false });

			var state = viewContent.RemoteDesignerState;
			return JsonSerializer.Serialize(new {
				designerLoaded = true,
				outOfProcess = true,
				usesCodeDomLoader = false,
				loaderType = "ICSharpCode.FormsDesigner.Host.SnapshotRoslynDesignerLoader",
				hostProcessId = viewContent.RemoteDesignerProcessId,
				rootComponentType = state.RootType,
				controlNames = state.Components.Select(component => component.Name).ToArray()
			});
		}

		[DevFlowAction("od.forms-designer.query-control-screen-bounds", Description = "Get a named control's on-screen bounds within the active out-of-process WinForms design surface, translated to WPF screen coordinates by the child host - used to drive a synthetic mouse drag (press/drag-move/release via cliclick) onto it, mirroring od.wpf-designer.query-element-screen-bounds for the WPF Design canvas")]
		public static string QueryControlScreenBounds(string controlName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { success = false, error = "WinForms designer is not loaded" });

			if (!viewContent.IsRemoteDesignerLoaded)
				return JsonSerializer.Serialize(new { success = false, error = "WinForms designer is not loaded" });

			if (!viewContent.TryGetRemoteComponentScreenBounds(controlName, out var bounds))
				return JsonSerializer.Serialize(new { success = false, error = "Control not found: " + controlName });
			return JsonSerializer.Serialize(new {
				success = true,
				x = bounds.X,
				y = bounds.Y,
				width = bounds.Width,
				height = bounds.Height
			});
		}

		[DevFlowAction("od.forms-designer.set-property", Description = "Set a component property in the active out-of-process WinForms designer and refresh its rendered frame")]
		public static string SetProperty(string componentName, string propertyName, string value)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return JsonSerializer.Serialize(new { success = false, error = "The out-of-process WinForms designer is not loaded" });
			try {
				viewContent.SetRemoteProperty(componentName, propertyName, value);
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception exception) {
				return JsonSerializer.Serialize(new { success = false, error = exception.Message });
			}
		}

		[DevFlowAction("od.forms-designer.set-event", Description = "Bind a component event in the out-of-process WinForms designer and generate a missing handler")]
		public static string SetEvent(string componentName, string eventName, string handlerName)
		{
			return InvokeRemote(view => view.SetRemoteEvent(componentName, eventName, handlerName));
		}

		[DevFlowAction("od.forms-designer.add-control", Description = "Create a standard control in the active out-of-process WinForms designer and generate its designer source")]
		public static string AddControl(string parentName, string controlType, string componentName, int x, int y)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return JsonSerializer.Serialize(new { success = false, error = "The out-of-process WinForms designer is not loaded" });
			try {
				viewContent.AddRemoteControl(parentName, controlType, componentName, x, y);
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception exception) {
				return JsonSerializer.Serialize(new { success = false, error = exception.Message });
			}
		}

		[DevFlowAction("od.forms-designer.set-bounds", Description = "Move and resize a control in the active out-of-process WinForms designer")]
		public static string SetBounds(string componentName, int x, int y, int width, int height)
		{
			return InvokeRemote(view => view.SetRemoteBounds(componentName, x, y, width, height));
		}

		[DevFlowAction("od.forms-designer.delete-component", Description = "Delete a component in the active out-of-process WinForms designer")]
		public static string DeleteComponent(string componentName)
		{
			return InvokeRemote(view => view.DeleteRemoteComponent(componentName));
		}

		static string InvokeRemote(Action<FormsDesignerViewContent> action)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return JsonSerializer.Serialize(new { success = false, error = "The out-of-process WinForms designer is not loaded" });
			try {
				action(viewContent);
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception exception) {
				return JsonSerializer.Serialize(new { success = false, error = exception.Message });
			}
		}
	}
}
