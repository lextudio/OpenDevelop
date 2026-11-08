using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Presentation;
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
			active = true, status = view.Status, diagnosticText = view.DiagnosticText, loadError = view.LoadError, diagnostics = view.Diagnostics, hostLog = view.HostLog, rootId = view.RootId, elementCount = view.ElementCount, elementIds = view.ElementIds, selectedId = view.SelectedId, selectedIds = view.SelectedIds, hostProcessId = view.HostProcessId, hostPoolKey = view.HostPoolKey, hostSessionId = view.HostSessionId, hostDocumentId = view.HostDocumentId, activeHostLeases = view.ActiveHostLeases, hostRecoveryCount = view.HostRecoveryCount, requestedRenderRevision = view.RequestedRenderRevision, renderedRevision = view.RenderedRevision, renderPending = view.IsRenderPending, nativeRenderer = "in-process GSK/Cairo", nativeFrame = view.HasNativeFrame, nativeFrameFingerprint = view.NativeFrameFingerprint, nativeFrameWidth = view.NativeFrameWidth, nativeFrameHeight = view.NativeFrameHeight, nativeBoundsCount = view.NativeBoundsCount, canvasFrameShown = view.CanvasFrameShown, canvasDisplay = view.CanvasDisplayDiagnostic,
			toolboxItemCount = view.ToolboxItemCount, toolboxDrag = SharedToolbox.Instance.DragDiagnostic, toolboxFilterText = view.ToolboxFilterText, toolboxHosted = view.IsToolboxHosted, toolboxSearchHosted = (SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HasToolboxSearch == true, toolboxSelectedItem = view.SelectedToolboxType, zoomComboSelectedIndex = view.ZoomComboSelectedIndex, outlineHosted = view.IsOutlineHosted, outlineItemCount = view.OutlineItemCount,
			toolbarItemCount = view.ToolbarItemCount, toolbarItems = view.ToolbarItems, toolbarCapabilities = view.ToolbarCapabilities, zoom = view.Zoom, fitMeasured = view.FitMeasured, gridlines = view.Gridlines, previewTheme = view.PreviewTheme, canvasTheme = view.CanvasTheme,
			propertyPadSelectedType = grid?.SelectedObject?.GetType().FullName,
			propertyPadPropertyCount = grid?.Properties?.Count ?? 0, canUndo = view.EnableUndo, canRedo = view.EnableRedo, hostAlive = view.IsHostAlive, documentCanUndo = view.DocumentCanUndo, documentCanRedo = view.DocumentCanRedo, loadCount = view.LoadCount,
			canvasHasKeyboardFocus = view.CanvasHasKeyboardFocus
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
		var full = Environment.ExpandEnvironmentVariables(path);
		// The composed canvas is what we want, but WPF's imaging stack is not available on every host
		// this runs on - LibreWPF on macOS has no native encoder and PngBitmapEncoder throws
		// DllNotFoundException. Falling back to the GTK host's own PNG still answers the question the
		// capture exists for: that image is exactly what GTK rendered, so if it is undistorted, whatever
		// looks wrong is the canvas placing it rather than GTK producing it. The source is reported so the
		// two are never confused.
		var fallback = view.RenderPreviewPng();
		if (fallback != null) {
			try {
				var probe = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
				probe.Render(visual);
				var composedEncoder = new PngBitmapEncoder();
				composedEncoder.Frames.Add(BitmapFrame.Create(probe));
				using (var composed = System.IO.File.Create(full)) composedEncoder.Save(composed);
				return JsonSerializer.Serialize(new { success = true, source = "wpf-visual", path = full, width, height });
			} catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or TypeInitializationException) {
				System.IO.File.WriteAllBytes(full, fallback);
				var declared = view.RenderPreviewPngSize;
				return JsonSerializer.Serialize(new {
					success = true, source = "gtk-host-frame", path = full,
					frameWidth = declared?.Width, frameHeight = declared?.Height,
					note = "WPF imaging unavailable on this host (" + ex.GetType().Name + "); wrote GTK's own render instead. "
						+ "This is the design content only, without the canvas chrome, so it shows what GTK produced."
				});
			}
		}
		// No host frame to fall back on either, so the composed canvas is the only thing left to try.
		var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		target.Render(visual);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(target));
		var directory = System.IO.Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(directory))
			System.IO.Directory.CreateDirectory(directory);
		using (var stream = System.IO.File.Create(full))
			encoder.Save(stream);
		return JsonSerializer.Serialize(new { success = true, source = "wpf-visual", path = full, width, height });
	}

	[DevFlowAction("od.gtk-designer.select", Description = "Select a GtkBuilder object and populate the real Properties pad")]
	public static string Select(string id) { var view = Activate(); var ok = view?.SelectById(id) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId, propertyPadSelectedType = PropertyGrid?.SelectedObject?.GetType().FullName }); }
	[DevFlowAction("od.gtk-designer.context-commands", Description = "List the GTK design surface context menu's commands (header path, command, enabled) as opening the menu would evaluate them")]
	public static string GetContextCommands()
	{
		var view = Activate();
		return view == null ? DesignerDevFlowResults.Failure("GTK designer is not active")
			: JsonSerializer.Serialize(new { success = true, commands = view.DescribeContextCommands().Select(entry => new { header = entry.Header, command = entry.Command, enabled = entry.Enabled }) });
	}
	[DevFlowAction("od.gtk-designer.multi-select", Description = "Replace the GTK designer selection set; first id is primary")]
	public static string MultiSelect(string ids) { var view = Activate(); var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var ok = view?.SelectByIds(list) == true; return DesignerDevFlowResults.Selection(ok, view?.SelectedIds); }
	[DevFlowAction("od.gtk-designer.surface-geometry", Description = "Report the shared design surface geometry (rendered native frame, selection outline, handle and the scroll viewport) in screen coordinates - the same probe od.wpf-designer.surface-geometry publishes, so a synthetic pointer gesture can start on real viewport background")]
	public static string GetSurfaceGeometry()
	{
		var view = Activate();
		return view == null ? JsonSerializer.Serialize(new { available = false })
			: JsonSerializer.Serialize(DesignerSurfaceGeometryProbe.ToJson(view.SurfaceGeometry()));
	}

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
	[DevFlowAction("od.gtk-designer.properties.edit-items", Description = "Click a GtkDropDown Items row's real \"…\" button and apply an edit script to the collection dialog's model: ';'-separated add:<afterIndex>:<text>, remove:<i>, up:<i>, down:<i>, rename:<i>:<text>, or 'cancel'")]
	public static string EditItemsThroughPropertiesPad(string propertyName, string script)
	{
		var view = Activate(); var grid = PropertyGrid;
		if (view == null || grid?.SelectedObject is not GtkPropertyAdapter)
			return DesignerDevFlowResults.Failure("Properties pad has no selected GTK design item");
		var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => candidate.PropertyName == propertyName);
		var button = (item?.Editor as DockPanel)?.Children.OfType<Button>()
			.FirstOrDefault(candidate => candidate.Name == GtkItemsCollectionEditor.EditButtonName);
		if (button == null || !button.IsEnabled)
			return DesignerDevFlowResults.Failure("Properties pad did not render an enabled items editor button: " + propertyName);
		var result = CollectionItemsEditing.RunScriptedEdit(button, script);
		return result.Success
			? JsonSerializer.Serialize(new { success = true, selectedId = view.SelectedId, before = result.Before, after = result.After, committed = result.Committed })
			: DesignerDevFlowResults.Failure(result.Error ?? "Items edit failed");
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
	public static async Task<string> Delete() { var view = Activate(); var success = view != null && await view.DeleteSelectedAsync(); return JsonSerializer.Serialize(new { success, elementCount = view?.ElementCount ?? 0 }); }
	[DevFlowAction("od.gtk-designer.clipboard", Description = "Run the design surface's cut|copy|paste|duplicate|move-first|move-earlier|move-later|move-last|unwrap|paste-here|align-<h|v>-<fill|start|center|end>|span-<column|row>-<more|less>|grid-insert-row|grid-insert-column|grid-delete-row|grid-delete-column|wrap-<GtkClass> command on the selection, as its context menu does")]
	public static async Task<string> Clipboard(string command)
	{
		var view = Activate();
		var ok = view != null && command switch {
			"copy" => view.CopySelection(),
			"cut" => view.CopySelection() && await view.DeleteSelectedAsync(),
			"paste" => view.PasteIntoSelection(),
			"duplicate" => view.DuplicateSelection(),
			"move-first" or "move-earlier" or "move-later" or "move-last" => view.MoveSelected(command),
			"unwrap" => view.UnwrapSelected(),
			"paste-here" => view.PasteHere(),
			_ when command.StartsWith("align-", StringComparison.Ordinal) => view.AlignSelected(command),
			_ when command.StartsWith("span-", StringComparison.Ordinal) => view.SpanSelected(command),
			"grid-insert-row" or "grid-insert-column" => view.InsertGridTrack(command),
			"grid-delete-row" or "grid-delete-column" => view.DeleteGridTrack(command),
			_ when command.StartsWith("wrap-", StringComparison.Ordinal) => view.WrapSelected(command.Substring("wrap-".Length)),
			_ => false
		};
		return JsonSerializer.Serialize(new { success = ok, selectedIds = view?.SelectedIds ?? Array.Empty<string>(), elementCount = view?.ElementCount ?? 0 });
	}
	[DevFlowAction("od.gtk-designer.signal.set", Description = "Set a GtkBuilder signal handler on the selected object")]
	public static string SetSignal(string signalName, string handlerName) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.SetSelectedSignal(signalName, handlerName) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.notebook-action.add", Description = "Add a GTK notebook action-start or action-end widget to the selected GtkNotebook")]
	public static string AddNotebookAction(bool atEnd, string className = "GtkButton") { var view = Activate(); var ok = view?.AddNotebookAction(atEnd, className) == true; return JsonSerializer.Serialize(new { success = ok, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.properties.event.bind", Description = "Bind a GTK signal through the selected Properties-pad adapter")]
	public static string BindEvent(string eventName) { var view = Activate(); var selected = PropertyGrid?.SelectedObject; var exists = selected != null && TypeDescriptor.GetEvents(selected).Find(eventName, false) != null; if (exists && selected is IEventBindingHost host) host.BindEvent(eventName); return JsonSerializer.Serialize(new { success = exists && selected is IEventBindingHost, eventName, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.factory-template", Description = "Read the selected list/grid/column view's GtkBuilderListItemFactory bytes (the cell template)")]
	public static string GetFactoryTemplate() { var view = Activate(); var template = view?.FactoryTemplate(); return JsonSerializer.Serialize(new { success = template != null, template }); }
	[DevFlowAction("od.gtk-designer.factory-template.set", Description = "Replace the selected list/grid/column view's factory template as one undo step; invalid XML is refused")]
	public static string SetFactoryTemplate(string template) { var view = Activate(); var ok = view?.SetFactoryTemplate(template) == true; return JsonSerializer.Serialize(new { success = ok, template = view?.FactoryTemplate() }); }
	[DevFlowAction("od.gtk-designer.factory-class", Description = "The class of the selected list view, grid view or column's inline list-item factory, or null")]
	public static string GetFactoryClass() { var view = Activate(); return JsonSerializer.Serialize(new { success = view != null, factoryClass = view?.FactoryClass() }); }
	[DevFlowAction("od.gtk-designer.factory-class.set", Description = "Switch the selected list view, grid view or column's list-item factory to a builder, signal or registered custom GtkListItemFactory GType as one undo step; the signal factory's setup/bind stubs are generated")]
	public static string SetFactoryClass(string factoryClass) { var view = Activate(); var ok = view?.SetFactoryClass(factoryClass) == true; return JsonSerializer.Serialize(new { success = ok, factoryClass = view?.FactoryClass() }); }
	[DevFlowAction("od.gtk-designer.column-sortable", Description = "Whether the selected GtkColumnViewColumn has a sorter (null when it is not a column of a column view)")]
	public static string GetColumnSortable() { var view = Activate(); return JsonSerializer.Serialize(new { success = view != null, sortable = view?.ColumnSortable() }); }
	[DevFlowAction("od.gtk-designer.column-sortable.set", Description = "Make the selected GtkColumnViewColumn sortable (a string sorter, plus a sort model driven by the view's sorter when needed) or not, as one undo step; a refusal returns success=false and shows under the canvas")]
	public static string SetColumnSortable(bool sortable) { var view = Activate(); var ok = view?.SetColumnSortable(sortable) == true; return JsonSerializer.Serialize(new { success = ok, sortable = view?.ColumnSortable() }); }
	[DevFlowAction("od.gtk-designer.model-data", Description = "The class of the selected list view's innermost inline data model (GtkStringList, GListStore, ...), or null")]
	public static string GetModelData() { var view = Activate(); return JsonSerializer.Serialize(new { success = view != null, modelData = view?.ModelData() }); }
	[DevFlowAction("od.gtk-designer.model-data.set", Description = "Switch the selected list view's inline data model to GtkStringList or GListStore as one undo step (the string items are dropped when going to a store); a refusal returns success=false and shows under the canvas")]
	public static string SetModelData(string modelClass) { var view = Activate(); var ok = view?.SetModelData(modelClass) == true; return JsonSerializer.Serialize(new { success = ok, modelData = view?.ModelData() }); }
	[DevFlowAction("od.gtk-designer.item-type", Description = "The item-type of the selected GListStore (or of the store a selected list view ends in): empty when unset, null when there is no store")]
	public static string GetItemType() { var view = Activate(); return JsonSerializer.Serialize(new { success = view != null, itemType = view?.ItemType() }); }
	[DevFlowAction("od.gtk-designer.item-type.set", Description = "Set the selected GListStore's item-type to a GType name (empty removes it) as one undo step; a refusal returns success=false and shows under the canvas")]
	public static string SetItemType(string typeName) { var view = Activate(); var ok = view?.SetItemType(typeName) == true; return JsonSerializer.Serialize(new { success = ok, itemType = view?.ItemType() }); }
	[DevFlowAction("od.gtk-designer.item-property", Description = "The selected non-string list's property used by its builder factory, sorter and filter, or null until chosen")]
	public static string GetItemProperty() { var view = Activate(); return JsonSerializer.Serialize(new { success = view != null, itemProperty = view?.ItemProperty() }); }
	[DevFlowAction("od.gtk-designer.item-property.set", Description = "Set the selected GListStore object's displayed/sorted/filtered property as one undo step")]
	public static string SetItemProperty(string propertyName) { var view = Activate(); var ok = view?.SetItemProperty(propertyName) == true; return JsonSerializer.Serialize(new { success = ok, itemProperty = view?.ItemProperty() }); }
	[DevFlowAction("od.gtk-designer.model-chain", Description = "Read the selected list/grid/column view's model chain, outermost first (selection, sort, filter, data)")]
	public static string GetModelChain() { var view = Activate(); var chain = view?.ModelChain(); return JsonSerializer.Serialize(new { success = view != null, chain }); }
	[DevFlowAction("od.gtk-designer.model-wrapper.set", Description = "Add (add=true) or remove the selected list/grid/column view's 'sort' or 'filter' model as one undo step; a refusal returns success=false and shows under the canvas")]
	public static string SetModelWrapper(string kind, bool add) { var view = Activate(); var ok = view?.SetModelWrapper(kind, add) == true; return JsonSerializer.Serialize(new { success = ok, chain = view?.ModelChain() }); }
	[DevFlowAction("od.gtk-designer.selection-model", Description = "Read the selected list/grid/column view's selection model class (GtkNoSelection, GtkSingleSelection or GtkMultiSelection), or null")]
	public static string GetSelectionModel() { var view = Activate(); var model = view?.SelectionModel(); return JsonSerializer.Serialize(new { success = view != null, selectionModel = model }); }
	[DevFlowAction("od.gtk-designer.selection-model.set", Description = "Set the selected list/grid/column view's selection model (one undo step); a model that is not a selection model is wrapped")]
	public static string SetSelectionModel(string selectionClass) { var view = Activate(); var ok = view?.SetSelectionModel(selectionClass) == true; return JsonSerializer.Serialize(new { success = ok, selectionModel = view?.SelectionModel() }); }
	[DevFlowAction("od.gtk-designer.menu-items", Description = "Read the selected popover/menu button's top-level menu items as the shared item/section/submenu tree")]
	public static string GetMenuItems() { var view = Activate(); var items = view?.MenuItems(); return JsonSerializer.Serialize(new { success = items != null, items }); }
	[DevFlowAction("od.gtk-designer.menu-items.set", Description = "Replace the selected popover/menu button's menu from the shared outline (one undo step): 'label=action' items, '[section] label' and '[submenu] label' with their entries indented two spaces below; ';' or a newline separates lines")]
	public static string SetMenuItems(string items)
	{
		var view = Activate();
		var before = view?.MenuItems() ?? Array.Empty<DesignerMenuNode>();
		var parsed = DesignerMenuOutline.Parse((items ?? "").Replace(';', '\n'), before, out var error);
		var ok = parsed != null && view?.SetMenuItems(parsed) == true;
		return JsonSerializer.Serialize(new { success = ok, error, items = view?.MenuItems() });
	}
	[DevFlowAction("od.gtk-designer.reorder", Description = "Move the selected GTK child within its parent")]
	public static string Reorder(int delta) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.ReorderSelected(delta) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.pointer-reorder", Description = "Exercise the native-bounds pointer reorder mapping between sibling objects")]
	public static string PointerReorder(string sourceId, string targetId) { var view = Activate(); return JsonSerializer.Serialize(new { success = view?.PointerReorder(sourceId, targetId) == true, selectedId = view?.SelectedId }); }
	[DevFlowAction("od.gtk-designer.undo", Description = "Undo a GTK designer source edit")]
	public static async Task<string> Undo() { var view = Activate(); if (view != null) await view.UndoAsync(); return Status(); }
	[DevFlowAction("od.gtk-designer.redo", Description = "Redo a GTK designer source edit")]
	public static async Task<string> Redo() { var view = Activate(); if (view != null) await view.RedoAsync(); return Status(); }
	[DevFlowAction("od.gtk-designer.properties.event.set", Description = "Set (or clear, with an empty handler) a GTK signal handler through the selected Properties-pad adapter; returns the handler the pad then shows")]
	public static string SetPropertiesEvent(string eventName, string handlerName)
	{
		var view = Activate(); var selected = PropertyGrid?.SelectedObject;
		var source = selected as IPropertyGridEventSource;
		source?.SetEventHandler(eventName, handlerName ?? "");
		return JsonSerializer.Serialize(new { success = source != null, eventName, handler = source?.GetEventHandler(eventName), selectedId = view?.SelectedId });
	}
	[DevFlowAction("od.gtk-designer.refresh", Description = "Redraw the current GTK design preview without changing source-model history")]
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
	[DevFlowAction("od.gtk-designer.theme", Description = "Set the GTK preview-only Light or Dark theme")]
	public static string Theme(string theme) { var view = Activate(); if (view != null) view.SetPreviewTheme(theme); return JsonSerializer.Serialize(new { success = view != null, theme }); }
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

		if (view.ToolboxRow(typeName, out var error) is not { } container)
			return JsonSerializer.Serialize(new { success = false, error });
		return JsonSerializer.Serialize(GetScreenBounds(container));
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
