using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Designer.Remote;
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
			active = true, status = view.Status, loadError = view.LoadError, diagnostics = view.Diagnostics, hostLog = view.HostLog, rootId = view.RootId, rootIsWindow = view.RootIsWindow, windowTitle = view.WindowTitle, elementCount = view.ElementCount, elementIds = view.ElementIds, selectedId = view.SelectedId, selectedIds = view.SelectedIds, hostProcessId = view.HostProcessId, hostPoolKey = view.HostPoolKey, hostSessionId = view.HostSessionId, hostDocumentId = view.HostDocumentId, activeHostLeases = view.ActiveHostLeases, hostRecoveryCount = view.HostRecoveryCount, requestedRenderRevision = view.RequestedRenderRevision, renderedRevision = view.RenderedRevision, renderPending = view.IsRenderPending, nativeRenderer = "in-process GSK/Cairo", nativeFrame = view.HasNativeFrame, nativeFrameFingerprint = view.NativeFrameFingerprint, nativeFrameWidth = view.NativeFrameWidth, nativeFrameHeight = view.NativeFrameHeight, nativeBoundsCount = view.NativeBoundsCount,
			toolboxItemCount = view.ToolboxItemCount, toolboxFilterText = view.ToolboxFilterText, toolboxHosted = view.IsToolboxHosted, toolboxSearchHosted = (SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HasToolboxSearch == true, toolboxSelectedItem = view.SelectedToolboxType, zoomComboSelectedIndex = view.ZoomComboSelectedIndex, outlineHosted = view.IsOutlineHosted, outlineItemCount = view.OutlineItemCount,
			toolbarItemCount = view.ToolbarItemCount, toolbarItems = view.ToolbarItems, toolbarCapabilities = view.ToolbarCapabilities, zoom = view.Zoom, fitMeasured = view.FitMeasured, gridlines = view.Gridlines,
			propertyPadSelectedType = grid?.SelectedObject?.GetType().FullName,
			propertyPadPropertyCount = grid?.Properties?.Count ?? 0, canUndo = view.EnableUndo, canRedo = view.EnableRedo, hostAlive = view.IsHostAlive, documentCanUndo = view.DocumentCanUndo, documentCanRedo = view.DocumentCanRedo, loadCount = view.LoadCount
		});
	}
	/// <summary>Renders the live designer to a PNG. The GTK host's own render frame only carries the
	/// design content, so it cannot show how the canvas placed and scaled that content - which is where
	/// a distorted surface shows up. Capturing the real WPF visual covers the whole designer, chrome and
	/// all, and unlike an OS window capture it is independent of window position, occlusion and monitor
	/// scaling, so two runs can be compared pixel for pixel.</summary>
	[DevFlowAction("od.gtk-designer.screenshot", Description = "Render the active GTK designer to a PNG at its current size and zoom")]
	public static string Screenshot(string path)
	{
		var view = Activate();
		if (view == null)
			return JsonSerializer.Serialize(new { success = false, error = "No GTK designer is active." });
		var visual = view.Control as FrameworkElement;
		if (visual == null)
			return JsonSerializer.Serialize(new { success = false, error = "The GTK designer has no visual tree to capture." });
		// Layout has to be flushed first: after an insert, a zoom or a rehost, ActualWidth is still the
		// previous pass's value and the bitmap would be captured at the stale size.
		visual.UpdateLayout();
		var width = (int)Math.Ceiling(visual.ActualWidth);
		var height = (int)Math.Ceiling(visual.ActualHeight);
		if (width <= 0 || height <= 0)
			return JsonSerializer.Serialize(new {
				success = false,
				error = "The GTK designer has not been laid out yet (size " + visual.ActualWidth + "x" + visual.ActualHeight + ")."
			});
		var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		target.Render(visual);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(target));
		var full = Environment.ExpandEnvironmentVariables(path);
		var directory = System.IO.Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(directory))
			System.IO.Directory.CreateDirectory(directory);
		using (var stream = System.IO.File.Create(full))
			encoder.Save(stream);
		return JsonSerializer.Serialize(new { success = true, path = full, width, height });
	}

	[DevFlowAction("od.gtk-designer.select", Description = "Select a GtkBuilder object and populate the real Properties pad")]
	public static string Select(string id) { var view = Activate(); var ok = view?.SelectById(id) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId, propertyPadSelectedType = PropertyGrid?.SelectedObject?.GetType().FullName }); }
	[DevFlowAction("od.gtk-designer.multi-select", Description = "Replace the GTK designer selection set; first id is primary")]
	public static string MultiSelect(string ids) { var view = Activate(); var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var ok = view?.SelectByIds(list) == true; return DesignerDevFlowResults.Selection(ok, view?.SelectedIds); }
	[DevFlowAction("od.gtk-designer.bounds", Description = "Get GTK-native layout bounds for a GtkBuilder object")]
	public static string Bounds(string id) { var view = Activate(); var node = view?.FindById(id); return JsonSerializer.Serialize(new { success = node?.Width > 0 && node.Height > 0, id, x = node?.X ?? 0, y = node?.Y ?? 0, width = node?.Width ?? 0, height = node?.Height ?? 0 }); }
	[DevFlowAction("od.gtk-designer.hit-test", Description = "Select using the child GTK-native layout hit-test")]
	public static string HitTest(double x, double y) { var view = Activate(); var ok = view?.HitTest(x, y) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.toolbox.insert", Description = "Insert a GTK 4 control from the real Tools catalogue")]
	public static string Insert(string className) { var view = Activate(); var known = view.IsToolName(className); return JsonSerializer.Serialize(new { success = known && view?.Add(className) == true, elementCount = view?.ElementCount ?? 0, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.toolbox.filter", Description = "Filter the GTK Toolbox using the common catalogue semantics")]
	public static string FilterToolbox(string text) { var view = Activate(); view?.FilterToolbox(text); return DesignerDevFlowResults.ToolboxFilter(view != null, view?.ToolboxFilterText, view?.ToolboxItemCount ?? 0, view?.SelectedToolboxType); }
	[DevFlowAction("od.gtk-designer.properties.edit", Description = "Edit through the real shared Properties pad PropertyItem")]
	public static string EditProperty(string propertyName, string value)
	{
		var view = Activate(); var grid = PropertyGrid; if (view == null || grid?.SelectedObject == null) return DesignerDevFlowResults.Failure("GTK selection is not bound to the shared Properties pad");
		var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(p => p.PropertyName == propertyName);
		if (item == null) return JsonSerializer.Serialize(new { success = false, error = "Property not found", propertyNames = grid.Properties?.OfType<PropertyItem>().Select(p => p.PropertyName).ToArray() });
		item.Value = value; return JsonSerializer.Serialize(new { success = true, selectedIds = view.SelectedIds, primarySelectedId = view.SelectedId, propertyName, after = item.Value?.ToString() });
	}
	[DevFlowAction("od.gtk-designer.properties.describe", Description = "Describe the selected object's Properties-pad items: descriptor name, display name, GTK category, .NET editor type, enum choices and whether Reset is available - the GIR-typed property contract")]
	public static string DescribeProperties()
	{
		var view = Activate(); var selected = PropertyGrid?.SelectedObject;
		if (view == null || selected == null) return DesignerDevFlowResults.Failure("GTK selection is not bound to the shared Properties pad");
		var items = TypeDescriptor.GetProperties(selected).Cast<PropertyDescriptor>().Select(p => {
			var converter = p.Converter; var context = new DescriptorContext(selected, p);
			var choices = converter != null && converter.GetStandardValuesSupported(context) ? converter.GetStandardValues(context)?.Cast<object>().Select(v => v.ToString()).ToArray() : null;
			return new { name = p.Name, displayName = p.DisplayName, category = p.Category, description = p.Description, type = p.PropertyType.Name, readOnly = p.IsReadOnly,
				choices, exclusive = choices != null && converter!.GetStandardValuesExclusive(context), canReset = p.CanResetValue(selected), value = p.GetValue(selected)?.ToString() };
		}).ToArray();
		return JsonSerializer.Serialize(new { success = true, selectedId = view.SelectedId, count = items.Length, items });
	}
	[DevFlowAction("od.gtk-designer.properties.reset", Description = "Reset a property through the selected Properties-pad descriptor (removes it from the .ui so GTK's default applies)")]
	public static string ResetProperty(string propertyName)
	{
		var view = Activate(); var selected = PropertyGrid?.SelectedObject;
		var descriptor = selected == null ? null : TypeDescriptor.GetProperties(selected).Find(propertyName, false);
		if (view == null || descriptor == null) return DesignerDevFlowResults.Failure("Property not found: " + propertyName);
		if (!descriptor.CanResetValue(selected!)) return JsonSerializer.Serialize(new { success = false, error = "Property cannot be reset (not set in the file?)", propertyName });
		descriptor.ResetValue(selected!);
		return JsonSerializer.Serialize(new { success = true, propertyName, selectedId = view.SelectedId });
	}
	sealed class DescriptorContext(object instance, PropertyDescriptor descriptor) : ITypeDescriptorContext
	{
		public IContainer? Container => null; public object? Instance => instance; public PropertyDescriptor? PropertyDescriptor => descriptor;
		public object? GetService(Type serviceType) => null; public bool OnComponentChanging() => true; public void OnComponentChanged() { }
	}
	[DevFlowAction("od.gtk-designer.drop-plan", Description = "Where a toolbox drop at this design point would go (container, box index or grid cell, and the indicator rectangle drawn while dragging) - GtkDropPlanner over GTK's measured bounds")]
	public static string DropPlan(double x, double y)
	{
		var view = Activate(); var plan = view?.PlanDropAt(x, y);
		return JsonSerializer.Serialize(new { success = plan != null, containerId = plan?.ContainerId, index = plan?.Index, column = plan?.Cell?.Column, row = plan?.Cell?.Row,
			indicator = plan == null ? null : new { x = plan.Indicator.X, y = plan.Indicator.Y, width = plan.Indicator.Width, height = plan.Indicator.Height } });
	}
	[DevFlowAction("od.gtk-designer.toolbox.drop-at", Description = "Drop a GTK toolbox item at a design point through the same planned insertion a real canvas drop performs")]
	public static string DropAt(string className, double x, double y)
	{
		var view = Activate(); var plan = view?.PlanDropAt(x, y);
		var ok = view != null && plan != null && view.IsToolName(className) && view.DropAt(plan, className, x, y);
		return JsonSerializer.Serialize(new { success = ok, containerId = plan?.ContainerId, selectedId = view?.SelectedId, elementCount = view?.ElementCount ?? 0 });
	}
	[DevFlowAction("od.gtk-designer.delete", Description = "Delete the selected GTK object")]
	public static string Delete() { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.DeleteSelected() == true, elementCount = view?.ElementCount ?? 0 }); }
	[DevFlowAction("od.gtk-designer.signal.set", Description = "Set a GtkBuilder signal handler on the selected object")]
	public static string SetSignal(string signalName, string handlerName) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.SetSelectedSignal(signalName, handlerName) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.properties.event.bind", Description = "Bind a GTK signal through the selected Properties-pad adapter")]
	public static string BindEvent(string eventName) { var view = Activate(); var selected = PropertyGrid?.SelectedObject; var exists = selected != null && TypeDescriptor.GetEvents(selected).Find(eventName, false) != null; if (exists && selected is IEventBindingHost host) host.BindEvent(eventName); return JsonSerializer.Serialize(new { success = exists && selected is IEventBindingHost, eventName, selectedId = view?.SelectedId }); }
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
	public static string RestartHost() { var view = Activate(); var oldPid = view?.HostProcessId ?? 0; view?.RestartDesignHost(); return DesignerDevFlowResults.HostRestart(view != null, oldPid, view?.HostProcessId ?? 0); }
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
		if (!view.IsToolName(typeName))
			return JsonSerializer.Serialize(new { success = false, error = "Unknown toolbox item: " + typeName });

		var toolbox = view.ToolboxControl;
		if (!view.SelectToolboxType(typeName)) return JsonSerializer.Serialize(new { success = false, error = "Toolbox controller rejected item: " + typeName });
		var toolboxItem = view.SelectedToolboxItem!;
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
	/// Blocks until an input hit-test at <paramref name="container"/>'s own centre actually
	/// resolves back to it, so the bounds we hand out are ones a real synthetic click will land on.
	///
	/// ScrollIntoView/BringIntoView update layout synchronously (so PointToScreen immediately
	/// reports the post-scroll position), but what the pointer actually hits is the last RENDERED
	/// frame, which lags by at least one compose. Measured: querying GtkSwitch's row returned
	/// coordinates that pressed the adjacent GtkCheckButton row instead - and because a real press
	/// re-selects whatever row it lands on, the drag then carried the wrong control type and the
	/// test dropped a GtkCheckButton while asking for a GtkSwitch. Pumping a nested frame lets
	/// render frames run; re-checking (rather than sleeping a fixed amount) keeps this as short as
	/// possible and self-verifying.
	///
	/// Uses InputHitTest deliberately: VisualTreeHelper.HitTest routes through the compositor scene
	/// on this stack and reports stale/incorrect results for layout-only elements.
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

	[DevFlowAction("od.gtk-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a rendered GtkBuilder object in the active designer's native preview, for driving a synthetic mouse drag")]
	public static string QueryElementScreenBounds(string id)
	{
		var view = Activate();
		if (view == null) return JsonSerializer.Serialize(new { success = false, error = "GTK designer is not loaded" });
		if (view.ScreenBoundsOf(id) is not { } bounds) return JsonSerializer.Serialize(new { success = false, error = "No rendered native target for: " + id });
		return JsonSerializer.Serialize(new {
			success = true,
			x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height,
			centerX = bounds.X + bounds.Width / 2, centerY = bounds.Y + bounds.Height / 2
		});
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
		if (SD.Workbench.ActiveViewContent is GtkDesignerViewContent active) {
			var activeWindow = active.WorkbenchWindow;
			if (activeWindow != null)
				for (var i = 0; i < activeWindow.ViewContents.Count; i++)
					if (ReferenceEquals(activeWindow.ViewContents[i], active)) { activeWindow.SwitchView(i); break; }
			return active;
		}
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window != null)
			for (var i = 0; i < window.ViewContents.Count; i++) if (window.ViewContents[i] is GtkDesignerViewContent view) { window.SwitchView(i); return view; }
		// Another document is in front - e.g. the behavior file a signal binding just opened at
		// the new handler. Bring the open GTK designer back, as clicking its tab would.
		var designer = SD.Workbench.ViewContentCollection.OfType<GtkDesignerViewContent>().FirstOrDefault();
		var designerWindow = designer?.WorkbenchWindow;
		if (designer == null || designerWindow == null) return null;
		designerWindow.SelectWindow();
		for (var i = 0; i < designerWindow.ViewContents.Count; i++)
			if (ReferenceEquals(designerWindow.ViewContents[i], designer)) { designerWindow.SwitchView(i); break; }
		return designer;
	}
}
