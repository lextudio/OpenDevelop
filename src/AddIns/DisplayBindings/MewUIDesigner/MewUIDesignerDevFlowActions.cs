using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Designer.Remote;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.MewUIDesigner;

[DevFlowUIThread]
public static class MewUIDesignerDevFlowActions
{
	[DevFlowAction("od.mewui-designer.status", Description = "Inspect the active MewUI C# visual designer")]
	public static string Status()
	{
		var view = Activate();
		var grid = PropertyGrid;
		return view == null ? JsonSerializer.Serialize(new { active = false }) : JsonSerializer.Serialize(new {
			active = true, status = view.Status, windowClassName = view.WindowClassName, elementCount = view.ElementCount,
			selectedName = view.SelectedName, selectedIds = view.SelectedIds, hostProcessId = view.HostProcessId, hostPoolKey = view.HostPoolKey, hostSessionId = view.HostSessionId, hostDocumentId = view.HostDocumentId, activeHostLeases = view.ActiveHostLeases, hostRecoveryCount = view.HostRecoveryCount, canUndo = view.EnableUndo, canRedo = view.EnableRedo,
			toolboxItemCount = view.ToolboxItemCount, toolboxFilterText = view.ToolboxFilterText, toolboxSelectedItem = view.SelectedToolboxType, toolboxHosted = view.IsToolboxHosted, toolboxSearchHosted = (SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HasToolboxSearch == true, zoomComboSelectedIndex = view.ZoomComboSelectedIndex, outlineHosted = view.IsOutlineHosted, outlineItemCount = view.OutlineItemCount,
			propertyPadSelectedType = grid?.SelectedObject?.GetType().FullName, propertyPadPropertyCount = grid?.Properties?.Count ?? 0,
			toolbarItemCount = view.ToolbarItemCount, toolbarItems = view.ToolbarItems, toolbarCapabilities = view.ToolbarCapabilities, zoom = view.Zoom, fitMeasured = view.FitMeasured, gridlines = view.Gridlines,
			isDirty = view.IsDesignerDirty, hostLogTail = view.HostLogTail
		});
	}
	[DevFlowAction("od.mewui-designer.select", Description = "Select a MewUI element by generated field name")]
	public static string Select(string name) { var v = Activate(); var ok = v?.SelectByName(name) == true; return JsonSerializer.Serialize(new { success = ok, selectedName = v?.SelectedName, propertyPadSelectedType = PropertyGrid?.SelectedObject?.GetType().FullName }); }
	[DevFlowAction("od.mewui-designer.multi-select", Description = "Replace the MewUI designer selection set; first name is primary")]
	public static string MultiSelect(string names) { var view = Activate(); var list = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var ok = view?.SelectByNames(list) == true; return DesignerDevFlowResults.Selection(ok, view?.SelectedIds); }
	[DevFlowAction("od.mewui-designer.toolbox.insert", Description = "Insert a MewUI control into the selected container")]
	public static string Insert(string controlName) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.Add(controlName) == true, elementCount = v?.ElementCount ?? 0 }); }
	[DevFlowAction("od.mewui-designer.toolbox.filter", Description = "Filter the MewUI Toolbox using the common catalogue semantics")]
	public static string FilterToolbox(string text) { var v = Activate(); v?.FilterToolbox(text); return DesignerDevFlowResults.ToolboxFilter(v != null, v?.ToolboxFilterText, v?.ToolboxItemCount ?? 0, v?.SelectedToolboxType); }
	[DevFlowAction("od.mewui-designer.set-property", Description = "Set a source-backed property on the selected MewUI element")]
	public static string SetProperty(string name, string value) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.SetSelectedProperty(name, value) == true }); }
	[DevFlowAction("od.mewui-designer.properties.event.bind", Description = "Bind a MewUI event through the selected Properties-pad adapter")]
	public static string BindEvent(string eventName) { var view = Activate(); var selected = PropertyGrid?.SelectedObject; var exists = selected != null && TypeDescriptor.GetEvents(selected).Find(eventName, false) != null; if (exists && selected is IEventBindingHost host) host.BindEvent(eventName); return JsonSerializer.Serialize(new { success = exists && selected is IEventBindingHost, eventName, selectedName = view?.SelectedName }); }
	[DevFlowAction("od.mewui-designer.properties.edit", Description = "Edit a common property through the real shared Properties pad")]
	public static string EditProperty(string propertyName, string value)
	{
		var view = Activate(); var grid = PropertyGrid;
		if (view == null || grid?.SelectedObject == null) return DesignerDevFlowResults.Failure("MewUI selection is not bound to the Properties pad");
		var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(property => property.PropertyName == propertyName);
		if (item == null) return JsonSerializer.Serialize(new { success = false, error = "Property not found", propertyNames = grid.Properties?.OfType<PropertyItem>().Select(property => property.PropertyName).ToArray() });
		item.Value = value;
		return JsonSerializer.Serialize(new { success = true, selectedIds = view.SelectedIds, propertyName, after = item.Value?.ToString() });
	}
	[DevFlowAction("od.mewui-designer.delete", Description = "Delete the selected MewUI element")]
	public static string Delete() { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.DeleteSelected() == true, elementCount = v?.ElementCount ?? 0 }); }
	[DevFlowAction("od.mewui-designer.reorder", Description = "Move the selected MewUI child within its generated Children relationship")]
	public static string Reorder(int delta) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.ReorderSelected(delta) == true, selectedName = v?.SelectedName }); }
	[DevFlowAction("od.mewui-designer.undo", Description = "Undo the last MewUI designer source edit")]
	public static string Undo() { var v = Activate(); v?.Undo(); return Status(); }
	[DevFlowAction("od.mewui-designer.redo", Description = "Redo the last MewUI designer source edit")]
	public static string Redo() { var v = Activate(); v?.Redo(); return Status(); }
	[DevFlowAction("od.mewui-designer.refresh", Description = "Reload the MewUI design from generated source")]
	public static string Refresh() { var v = Activate(); v?.RefreshDesign(); return JsonSerializer.Serialize(new { success = v != null, hostProcessId = v?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.mewui-designer.restart-host", Description = "Restart the isolated MewUI designer host")]
	public static string RestartHost() { var v = Activate(); var oldPid = v?.HostProcessId ?? 0; v?.RestartDesignHost(); return JsonSerializer.Serialize(new { success = v != null, oldHostProcessId = oldPid, hostProcessId = v?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.mewui-designer.terminate-host", Description = "Terminate the shared MewUI host to verify automatic recovery")]
	public static string TerminateHost() { var v = Activate(); var oldPid = v?.HostProcessId ?? 0; v?.TerminateDesignHost(); return JsonSerializer.Serialize(new { success = v != null, oldHostProcessId = oldPid }); }
	[DevFlowAction("od.mewui-designer.zoom", Description = "Set the common designer toolbar zoom")]
	public static string Zoom(double value) { var v = Activate(); if (v != null) v.Zoom = value; return JsonSerializer.Serialize(new { success = v != null, zoom = v?.Zoom ?? 0 }); }
	[DevFlowAction("od.mewui-designer.fit", Description = "Fit the MewUI design using the common canvas toolbar behavior")]
	public static string Fit() { var v = Activate(); v?.FitDesign(); return JsonSerializer.Serialize(new { success = v != null, zoom = v?.Zoom ?? 0, measured = v?.FitMeasured ?? false }); }
	[DevFlowAction("od.mewui-designer.gridlines", Description = "Toggle MewUI design-space gridlines")]
	public static string Gridlines(bool enabled) { var v = Activate(); v?.ShowGridlines(enabled); return JsonSerializer.Serialize(new { success = v != null, gridlines = v?.Gridlines ?? false }); }

	// Mirrors od.wpf-designer.toolbox.query-item-bounds / od.gtk-designer.toolbox.query-item-bounds -
	// real screen bounds via plain UIElement.PointToScreen, so a test can drive a REAL synthetic
	// mouse press/drag-move/release starting at the actual toolbox row and ending on the actual
	// rendered preview element, exercising DragDrop.DoDragDrop end to end.
	[DevFlowAction("od.mewui-designer.toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a MewUI Toolbox row for a given control type, for driving a synthetic mouse drag")]
	public static string QueryToolboxItemBounds(string typeName)
	{
		var v = Activate();
		if (v == null) return JsonSerializer.Serialize(new { success = false, error = "MewUI designer is not loaded" });
		if (!MewUIDesignerViewContent.ToolNames.Contains(typeName, StringComparer.Ordinal))
			return JsonSerializer.Serialize(new { success = false, error = "Unknown toolbox item: " + typeName });

		var toolbox = v.ToolboxControl;
		if (!v.SelectToolboxType(typeName)) return JsonSerializer.Serialize(new { success = false, error = "Toolbox controller rejected item: " + typeName });
		var toolboxItem = v.SelectedToolboxItem!;
		toolbox.ScrollIntoView(toolboxItem);
		toolbox.UpdateLayout();

		if (FindRealizedContainer(toolbox, toolboxItem) is not FrameworkElement container)
			return JsonSerializer.Serialize(new { success = false, error = "Toolbox row has no realized container (not scrolled into view?): " + typeName });

		container.BringIntoView();
		toolbox.UpdateLayout();

		if (!WaitUntilRowHitTestableAt(toolbox, container))
			return JsonSerializer.Serialize(new { success = false, error = "Toolbox row never settled at its own layout position (scroll/render lag): " + typeName });

		return JsonSerializer.Serialize(GetScreenBounds(container));
	}

	/// <summary>
	/// Blocks until an input hit-test at <paramref name="container"/>'s own centre resolves back to
	/// it, so the bounds handed out are ones a real synthetic click lands on. ScrollIntoView updates
	/// layout synchronously, but the pointer hits the last RENDERED frame, which lags a compose.
	/// InputHitTest deliberately: VisualTreeHelper.HitTest goes through the compositor scene on this
	/// stack and reports stale results for layout-only elements. Mirrors the GTK designer's copy.
	/// </summary>
	static bool WaitUntilRowHitTestableAt(ListBox toolbox, FrameworkElement container, int timeoutMilliseconds = 4000)
	{
		for (var elapsed = 0; ; elapsed += 100) {
			var centre = new Point(container.RenderSize.Width / 2, container.RenderSize.Height / 2);
			var inToolbox = container.TranslatePoint(centre, toolbox);
			if (toolbox.InputHitTest(inToolbox) is DependencyObject hit && ResolvesTo(hit, container))
				return true;
			if (elapsed >= timeoutMilliseconds)
				return false;
			PumpFor(100);
			toolbox.UpdateLayout();
		}

		static bool ResolvesTo(DependencyObject hit, FrameworkElement container)
		{
			for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
				if (ReferenceEquals(current, container))
					return true;
			return false;
		}
	}

	static void PumpFor(int milliseconds)
	{
		var frame = new System.Windows.Threading.DispatcherFrame();
		var timer = new System.Windows.Threading.DispatcherTimer(
			TimeSpan.FromMilliseconds(milliseconds),
			System.Windows.Threading.DispatcherPriority.Background,
			(_, _) => frame.Continue = false,
			System.Windows.Threading.Dispatcher.CurrentDispatcher);
		timer.Start();
		try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
		finally { timer.Stop(); }
	}

	[DevFlowAction("od.mewui-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a rendered MewUI element in the active designer's preview, for driving a synthetic mouse drag")]
	public static string QueryElementScreenBounds(string id)
	{
		var v = Activate();
		if (v == null) return JsonSerializer.Serialize(new { success = false, error = "MewUI designer is not loaded" });
		var target = v.FindPreviewTarget(id);
		if (target == null) return JsonSerializer.Serialize(new { success = false, error = "No rendered preview target for: " + id });
		return JsonSerializer.Serialize(GetScreenBounds(target));
	}

	static ListBoxItem? FindRealizedContainer(ItemsControl itemsControl, object item)
	{
		return FindInVisualTree(itemsControl);

		ListBoxItem? FindInVisualTree(DependencyObject node)
		{
			int count = VisualTreeHelper.GetChildrenCount(node);
			for (int i = 0; i < count; i++) {
				var child = VisualTreeHelper.GetChild(node, i);
				if (child is ListBoxItem listBoxItem && Equals(listBoxItem.DataContext, item))
					return listBoxItem;
				if (FindInVisualTree(child) is ListBoxItem found)
					return found;
			}
			return null;
		}
	}

	static object GetScreenBounds(UIElement element)
	{
		var topLeft = element.PointToScreen(new Point(0, 0));
		var bottomRight = element.PointToScreen(new Point(element.RenderSize.Width, element.RenderSize.Height));
		return new {
			success = true,
			x = topLeft.X, y = topLeft.Y,
			width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y,
			centerX = (topLeft.X + bottomRight.X) / 2, centerY = (topLeft.Y + bottomRight.Y) / 2
		};
	}

	static MewUIDesignerViewContent Activate()
	{
		if (SD.Workbench.ActiveViewContent is MewUIDesignerViewContent active) {
			var activeWindow = active.WorkbenchWindow;
			if (activeWindow != null)
				for (var i = 0; i < activeWindow.ViewContents.Count; i++)
					if (ReferenceEquals(activeWindow.ViewContents[i], active)) { activeWindow.SwitchView(i); break; }
			return active;
		}
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window == null) return null;
		for (var i = 0; i < window.ViewContents.Count; i++) if (window.ViewContents[i] is MewUIDesignerViewContent view) { window.SwitchView(i); return view; }
		return null;
	}
	static PropertyGrid? PropertyGrid => (SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;
}
