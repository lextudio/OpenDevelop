using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WinUIXamlDesigner;

[DevFlowUIThread]
public static class WinUIXamlDesignerDevFlowActions
{
	[DevFlowAction("od.winui-designer.surface-geometry", Description = "Report the WinUI/Uno design surface geometry (rendered design bitmap bounds, selection outline bounds, bottom-right resize handle, selected element bounds) in screen coordinates - the smoke probe for the resize-drag invariant that selection and handle always track the rendered element")]
	public static string GetSurfaceGeometry()
	{
		var view = ActivateDesigner();
		if (view == null)
			return JsonSerializer.Serialize(new { available = false });
		return JsonSerializer.Serialize(DesignerSurfaceGeometryProbe.ToJson(view.SurfaceGeometry()));
	}

	[DevFlowAction("od.winui-designer.status", Description = "Inspect the active WinUI/Uno XAML designer: framework profile, preview state, toolbox counts, outline tree, selection and undo/redo availability")]
	public static string Status()
	{
		var view = ActivateDesigner();
		if (view == null)
			return JsonSerializer.Serialize(new { active = false });

		var toolbox = WinUIXamlToolbox.Instance;
		return JsonSerializer.Serialize(new {
			active = true,
			framework = view.Framework.Kind.ToString(),
			evidence = view.Framework.Evidence,
			rendered = view.HasRenderedPreview,
			status = view.StatusText,
			documentError = view.DocumentError,
			toolboxItemCount = toolbox.ItemCount,
			toolboxGroupCount = toolbox.GroupCount,
			outlineChildCount = view.OutlineChildCount,
			elementNames = view.ElementNames(),
			selectedName = view.SelectedElementName,
			resolvedNameCount = view.ResolvedNameCount,
			lastPick = view.LastPickDiagnostic,
			canUndo = view.CanUndo,
			canRedo = view.CanRedo,
			isDirty = view.PrimaryFile?.IsDirty ?? false
		});
	}

