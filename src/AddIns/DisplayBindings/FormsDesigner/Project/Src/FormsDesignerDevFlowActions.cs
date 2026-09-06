// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the WinForms designer's
// runtime state (drag a toolbox item from the shared WpfToolbox onto the out-of-process design
// surface) without a native UI automation pipeline. See WpfDesignDevFlowActions.cs for the WPF
// equivalent.

using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Text.Json;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.FormsDesigner.OutOfProcess;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

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

		[DevFlowAction("od.forms-designer.toolbox.filter", Description = "Filter the active WinForms Toolbox by control or category name")]
		public static string FilterToolbox(string text)
		{
			_ = SharedToolboxAccess.ToolboxControl;
			SharedToolbox.Instance.SetActiveScopes("winforms");
			SharedToolbox.Instance.Filter(text);
			return DesignerDevFlowResults.ToolboxFilter(true, SharedToolbox.Instance.FilterText, SharedToolbox.Instance.VisibleItemCount);
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
			// The native WinForms outline realizes its root node lazily.  Selecting only the
			// unrealized node leaves the surface unchanged; drive the same component-selection
			// path first, then synchronize the outline node.
			viewContent.SelectRemoteComponents(name);
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
				backend = viewContent.BackendName,
				usesCodeDomLoader = false,
				loaderType = "ICSharpCode.FormsDesigner.Host.SnapshotRoslynDesignerLoader",
				hostProcessId = viewContent.RemoteDesignerProcessId,
				rootComponentType = state.RootType,
				canUndo = viewContent.EnableUndo,
				canRedo = viewContent.EnableRedo,
				toolboxSearchHosted = (SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HasToolboxSearch == true,
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

		[DevFlowAction("od.forms-designer.describe-context-menu", Description = "Build the designer right-click menu for a component and report its item labels WITHOUT opening it - the only way to assert the menu's content, since a WPF ContextMenu is its own top-level window and appears in neither a screenshot nor the ui/tree")]
		public static string DescribeContextMenu(string componentName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return JsonSerializer.Serialize(new { success = false, error = "The out-of-process WinForms designer is not loaded" });
			try {
				var labels = viewContent.DescribeContextMenu(componentName);
				return JsonSerializer.Serialize(new { success = true, items = labels });
			} catch (Exception exception) {
				return JsonSerializer.Serialize(new { success = false, error = exception.Message });
			}
		}

		[DevFlowAction("od.forms-designer.query-tab-header-screen-bounds", Description = "Get one tab HEADER's on-screen bounds (real TabControl.GetTabRect, translated by the same path as query-control-screen-bounds) so a synthetic click can be aimed at a tab strip precisely - a header is not a component, and a TabControl's own rect is a useless target because its pages cover nearly all of it")]
		public static string QueryTabHeaderScreenBounds(string tabControlName, int tabIndex)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return JsonSerializer.Serialize(new { success = false, error = "The out-of-process WinForms designer is not loaded" });

			if (!viewContent.TryGetRemoteTabHeaderScreenBounds(tabControlName, tabIndex, out var bounds))
				return JsonSerializer.Serialize(new {
					success = false,
					error = "No tab header " + tabIndex + " on " + tabControlName + " (not a TabControl, or the index is out of range)"
				});
			return JsonSerializer.Serialize(new {
				success = true,
				x = bounds.X,
				y = bounds.Y,
				width = bounds.Width,
				height = bounds.Height,
				// The point a synthetic click should actually use, so callers never re-derive it.
				centerX = bounds.X + bounds.Width / 2,
				centerY = bounds.Y + bounds.Height / 2
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

		static string Failure(string error) => JsonSerializer.Serialize(new { success = false, error });

		static FormsDesignerViewContent LoadedViewOr()
		{
			return FindFormsDesignerViewContent();
		}

		[DevFlowAction("od.forms-designer.select", Description = "Select a named component in the active out-of-process WinForms designer (routes through the Document Outline selection path, mirroring od.winui-designer.select)")]
		public static string Select(string componentName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent == null)
				return Failure("no designer view");
			var outline = viewContent.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			if (outline == null)
				return Failure("no outline control");
			viewContent.SelectRemoteComponents(componentName);
			outline.SelectNodeById(componentName);
			return JsonSerializer.Serialize(new {
				success = true,
				selectedName = viewContent.RemoteDesignerSelectedComponent
			});
		}

		[DevFlowAction("od.forms-designer.multi-select", Description = "Set the design-surface selection to the named components (first is primary), for align/distribute/match-size operations - mirrors od.winui-designer.multi-select")]
		public static string MultiSelect(string names)
		{
			var viewContent = LoadedViewOr();
			if (viewContent == null)
				return Failure("The out-of-process WinForms designer is not loaded");
			var list = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			viewContent.SelectRemoteComponents(list);
			return DesignerDevFlowResults.Selection(true, viewContent.RemoteSelectedComponentNames);
		}

		[DevFlowAction("od.forms-designer.undo", Description = "Undo the last WinForms designer edit - mirrors od.winui-designer.undo")]
		public static string Undo()
		{
			return History(view => view.Undo());
		}

		[DevFlowAction("od.forms-designer.redo", Description = "Redo the last undone WinForms designer edit - mirrors od.winui-designer.redo")]
		public static string Redo()
		{
			return History(view => view.Redo());
		}

		static string History(Action<FormsDesignerViewContent> operation)
		{
			var viewContent = LoadedViewOr();
			if (viewContent == null)
				return Failure("The out-of-process WinForms designer is not loaded");
			operation(viewContent);
			var after = viewContent.RemoteDesignerState;
			return JsonSerializer.Serialize(new {
				success = true,
				canUndo = viewContent.EnableUndo,
				canRedo = viewContent.EnableRedo,
				controlNames = after?.Components.Select(component => component.Name).ToArray()
			});
		}

		[DevFlowAction("od.forms-designer.delete", Description = "Delete the currently selected components in the active out-of-process WinForms designer - mirrors od.winui-designer.delete")]
		public static string Delete()
		{
			var viewContent = LoadedViewOr();
			if (viewContent == null)
				return Failure("The out-of-process WinForms designer is not loaded");
			try {
				viewContent.Delete();
				return JsonSerializer.Serialize(new { success = true, controlNames = viewContent.RemoteDesignerState?.Components.Select(component => component.Name).ToArray() });
			} catch (Exception exception) {
				return Failure(exception.Message);
			}
		}

		static CommandID AlignCommand(string mode) => mode switch {
			"left" => StandardCommands.AlignLeft,
			"center" or "horizontal-centers" => StandardCommands.AlignHorizontalCenters,
			"right" => StandardCommands.AlignRight,
			"top" => StandardCommands.AlignTop,
			"middle" or "vertical-centers" => StandardCommands.AlignVerticalCenters,
			"bottom" => StandardCommands.AlignBottom,
			_ => null
		};

		[DevFlowAction("od.forms-designer.align", Description = "Align the selected components against the primary selection: left/center/right (horizontal) or top/middle/bottom (vertical), routed through the real layout commands - mirrors od.winui-designer.align")]
		public static string Align(string mode)
		{
			var command = AlignCommand(mode);
			if (command == null)
				return Failure("Expected left/center/right/top/middle/bottom, got: " + mode);
			return RunLayout(command, mode);
		}

		[DevFlowAction("od.forms-designer.distribute", Description = "Distribute the selected components evenly across their bounding box: horizontal or vertical - mirrors od.winui-designer.distribute")]
		public static string Distribute(string axis)
		{
			var command = axis == "horizontal" ? StandardCommands.HorizSpaceMakeEqual
				: axis == "vertical" ? StandardCommands.VertSpaceMakeEqual
				: null;
			if (command == null)
				return Failure("Expected horizontal or vertical, got: " + axis);
			return RunLayout(command, axis);
		}

		[DevFlowAction("od.forms-designer.match-size", Description = "Match the selected components' size to the primary selection: width/height/both - mirrors od.winui-designer.match-size")]
		public static string MatchSize(string mode)
		{
			var command = mode switch {
				"width" => StandardCommands.SizeToControlWidth,
				"height" => StandardCommands.SizeToControlHeight,
				"both" => StandardCommands.SizeToControl,
				_ => null
			};
			if (command == null)
				return Failure("Expected width/height/both, got: " + mode);
			return RunLayout(command, mode);
		}

		static string RunLayout(CommandID command, string mode)
		{
			var viewContent = LoadedViewOr();
			if (viewContent == null)
				return Failure("The out-of-process WinForms designer is not loaded");
			var before = viewContent.RemoteSelectedComponentNames;
			var applied = viewContent.TryExecuteRemoteLayout(command);
			return JsonSerializer.Serialize(new {
				success = applied,
				mode,
				selectedBefore = before,
				selectedAfter = viewContent.RemoteSelectedComponentNames,
				controlNames = viewContent.RemoteDesignerState?.Components.Select(component => component.Name).ToArray()
			});
		}

		[DevFlowAction("od.forms-designer.nudge", Description = "Nudge the selected components by dx,dy design units - mirrors od.winui-designer.nudge")]
		public static string Nudge(double dx, double dy)
		{
			var viewContent = LoadedViewOr();
			if (viewContent == null)
				return Failure("The out-of-process WinForms designer is not loaded");
			var moved = viewContent.TryNudgeRemoteSelection((int)dx, (int)dy);
			return JsonSerializer.Serialize(new {
				success = moved,
				dx = (int)dx,
				dy = (int)dy,
				selectedAfter = viewContent.RemoteSelectedComponentNames
			});
		}

		[DevFlowAction("od.forms-designer.toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a Toolbox row in the shared toolbox (the merged Base SharedToolbox, whose \"winforms\" scope the WpfToolbox facade seeds), for driving a synthetic mouse drag - mirrors od.winui-designer.toolbox.query-item-bounds")]
		public static string QueryToolboxItemBounds(string typeName)
		{
			if (FindFormsDesignerViewContent()?.IsRemoteDesignerLoaded != true)
				return Failure("The out-of-process WinForms designer is not loaded");
			// The merged engine is Base's SharedToolbox (referenced directly); only its
			// "winforms" scope needs seeding, which SharedToolboxAccess does (touching
			// WpfDesign.AddIn's WpfToolbox facade via reflection, since a pure WinForms session
			// never loads that assembly - no compile-time reference to it either way).
			var host = SharedToolboxAccess.Host;
			if (!(host?.ToolboxControl is System.Windows.Controls.ListBox list))
				return Failure("Shared toolbox is not available");
			var item = SharedToolbox.Instance.FindItem("winforms", typeName);
			if (item == null)
				return Failure("Toolbox item not found: " + typeName);

			list.SelectedItem = item;
			list.ScrollIntoView(item);
			list.UpdateLayout();

			if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
				return Failure("Toolbox row has no realized container: " + typeName);
			container.BringIntoView();
			list.UpdateLayout();

			var origin = container.PointToScreen(new Point(0, 0));
			return JsonSerializer.Serialize(new {
				success = true,
				name = typeName,
				x = origin.X,
				y = origin.Y,
				width = container.ActualWidth,
				height = container.ActualHeight,
				centerX = origin.X + container.ActualWidth / 2,
				centerY = origin.Y + container.ActualHeight / 2
			});
		}

		static PropertyGrid PropertyPadGrid =>
			(SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;

		[DevFlowAction("od.forms-designer.properties-pad.edit", Description = "Edit a property through the real shared Properties pad PropertyItem; does not access the remote designer's property list directly - mirrors od.winui-designer.properties-pad.edit")]
		public static string EditPropertyThroughPropertiesPad(string propertyName, string value)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return Failure("The out-of-process WinForms designer is not loaded");
			var grid = PropertyPadGrid;
			if (grid == null)
				return Failure("Properties pad is not available");
			if (grid.SelectedObject == null)
				return JsonSerializer.Serialize(new {
					success = false,
					error = "Properties pad has no selected WinForms design item",
					selectedType = grid.SelectedObject?.GetType().FullName
				});

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
				selectedName = viewContent.RemoteDesignerSelectedComponent,
				propertyName = item.PropertyName,
				before,
				after = item.Value?.ToString()
			});
		}

		[DevFlowAction("od.forms-designer.pad-view-mode", Description = "Switch the shared Properties pad grid between its Properties and Events views; optionally set a Click handler name and report the events - mirrors od.winui-designer.pad-view-mode")]
		public static string PadViewMode(string mode, string handlerName = null)
		{
			var grid = PropertyPadGrid;
			if (grid == null)
				return Failure("Properties pad is not available");
			if (mode.Equals("Events", StringComparison.OrdinalIgnoreCase) || mode.Equals("Properties", StringComparison.OrdinalIgnoreCase))
				grid.ViewMode = mode.Equals("Events", StringComparison.OrdinalIgnoreCase)
					? Xceed.Wpf.Toolkit.PropertyGrid.PropertyGridMode.Events
					: Xceed.Wpf.Toolkit.PropertyGrid.PropertyGridMode.Properties;
			if (!string.IsNullOrEmpty(handlerName)) {
				var click = grid.Events.Cast<EventItem>()
					.FirstOrDefault(e => e.Name == "Click");
				if (click != null)
					click.HandlerName = handlerName;
			}
			return JsonSerializer.Serialize(new {
				success = true,
				viewMode = grid.ViewMode.ToString(),
				eventCount = grid.Events.Count,
				events = grid.Events.Cast<EventItem>().Select(e => new { e.Name, e.HandlerName, e.HandlerTypeName }).ToArray()
			});
		}

		[DevFlowAction("od.forms-designer.activate-design", Description = "Switch the active document to its WinForms Design (secondary) view, which re-loads the current source into the designer - mirrors od.winui-designer.activate-design")]
		public static string ActivateDesign()
		{
			var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
			if (window == null)
				return Failure("No active document window");
			for (var index = 0; index < window.ViewContents.Count; index++) {
				if (window.ViewContents[index] is FormsDesignerViewContent) {
					window.SwitchView(index);
					return JsonSerializer.Serialize(new {
						success = true,
						activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
					});
				}
			}
			return Failure("No design view in the active document");
		}

		[DevFlowAction("od.forms-designer.switch-to-source", Description = "Switch the active document back to its primary Source view, so a Source-then-Design round trip can be driven - mirrors od.winui-designer.switch-to-source")]
		public static string SwitchToSource()
		{
			var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
			if (window == null)
				return Failure("No active document window");
			for (var index = 0; index < window.ViewContents.Count; index++) {
				if (window.ViewContents[index] is not FormsDesignerViewContent) {
					window.SwitchView(index);
					return JsonSerializer.Serialize(new {
						success = true,
						activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
					});
				}
			}
			return Failure("This document has no non-designer view to switch to");
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

		// The four actions below give DevFlow direct RPC access to the smart-tag/verb pair,
		// bypassing the chevron glyph and Ctrl+. keyboard shortcut entirely - both require driving
		// real OS mouse/keyboard input against a tiny/keyboard-focus-dependent target that proved
		// unreliable to hit blindly via synthetic screen coordinates (see the 2026-09-05 TabControl
		// technote entries). Useful for exercising Add Tab/Remove Tab (or any other smart-tag/verb
		// feature, e.g. ToolStrip's "Insert Standard Items") without any of that.

		[DevFlowAction("od.forms-designer.list-smart-tag-actions", Description = "List the smart-tag (DesignerActionList) items for a named component in the active out-of-process WinForms designer - Microsoft backend only")]
		public static string ListSmartTagActions(string componentName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return Failure("The out-of-process WinForms designer is not loaded");
			try {
				var actions = viewContent.ListRemoteSmartTagActions(componentName);
				return JsonSerializer.Serialize(new {
					success = actions.Accepted, error = actions.Error,
					items = actions.Items.Select(item => new {
						item.ListIndex, item.ItemIndex, item.Kind, item.DisplayName, item.Description,
						item.Category, item.MemberName
					}).ToArray()
				});
			} catch (Exception exception) {
				return Failure(exception.Message);
			}
		}

		[DevFlowAction("od.forms-designer.invoke-smart-tag-method", Description = "Invoke a smart-tag method item (by listIndex/itemIndex from od.forms-designer.list-smart-tag-actions) for a named component - Microsoft backend only")]
		public static string InvokeSmartTagMethod(string componentName, int listIndex, int itemIndex)
		{
			return InvokeRemote(view => view.InvokeRemoteSmartTagMethod(componentName, listIndex, itemIndex));
		}

		[DevFlowAction("od.forms-designer.list-verbs", Description = "List the designer verbs (right-click context-menu items, e.g. TabControlDesigner's Add Tab/Remove Tab) for a named component in the active out-of-process WinForms designer - Microsoft backend only")]
		public static string ListVerbs(string componentName)
		{
			var viewContent = FindFormsDesignerViewContent();
			if (viewContent?.IsRemoteDesignerLoaded != true)
				return Failure("The out-of-process WinForms designer is not loaded");
			try {
				var verbs = viewContent.ListRemoteVerbs(componentName);
				return JsonSerializer.Serialize(new {
					success = verbs.Accepted, error = verbs.Error,
					items = verbs.Items.Select(item => new { item.Index, item.Text, item.Description, item.Enabled, item.Visible }).ToArray()
				});
			} catch (Exception exception) {
				return Failure(exception.Message);
			}
		}

		[DevFlowAction("od.forms-designer.invoke-verb", Description = "Invoke a designer verb (by index from od.forms-designer.list-verbs) for a named component - Microsoft backend only")]
		public static string InvokeVerb(string componentName, int verbIndex)
		{
			return InvokeRemote(view => view.InvokeRemoteVerb(componentName, verbIndex));
		}
	}
}
