using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.GtkDesigner;

[DevFlowUIThread]
public static class GtkDesignerDevFlowActions
{
	[DevFlowAction("od.gtk-designer.status", Description = "Inspect the active GTK 4 designer and its real shared pads")]
	public static string Status()
	{
		var view = Activate(); var grid = PropertyGrid;
		return view == null ? JsonSerializer.Serialize(new { active = false }) : JsonSerializer.Serialize(new {
			active = true, status = view.Status, diagnostics = view.Diagnostics, hostLog = view.HostLog, rootId = view.RootId, elementCount = view.ElementCount, selectedId = view.SelectedId, hostProcessId = view.HostProcessId, hostPoolKey = view.HostPoolKey, hostSessionId = view.HostSessionId, hostDocumentId = view.HostDocumentId, activeHostLeases = view.ActiveHostLeases, hostRecoveryCount = view.HostRecoveryCount, requestedRenderRevision = view.RequestedRenderRevision, renderedRevision = view.RenderedRevision, renderPending = view.IsRenderPending, nativeRenderer = "in-process GSK/Cairo", nativeFrame = view.HasNativeFrame, nativeFrameFingerprint = view.NativeFrameFingerprint, nativeFrameWidth = view.NativeFrameWidth, nativeFrameHeight = view.NativeFrameHeight, nativeBoundsCount = view.NativeBoundsCount,
			toolboxItemCount = view.ToolboxItemCount, toolboxHosted = view.IsToolboxHosted, outlineHosted = view.IsOutlineHosted, outlineItemCount = view.OutlineItemCount,
			toolbarItemCount = view.ToolbarItemCount, toolbarItems = view.ToolbarItems, toolbarCapabilities = view.ToolbarCapabilities, zoom = view.Zoom, fitMeasured = view.FitMeasured, gridlines = view.Gridlines,
			propertyPadSelectedType = grid?.SelectedObject?.GetType().FullName,
			propertyPadPropertyCount = grid?.Properties?.Count ?? 0, canUndo = view.EnableUndo, canRedo = view.EnableRedo,
			debugMouseDownCount = view.DebugMouseDownCount, debugMouseMoveCount = view.DebugMouseMoveCount, debugMouseMovePressedCount = view.DebugMouseMovePressedCount, debugDragStartCount = view.DebugDragStartCount, debugDragOverCount = view.DebugDragOverCount, debugDropCount = view.DebugDropCount
		});
	}
	[DevFlowAction("od.gtk-designer.select", Description = "Select a GtkBuilder object and populate the real Properties pad")]
	public static string Select(string id) { var view = Activate(); var ok = view?.SelectById(id) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId, propertyPadSelectedType = PropertyGrid?.SelectedObject?.GetType().FullName }); }
	[DevFlowAction("od.gtk-designer.bounds", Description = "Get GTK-native layout bounds for a GtkBuilder object")]
	public static string Bounds(string id) { var view = Activate(); var node = view?.FindById(id); return JsonSerializer.Serialize(new { success = node?.Width > 0 && node.Height > 0, id, x = node?.X ?? 0, y = node?.Y ?? 0, width = node?.Width ?? 0, height = node?.Height ?? 0 }); }
	[DevFlowAction("od.gtk-designer.hit-test", Description = "Select using the child GTK-native layout hit-test")]
	public static string HitTest(double x, double y) { var view = Activate(); var ok = view?.HitTest(x, y) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.toolbox.insert", Description = "Insert a GTK 4 control from the real Tools catalogue")]
	public static string Insert(string className) { var view = Activate(); var known = GtkDesignerViewContent.ToolNames.Contains(className, StringComparer.Ordinal); return JsonSerializer.Serialize(new { success = known && view?.Add(className) == true, elementCount = view?.ElementCount ?? 0, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.properties.edit", Description = "Edit through the real shared Properties pad PropertyItem")]
	public static string EditProperty(string propertyName, string value)
	{
		var view = Activate(); var grid = PropertyGrid; if (view == null || grid?.SelectedObject is not GtkPropertyAdapter) return JsonSerializer.Serialize(new { success = false, error = "GTK adapter is not selected in the shared Properties pad" });
		var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(p => p.PropertyName == propertyName);
		if (item == null) return JsonSerializer.Serialize(new { success = false, error = "Property not found", propertyNames = grid.Properties?.OfType<PropertyItem>().Select(p => p.PropertyName).ToArray() });
		item.Value = value; return JsonSerializer.Serialize(new { success = true, selectedId = view.SelectedId, propertyName, after = item.Value?.ToString() });
	}
	[DevFlowAction("od.gtk-designer.delete", Description = "Delete the selected GTK object")]
	public static string Delete() { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.DeleteSelected() == true, elementCount = view?.ElementCount ?? 0 }); }
	[DevFlowAction("od.gtk-designer.signal.set", Description = "Set a GtkBuilder signal handler on the selected object")]
	public static string SetSignal(string signalName, string handlerName) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.SetSelectedSignal(signalName, handlerName) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.reorder", Description = "Move the selected GTK child within its parent")]
	public static string Reorder(int delta) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.ReorderSelected(delta) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.pointer-reorder", Description = "Exercise the native-bounds pointer reorder mapping between sibling objects")]
	public static string PointerReorder(string sourceId, string targetId) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.PointerReorder(sourceId, targetId) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.undo", Description = "Undo a GTK designer source edit")]
	public static string Undo() { var view = Activate(); view?.Undo(); return Status(); }
	[DevFlowAction("od.gtk-designer.redo", Description = "Redo a GTK designer source edit")]
	public static string Redo() { var view = Activate(); view?.Redo(); return Status(); }
	[DevFlowAction("od.gtk-designer.refresh", Description = "Reload the GTK design from its source")]
	public static string Refresh() { var view = Activate(); view?.RefreshDesign(); return JsonSerializer.Serialize(new { success = view != null, hostProcessId = view?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.gtk-designer.restart-host", Description = "Restart the isolated GTK designer host")]
	public static string RestartHost() { var view = Activate(); var oldPid = view?.HostProcessId ?? 0; view?.RestartDesignHost(); return JsonSerializer.Serialize(new { success = view != null, oldHostProcessId = oldPid, hostProcessId = view?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.gtk-designer.terminate-host", Description = "Terminate the shared GTK host to verify automatic recovery")]
	public static string TerminateHost() { var view = Activate(); var oldPid = view?.HostProcessId ?? 0; view?.TerminateDesignHost(); return JsonSerializer.Serialize(new { success = view != null, oldHostProcessId = oldPid }); }
	[DevFlowAction("od.gtk-designer.show-source", Description = "Switch from the GTK designer to its source document")]
	public static string ShowSource() { var view = Activate(); view?.ShowSource(); return JsonSerializer.Serialize(new { success = view != null }); }
	[DevFlowAction("od.gtk-designer.zoom", Description = "Set the common designer toolbar zoom")]
	public static string Zoom(double value) { var view = Activate(); if (view != null) view.Zoom = value; return JsonSerializer.Serialize(new { success = view != null, zoom = view?.Zoom ?? 0 }); }
	[DevFlowAction("od.gtk-designer.fit", Description = "Fit the GTK design using the common canvas toolbar behavior")]
	public static string Fit() { var view = Activate(); view?.FitDesign(); return JsonSerializer.Serialize(new { success = view != null, zoom = view?.Zoom ?? 0, measured = view?.FitMeasured ?? false }); }
	[DevFlowAction("od.gtk-designer.gridlines", Description = "Toggle GTK design-space gridlines")]
	public static string Gridlines(bool enabled) { var view = Activate(); view?.ShowGridlines(enabled); return JsonSerializer.Serialize(new { success = view != null, gridlines = view?.Gridlines ?? false }); }

	// Real screen bounds for a Toolbox row / a native-rendered design-surface target, computed
	// the same way od.wpf-designer.toolbox.query-item-bounds does (plain UIElement.PointToScreen -
	// the ToolsPad and design surface always share the single main window). Lets a test drive a
	// REAL synthetic mouse press/drag-move/release (od.ui/actions) starting at the actual toolbox
	// row and ending on the actual rendered target, exercising DragDrop.DoDragDrop end to end.
	[DevFlowAction("od.gtk-designer.toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a GTK Toolbox row for a given control type, for driving a synthetic mouse drag")]
	public static string QueryToolboxItemBounds(string typeName)
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { success = false, error = "GTK designer is not loaded" });
		if (!GtkDesignerViewContent.ToolNames.Contains(typeName, StringComparer.Ordinal))
			return JsonSerializer.Serialize(new { success = false, error = "Unknown toolbox item: " + typeName });

		var toolbox = view.ToolboxControl;
		toolbox.SelectedItem = typeName;
		toolbox.ScrollIntoView(typeName);
		toolbox.UpdateLayout();

		if (FindRealizedContainer(toolbox, typeName) is not FrameworkElement container)
			return JsonSerializer.Serialize(new { success = false, error = "Toolbox row has no realized container (not scrolled into view?): " + typeName });

		container.BringIntoView();
		toolbox.UpdateLayout();
		return JsonSerializer.Serialize(GetScreenBounds(container));
	}

	[DevFlowAction("od.gtk-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a rendered GtkBuilder object in the active designer's native preview, for driving a synthetic mouse drag")]
	public static string QueryElementScreenBounds(string id)
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { success = false, error = "GTK designer is not loaded" });
		var target = view.FindNativeTarget(id);
		if (target == null) return JsonSerializer.Serialize(new { success = false, error = "No rendered native target for: " + id });
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

	static PropertyGrid? PropertyGrid => (SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;
	static GtkDesignerViewContent? Activate()
	{
		if (SD.Workbench.ActiveViewContent is GtkDesignerViewContent active) return active;
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow; if (window == null) return null;
		for (var i = 0; i < window.ViewContents.Count; i++) if (window.ViewContents[i] is GtkDesignerViewContent view) { window.SwitchView(i); return view; }
		return null;
	}
}