	[DevFlowAction("od.winui-designer.select", Description = "Select a named element in the active WinUI/Uno designer so the shared Properties pad is populated from the XAML source")]
	public static string Select(string name)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (!view.SelectElement(name))
			return Failure("No element named '" + name + "'; known names: " + string.Join(", ", view.ElementNames()));
		// A scripted single select must also reset the runtime's selection set.
		view.MultiSelect(new[] { name });
		return JsonSerializer.Serialize(new {
			success = true,
			selectedName = view.SelectedElementName,
			// Proves the shared Properties pad - not a designer-private grid - is what got populated.
			propertyPadSelectedType = PropertyPadGrid?.SelectedObject?.GetType().FullName
		});
	}

	[DevFlowAction("od.winui-designer.toolbox.insert", Description = "Insert the named Toolbox control into a container element (or the root when container is omitted) in the active WinUI/Uno designer, landing the change as a XAML source edit")]
	public static string ToolboxInsert(string controlName, string containerName = null)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");

		// Go through the shared Toolbox pad's own item list, so this cannot pass while the pad is
		// empty or shows the wrong framework's controls.
		var tool = WinUIXamlToolbox.Instance.FindItem(controlName);
		if (tool == null)
			return Failure("Toolbox has no item named '" + controlName + "'");

		try {
			var insertedName = view.InsertFromToolbox(tool.Name, string.IsNullOrEmpty(containerName) ? null : containerName);
			return JsonSerializer.Serialize(new { success = true, insertedName, selectedName = view.SelectedElementName });
		} catch (Exception exception) {
			return Failure(exception.Message);
		}
	}

	[DevFlowAction("od.winui-designer.delete", Description = "Delete the currently selected element in the active WinUI/Uno designer as a XAML source edit")]
	public static string Delete()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		try {
			view.DeleteSelected();
			return JsonSerializer.Serialize(new { success = true, elementNames = view.ElementNames() });
		} catch (Exception exception) {
			return Failure(exception.Message);
		}
	}

	[DevFlowAction("od.winui-designer.undo", Description = "Undo the last WinUI/Uno designer edit")]
	public static string Undo() => History(view => view.Undo());

	[DevFlowAction("od.winui-designer.redo", Description = "Redo the last undone WinUI/Uno designer edit")]
	public static string Redo() => History(view => view.Redo());

	static string History(Func<WinUIXamlDesignerViewContent, bool> operation)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var moved = operation(view);
		return JsonSerializer.Serialize(new {
			success = moved,
			elementNames = view.ElementNames(),
			resolvedNameCount = view.ResolvedNameCount,
			lastPick = view.LastPickDiagnostic,
			canUndo = view.CanUndo,
			canRedo = view.CanRedo
		});
	}

	[DevFlowAction("od.winui-designer.properties-pad.edit", Description = "Edit a property through the real shared Properties pad PropertyItem; does not touch the XAML document directly")]
	public static string EditPropertyThroughPropertiesPad(string propertyName, string value)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var grid = PropertyPadGrid;
		if (grid == null)
			return Failure("Properties pad is not available");
		if (grid.SelectedObject is not WinUIXamlElementPropertyAdapter)
			return Failure("Properties pad has no selected WinUI/Uno element; it holds " +
				(grid.SelectedObject?.GetType().FullName ?? "null"));

		// Set the PropertyItem the visible grid generated, so this exercises the pad's normal
		// binding -> PropertyDescriptor -> source-edit path rather than the adapter directly.
		var item = grid.Properties?.OfType<PropertyItem>()
			.FirstOrDefault(candidate => candidate.PropertyName == propertyName);
		if (item == null)
			return JsonSerializer.Serialize(new {
				success = false,
				error = "Properties pad property not found: " + propertyName,
				propertyNames = grid.Properties?.OfType<PropertyItem>().Select(p => p.PropertyName).ToArray()
			});

		var before = item.Value?.ToString();
		item.Value = value;
		return JsonSerializer.Serialize(new {
			success = true,
			selectedName = view.SelectedElementName,
			propertyName = item.PropertyName,
			before,
			after = item.Value?.ToString()
		});
	}

	[DevFlowAction("od.winui-designer.toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a WinUI/Uno Toolbox row, for driving a synthetic mouse drag onto the design surface")]
	public static string QueryToolboxItemBounds(string controlName)
	{
		// Activating the designer is what makes the shared ToolsPad resolve this view's
		// IToolsHost.ToolsContent - i.e. actually realize the WinUI toolbox - in the first place.
		if (ActivateDesigner() == null)
			return Failure("No WinUI/Uno designer is active");

		if (WinUIXamlToolbox.Instance.ToolboxControl is not System.Windows.Controls.ListBox list)
			return Failure("Toolbox control is not a ListBox");
		var item = WinUIXamlToolbox.Instance.FindItem(controlName);
		if (item == null)
			return Failure("Toolbox has no item named '" + controlName + "'");

		// The pad is a process-lifetime singleton, so an earlier drag may have left a different
		// row selected; select explicitly so the drag picks up the tool that was asked for.
		list.SelectedItem = item;
		list.ScrollIntoView(item);
		list.UpdateLayout();

		if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
			return Failure("Toolbox row has no realized container: " + controlName);
		container.BringIntoView();
		list.UpdateLayout();

		var origin = container.PointToScreen(new Point(0, 0));
		return JsonSerializer.Serialize(new {
			success = true,
			name = item.Name,
			x = origin.X,
			y = origin.Y,
			width = container.ActualWidth,
			height = container.ActualHeight,
			centerX = origin.X + container.ActualWidth / 2,
			centerY = origin.Y + container.ActualHeight / 2
		});
	}

	[DevFlowAction("od.winui-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a named element as rendered by ProGPU on the design surface, for driving synthetic pointer input at it")]
	public static string QueryElementScreenBounds(string name)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var bounds = view.QueryElementScreenBounds(name);
		if (bounds == null)
			return Failure("No rendered element named '" + name + "'");
		var rect = bounds.Value;
		return JsonSerializer.Serialize(new {
			success = true,
			name,
			x = rect.X,
			y = rect.Y,
			width = rect.Width,
			height = rect.Height,
			centerX = rect.X + rect.Width / 2,
			centerY = rect.Y + rect.Height / 2
		});
	}

	[DevFlowAction("od.winui-designer.describe-element", Description = "Diagnostic-only dump of a named rendered element's style/template/box-model state (HasTemplate, Style/Template/Background set, Padding), for tracking down 'renders but doesn't look right' symptoms")]
	public static string DescribeElement(string name)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, name, description = view.DescribeElementState(name) });
	}

	[DevFlowAction("od.winui-designer.switch-to-source",Description = "Switch the active XAML document back to its primary Source view, so a Source-then-Design round trip can be driven the way a user clicking the tabs would")]
	public static string SwitchToSource()
	{
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window == null)
			return Failure("No active document window");
		for (var index = 0; index < window.ViewContents.Count; index++) {
			if (window.ViewContents[index] is not WinUIXamlDesignerViewContent) {
				window.SwitchView(index);
				return JsonSerializer.Serialize(new {
					success = true,
					activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
				});
			}
		}
		return Failure("This document has no non-designer view to switch to");
	}

	[DevFlowAction("od.winui-designer.runtime-stats", Description = "Report WinUI/Uno designer runtime lifecycle probes: whether the out-of-process design host child is alive (and therefore not yet released after closing the document)")]
	public static string RuntimeStats()
	{
		// The out-of-process Uno host is the active renderer: report the active designer's
		// child-process liveness. (The retired ProGPU probes lived in the ProGPUHost assembly,
		// which is still deployed but never hosts a design anymore.)
		var view = ActivateDesigner();
		var childAlive = view != null && view.IsChildProcessAlive;
		return JsonSerializer.Serialize(new {
			success = true,
			liveHosts = childAlive ? 1 : 0,
			childAlive,
			lastPreviewRootAlive = false
		});
	}

	[DevFlowAction("od.winui-designer.compositor-metrics", Description = "Temporary diagnostic: ProGPU compositor metrics from the last offscreen render")]
	public static string CompositorMetrics()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, metrics = view.CompositorMetricsDump() });
	}

	[DevFlowAction("od.winui-designer.draw-calls", Description = "Temporary diagnostic: dump the compositor's compiled draw calls")]
	public static string DrawCalls()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, drawCalls = view.DumpDrawCalls() });
	}

	[DevFlowAction("od.winui-designer.winui-commands", Description = "Temporary diagnostic: commands emitted by the WinUI root's OnRender")]
	public static string WinUICommands()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, commands = view.WinUICommandProbe() });
	}

	[DevFlowAction("od.winui-designer.diagnose-screen-anchors", Description = "Temporary diagnostic: screen origins of every candidate PointToScreen anchor on the design surface")]
	public static string DiagnoseScreenAnchors()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, anchors = view.DiagnoseScreenAnchors() });
	}

	[DevFlowAction("od.winui-designer.overlay", Description = "Temporary diagnostic: toggle the red OnRender overlay on the design surface")]
	public static string Overlay(bool enabled)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		view.SetShowDiagnosticOverlay(enabled);
		return JsonSerializer.Serialize(new { success = true, overlay = enabled });
	}

	[DevFlowAction("od.winui-designer.recreate-bitmap", Description = "Temporary diagnostic: toggle recreating the WriteableBitmap each frame")]
	public static string RecreateBitmap(bool enabled)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		view.SetRecreateBitmapEachFrame(enabled);
		return JsonSerializer.Serialize(new { success = true, recreate = enabled });
	}

	[DevFlowAction("od.winui-designer.background-brush", Description = "Temporary diagnostic: toggle presenting the frame via Background = ImageBrush")]
	public static string BackgroundBrush(bool enabled)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		view.SetPresentViaBackgroundBrush(enabled);
		return JsonSerializer.Serialize(new { success = true, backgroundBrush = enabled });
	}

	[DevFlowAction("od.winui-designer.image-path", Description = "Temporary diagnostic: replay LibreWPF's image adapter path step by step")]
	public static string ImagePath()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, imagePath = view.ImagePathProbe() });
	}

	static string Failure(string error) => JsonSerializer.Serialize(new { success = false, error });

	[DevFlowAction("od.winui-designer.frame-profile", Description = "Temporary diagnostic: row profile of non-white pixels in the presented ProGPU frame")]
	public static string FrameProfile()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, profile = view.FrameProfile() });
	}

	[DevFlowAction("od.winui-designer.view", Description = "Get or set the design-surface viewport. 'query' returns zoom/pan/scale; 'fit' resets to the fitted centered view; 'WxH' sets the design canvas size; 'zoom panX panY' sets the viewport directly")]
	public static string View(string command = "query")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (command == "query")
		{
			var viewport = view.GetViewport();
			return JsonSerializer.Serialize(new {
				success = true,
				zoom = viewport.Zoom,
				panX = viewport.PanX,
				panY = viewport.PanY
			});
		}
		if (command == "fit")
		{
			view.FitView();
			var viewport = view.GetViewport();
			return JsonSerializer.Serialize(new { success = true, zoom = viewport.Zoom, panX = viewport.PanX, panY = viewport.PanY });
		}
		var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var panX)
			&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var panY))
		{
			view.SetViewport(zoom, panX, panY);
			return JsonSerializer.Serialize(new { success = true, zoom, panX, panY });
		}
		return Failure("Expected 'query', 'fit' or 'zoom panX panY', got: " + command);
	}

	[DevFlowAction("od.winui-designer.design-size", Description = "Get or set the design canvas size. 'query' returns the current size; 'reset' restores the default; a named preset ('phone' 390x844, 'tablet' 768x1024, 'desktop' 1280x720) or 'WxH' (e.g. '1366x768') overrides the canvas size for pages without an explicit size")]
	public static string DesignSize(string command = "query")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (command == "query")
		{
			var configured = view.GetDesignSize();
			return JsonSerializer.Serialize(new { success = true, configured });
		}
		if (command == "reset")
		{
			view.ResetDesignSize();
			return JsonSerializer.Serialize(new { success = true, configured = (double?)null });
		}
		var preset = Presets.FirstOrDefault(p => p.Key == command);
		if (preset.Key != null)
		{
			view.SetDesignSize(preset.Width, preset.Height);
			return JsonSerializer.Serialize(new { success = true, preset = preset.Key, configured = new { width = preset.Width, height = preset.Height } });
		}
		var separator = command.IndexOfAny(new[] { 'x', 'X', '*' });
		if (separator > 0
			&& double.TryParse(command.Substring(0, separator), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
			&& double.TryParse(command.Substring(separator + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
			&& width > 0 && height > 0)
		{
			view.SetDesignSize(width, height);
			return JsonSerializer.Serialize(new { success = true, configured = new { width, height } });
		}
		return Failure("Expected 'query', 'reset', a preset (phone/tablet/desktop) or 'WxH' (e.g. '1366x768'), got: " + command);
	}

	/// <summary>Common form-factor presets for the design canvas.</summary>
	internal static readonly (string Key, double Width, double Height)[] Presets = {
		("phone", 390, 844),
		("tablet", 768, 1024),
		("desktop", 1280, 720)
	};

	/// <summary>
	/// The designer registers as a secondary view alongside the primary AvalonEdit text view, and
	/// "Source" is the default active sub-view, so ActiveViewContent alone never finds it. Merely
	/// locating the inactive secondary view is not enough either: SharpDevelop only calls
	/// LoadInternal once the tab actually becomes active, so the document model and preview do not
	/// exist until SwitchView has run.
	/// </summary>
	static WinUIXamlDesignerViewContent ActivateDesigner()
	{
		var active = SD.Workbench.ActiveViewContent;
		if (active is WinUIXamlDesignerViewContent designer)
			return designer;
		var window = active?.WorkbenchWindow;
		if (window == null)
			return null;
		for (var index = 0; index < window.ViewContents.Count; index++) {
			if (window.ViewContents[index] is WinUIXamlDesignerViewContent candidate) {
				window.SwitchView(index);
				return candidate;
			}
		}
		return null;
	}

	/// <summary>
	/// The live shared Properties pad's grid, reached through <c>IPropertyPadHost</c> rather than a
	/// compile-time reference to the pad itself (which lives in the App project this AddIn does not
	/// reference) - same approach as the WPF designer's actions.
	/// </summary>
	static PropertyGrid PropertyPadGrid =>
		(SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;

	[DevFlowAction("od.winui-designer.pad-view-mode",
		Description = "Switch the Properties pad grid between its Properties and Events views; optionally set the Click handler name (exercises the XAML event-attribute write path) and report the events")]
	public static string PadViewMode(string mode, string handlerName = null)
	{
		var grid = PropertyPadGrid;
		if (grid == null)
			return Failure("Properties pad is not available");
		if (mode.Equals("Events", StringComparison.OrdinalIgnoreCase) || mode.Equals("Properties", StringComparison.OrdinalIgnoreCase))
			grid.ViewMode = mode.Equals("Events", StringComparison.OrdinalIgnoreCase)
				? Xceed.Wpf.Toolkit.PropertyGrid.PropertyGridMode.Events
				: Xceed.Wpf.Toolkit.PropertyGrid.PropertyGridMode.Properties;
		if (!string.IsNullOrEmpty(handlerName))
		{
			var click = grid.Events.Cast<Xceed.Wpf.Toolkit.PropertyGrid.EventItem>()
				.FirstOrDefault(e => e.Name == "Click");
			if (click != null)
				click.HandlerName = handlerName;
		}
		return JsonSerializer.Serialize(new {
			success = true,
			viewMode = grid.ViewMode.ToString(),
			eventCount = grid.Events.Count,
			events = grid.Events.Cast<Xceed.Wpf.Toolkit.PropertyGrid.EventItem>().Select(e => new { e.Name, e.HandlerName, e.HandlerTypeName }).ToArray()
		});
	}

	[DevFlowAction("od.winui-designer.pad-hit-test",
		Description = "TEMP DIAGNOSTIC: hit-test the Properties pad grid at grid-relative (x,y) and report the topmost element chain")]
	public static string PadHitTest(double x, double y)
	{
		var grid = PropertyPadGrid;
		if (grid == null)
			return Failure("Properties pad is not available");
		var result = System.Windows.Media.VisualTreeHelper.HitTest(grid, new System.Windows.Point(x, y));
		if (result == null)
			return JsonSerializer.Serialize(new { hit = false, point = new { x, y } });
		var chain = new System.Collections.Generic.List<string>();
		var v = result.VisualHit;
		while (v != null) {
			chain.Add(v.GetType().Name);
			v = System.Windows.Media.VisualTreeHelper.GetParent(v) as System.Windows.Media.Visual;
		}
		return JsonSerializer.Serialize(new { hit = true, type = result.VisualHit.GetType().FullName, chain });
	}

	[DevFlowAction("od.winui-designer.pad-diagnostics",
		Description = "TEMP DIAGNOSTIC: report the Properties pad grid's property states (IsDefaultValue etc.) and the name-column width")]
	public static string PadDiagnostics()
	{
		var grid = PropertyPadGrid;
		if (grid == null)
			return Failure("Properties pad is not available");
		var props = grid.Properties?.Cast<object>().Take(6)
			.Select(p => {
				var pi = (Xceed.Wpf.Toolkit.PropertyGrid.PropertyItem)p;
				var pi2 = pi as dynamic;
				string isDefault = "";
				try { isDefault = pi2.IsDefaultValue.ToString(); } catch { }
				string val = "";
				try { val = pi2.Value?.ToString() ?? "<null>"; } catch { }
				return new { name = pi2.PropertyName, isDefault, value = val };
			}).ToArray();
		return JsonSerializer.Serialize(new {
			success = true,
			nameColumnWidth = grid.NameColumnWidth,
			properties = props
		});
	}

	[DevFlowAction("od.winui-designer.gridlines",
		Description = "Show or hide the design-space gridlines overlay; pass on/off or omit to query the current state")]
	public static string Gridlines(string command = "query")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (command == "query")
			return JsonSerializer.Serialize(new { success = true, gridlines = view.Gridlines });
		var show = command switch
		{
			"on" or "true" or "1" => true,
			"off" or "false" or "0" => false,
			_ => (bool?)null
		};
		if (show is null)
			return Failure("Expected on/off/true/false or 'query', got: " + command);
		view.SetGridlines(show.Value);
		return JsonSerializer.Serialize(new { success = true, gridlines = show.Value });
	}

	[DevFlowAction("od.winui-designer.multi-select",
		Description = "Set the design-surface selection to the named elements (first is primary), for align/distribute/match-size operations")]
	public static string MultiSelect(string names)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var list = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		view.MultiSelect(list);
		return JsonSerializer.Serialize(new { success = true, selected = list });
	}

	[DevFlowAction("od.winui-designer.align",
		Description = "Align the selected design elements against the primary selection: left/center/right (horizontal) or top/middle/bottom (vertical), landed as source edits")]
	public static string Align(string mode)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.AlignSelection(mode);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.distribute",
		Description = "Distribute the selected design elements evenly across their bounding box: horizontal or vertical, landed as source edits")]
	public static string Distribute(string axis)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.DistributeSelection(axis);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.match-size",
		Description = "Match the selected elements' size to the primary selection: width/height/both, landed as source edits")]
	public static string MatchSize(string mode)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.MatchSizeSelection(mode);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.context",
		Description = "Invoke a design-surface context command as a source edit: copy/paste/delete/bring-to-front/send-to-back/wrap-grid/wrap-stackpanel")]
	public static string Context(string command, string name = "")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		view.InvokeContextCommand(command, name);
		return JsonSerializer.Serialize(new { success = true, command, name });
	}

	[DevFlowAction("od.winui-designer.nudge",
		Description = "Nudge the selected design elements by dx,dy design units as a source edit")]
	public static string Nudge(double dx, double dy)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.NudgeSelection(dx, dy);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.reparent",
		Description = "Move a named element under another container element as a source edit (outline re-parent)")]
	public static string Reparent(string name, string targetContainer)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.ReparentElement(name, targetContainer);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.grid-resize",
		Description = "Resize a Grid row/column as a source edit: name, 'row'|'col', index, divider position in design units")]
	public static string GridResize(string name, string axis, int index, double position)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = axis == "row"
			? view.ResizeGridRow(name, index, position)
			: view.ResizeGridColumn(name, index, position);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.activate-design",
		Description = "Switch the active XAML document to its Design (secondary) view, which re-loads the current source into the designer")]
	public static string ActivateDesign()
	{
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window == null)
			return Failure("No active document window");
		for (var index = 0; index < window.ViewContents.Count; index++)
		{
			if (window.ViewContents[index] is WinUIXamlDesignerViewContent)
			{
				window.SwitchView(index);
				return JsonSerializer.Serialize(new {
					success = true,
					activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
				});
			}
		}
		return Failure("No design view in the active document");
	}

	[DevFlowAction("od.winui-designer.diagnostics",
		Description = "Return the design host's diagnostics: the document parse error (documentError) and the render diagnostics (message, line, column)")]
	public static string Diagnostics()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var documentError = view.DocumentErrorWithLocation is { } doc
			? new { message = doc.Message, line = doc.Line, column = doc.Column }
			: null;
		var list = view.LastDiagnostics.Select(d => new { message = d.Message, line = d.Line, column = d.Column }).ToList();
		return JsonSerializer.Serialize(new { success = true, documentError, renderDiagnostics = list });
	}

	[DevFlowAction("od.winui-designer.goto-error",
		Description = "Jump the source view's caret to the first design error's location: the document parse error when present, else the render diagnostic at the given index")]
	public static string GotoError(int index = 0)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		(string Message, int Line, int Column) target;
		if (view.DocumentErrorWithLocation is { } doc)
			target = (doc.Message, doc.Line, doc.Column);
		else if (index >= 0 && index < view.LastDiagnostics.Count)
			target = view.LastDiagnostics[index];
		else
			return Failure("No design error to jump to (count: " + view.LastDiagnostics.Count + ")");
		var line = target.Line > 0 ? target.Line : 1;
		var column = target.Column > 0 ? target.Column : 1;
		var result = view.GotoSourceLocation(line, column);
		return JsonSerializer.Serialize(new { success = true, result, diagnostic = new { message = target.Message, line = target.Line, column = target.Column } });
	}

	[DevFlowAction("od.winui-designer.render-timing",
		Description = "Performance report of the last design render: render ms, pixel size, raw vs compressed wire bytes")]
	public static string RenderTiming()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var (ms, width, height, dpi, compressed, raw) = view.RenderTiming();
		return JsonSerializer.Serialize(new {
			success = true,
			renderMs = Math.Round(ms, 1),
			width, height, dpi,
			rawBytes = raw,
			compressedBytes = compressed,
			compressionRatio = raw > 0 ? Math.Round((double)compressed / raw * 100, 1) : 0
		});
	}

	[DevFlowAction("od.winui-designer.debug-dpi",
		Description = "Test hook: query the effective display scale, simulate a monitor scale change (set N) or clear it (clear) - the poller re-renders on change, as it would on a real monitor move")]
	public static string DebugDpi(string command = "query")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (command == "query")
			return JsonSerializer.Serialize(new { success = true, dpi = view.EffectiveDisplayDpi });
		if (command == "clear")
		{
			view.SetSimulatedDpi(null);
			return JsonSerializer.Serialize(new { success = true, dpi = view.EffectiveDisplayDpi });
		}
		if (double.TryParse(command, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi >= 1)
		{
			view.SetSimulatedDpi(dpi);
			return JsonSerializer.Serialize(new { success = true, simulated = dpi });
		}
		return Failure("Expected 'query', 'clear' or a scale >= 1, got: " + command);
	}

	[DevFlowAction("od.winui-designer.export-png",
		Description = "Export the current design surface to a PNG file at the given path")]
	public static string ExportPng(string path)
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		var result = view.ExportPng(path);
		return JsonSerializer.Serialize(new { success = true, result });
	}

	[DevFlowAction("od.winui-designer.render-sample",
		Description = "Pixel samples of the last rendered design frame (center/corners as #RRGGBB), to verify a re-render actually changed the drawing")]
	public static string RenderSample()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, sample = view.RenderSample() });
	}

	[DevFlowAction("od.winui-designer.child-log",
		Description = "Return the last lines of the Uno design host child's stdout/stderr (render logs, ready banners, errors)")]
	public static string ChildLog()
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		return JsonSerializer.Serialize(new { success = true, log = view.ChildLog });
	}

	[DevFlowAction("od.winui-designer.theme",
		Description = "Get or set the WinUI/Uno design surface theme; pass theme=Light|Dark to switch (re-renders), or omit it to query the current theme")]
	public static string Theme(string theme = "query")
	{
		var view = ActivateDesigner();
		if (view == null)
			return Failure("No WinUI/Uno designer is active");
		if (string.Equals(theme, "query", StringComparison.OrdinalIgnoreCase))
			return JsonSerializer.Serialize(new { success = true, theme = view.GetDesignTheme() });
		view.SetDesignTheme(theme);
		return JsonSerializer.Serialize(new { success = true, theme });
	}
}
