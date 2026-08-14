using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WinUIXamlDesigner;

[DevFlowUIThread]
public static class WinUIXamlDesignerDevFlowActions
{
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

	[DevFlowAction("od.winui-designer.runtime-stats", Description = "Report WinUI/Uno designer runtime lifecycle probes: how many designer hosts are alive, and whether the last preview tree (and therefore its collectible preview assembly) is still reachable after a GC")]
	public static string RuntimeStats()
	{
		// The probes live in the ProGPUHost assembly, which this AddIn deliberately does not
		// reference at compile time - same reflection seam RegisterDevFlowActionsCommand uses.
		var bootstrap = Type.GetType(
			"ICSharpCode.WinUIXamlDesigner.ProGPUHost.ProGpuRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.ProGPUHost",
			throwOnError: false);
		if (bootstrap == null)
			return Failure("ProGPU runtime host is not loaded");

		var liveHosts = bootstrap.GetProperty("LiveHostCount",
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
		var rootAlive = bootstrap.GetMethod("LastPreviewRootAlive",
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
		return JsonSerializer.Serialize(new {
			success = true,
			liveHosts = liveHosts as int? ?? -1,
			lastPreviewRootAlive = rootAlive as bool? ?? true
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
}
