// DevFlow actions used by tests/OpenDevelop.IntegrationTests to inspect the WPF designer's
// runtime state (designer surface, toolbox, outline) without a native UI automation pipeline.
// Static methods on a [DevFlowUIThread]-annotated class are auto-discovered by
// LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread — see
// src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs for the base set of actions.
//
// Rewritten for the out-of-process cutover (doc/technotes/wpf-designer.md): every action here
// used to reach into a live in-process DesignItem/DesignContext/ChangeGroup transaction stack -
// none of that exists anymore. Selection/properties/outline/mutations all go through
// WpfSurfaceDesignerControl/WpfSurfaceHostClient instead, the same seam real UI code uses.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.WpfDesign.AddIn.OutOfProcess;
using ICSharpCode.WpfDesign.SurfaceHost;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WpfDesign.AddIn.DevFlow
{
	[DevFlowUIThread]
	public static class WpfDesignDevFlowActions
	{
		[DevFlowAction("od.wpf-designer.surface-geometry", Description = "Report the WPF design surface geometry (rendered design bitmap bounds, selected element bounds, its selection outline, bottom-right resize handle) in screen coordinates - the smoke probe for the resize-drag invariant that outline and handle always track the rendered element")]
		public static string GetSurfaceGeometry()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { available = false });
			return JsonSerializer.Serialize(DesignerSurfaceGeometryProbe.ToJson(viewContent.SurfaceGeometry()));
		}

		[DevFlowAction("od.wpf-designer.brush-handles", Description = "Report the gradient handles (Start/End, or Center/Origin/RadiusX/RadiusY) the WPF canvas shows over the selected element, with their screen-space centers, for driving a real pointer drag of one")]
		public static string GetBrushHandles()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { available = false });
			var handles = viewContent.BrushHandleScreenPoints().Select(handle => new { name = handle.Name, x = handle.Screen.X, y = handle.Screen.Y }).ToArray();
			return JsonSerializer.Serialize(new { available = true, handles });
		}

		[DevFlowAction("od.wpf-designer.scroll-status", Description = "Report the WPF design canvas ScrollViewer extent, viewport and offsets without inspecting its theme-specific visual template")]
		public static string GetScrollStatus()
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			return JsonSerializer.Serialize(new {
				available = true,
				surface.ScrollStatus.ExtentWidth,
				surface.ScrollStatus.ExtentHeight,
				surface.ScrollStatus.ViewportWidth,
				surface.ScrollStatus.ViewportHeight,
				surface.ScrollStatus.HorizontalOffset,
				surface.ScrollStatus.VerticalOffset,
				surface.ScrollStatus.ScrollableWidth,
				surface.ScrollStatus.ScrollableHeight
			});
		}

		[DevFlowAction("od.wpf-designer.inline-editor-status", Description = "Inspect WPF's on-canvas Text/Content/Header editor without changing the document")]
		public static string GetInlineEditorStatus()
		{
			return JsonSerializer.Serialize(FindWpfViewContent()?.SurfaceControl?.InlineEditorStatus);
		}

		[DevFlowAction("od.wpf-designer.inline-editor-input", Description = "Replace text in the already pointer-opened WPF inline editor and route Enter or Escape through its real key handler")]
		public static string InputInlineEditor(string text, bool cancel = false)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			return JsonSerializer.Serialize(new { success = surface?.InputInlineEditor(text, cancel) == true });
		}

		[DevFlowAction("od.wpf-designer.context-menu-tray-status", Description = "Inspect the selected WPF ContextMenu design tray and its pointer targets without changing the document")]
		public static string GetContextMenuTrayStatus()
		{
			return JsonSerializer.Serialize(FindWpfViewContent()?.SurfaceControl?.ContextMenuTrayStatus);
		}

		[DevFlowAction("od.wpf-designer.menu-type-here-status", Description = "Inspect the selected top-level WPF Menu's trailing \"Type Here\" hotspot (visibility, owning Menu id, screen center) without changing the document")]
		public static string GetMenuTypeHereHotspotStatus()
		{
			return JsonSerializer.Serialize(FindWpfViewContent()?.SurfaceControl?.MenuTypeHereHotspotStatus);
		}

		[DevFlowAction("od.wpf-designer.status", Description = "Inspect the active WPF designer view: whether the design surface loaded, the toolbox's item/group counts, and the outline pad's element tree")]
		public static string GetDesignerStatus()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { active = false });

			// If the XAML failed to parse, WpfViewContent swallows the exception into a
			// WpfDocumentError placeholder (see WpfViewContent.LoadInternal's catch-all) rather than
			// leaving the surface half-loaded with no clue why - surface that reason here too.
			string loadError = GetLoadErrorIfAny(viewContent);
			if (loadError != null)
				return JsonSerializer.Serialize(new { active = true, designerLoaded = false, loadError });

			var state = viewContent.SurfaceControl?.State;
			bool designerLoaded = state?.Accepted == true;

			var toolboxControl = WpfToolbox.Instance.ToolboxControl as ListBox;
			var toolboxItems = toolboxControl?.Items.OfType<SharedToolboxItem>().ToArray() ?? Array.Empty<SharedToolboxItem>();

			var outline = viewContent.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			var outlineSnapshot = outline?.Snapshot();
			var outlineNodes = outlineSnapshot?.Nodes ?? new List<ICSharpCode.SharpDevelop.Widgets.DocumentOutlineSnapshotNode>();

			return JsonSerializer.Serialize(new {
				active = true,
				backend = viewContent.BackendName,
				hostProcessId = viewContent.HostProcessId,
				hostRecoveryCount = viewContent.HostRecoveryCount,
				hostAlive = viewContent.IsHostAlive,
				designerLoaded,
				// A non-empty outline is not proof the renderer succeeded: the historical native
				// all-black-frame failure built the complete tree before returning a deceptive
				// fallback image. Report the accepted frame metadata so gallery tests can assert
				// that every page really reached the compositor without exporting its pixels.
				hasRenderFrame = state?.Render is { Data.Length: > 0, Width: > 0, Height: > 0 },
				renderWidth = state?.Render?.Width ?? 0,
				renderHeight = state?.Render?.Height ?? 0,
				// state.RootType is the DDP wire contract's full CLR name (e.g.
				// "System.Windows.Window" - see WpfSurfaceHostService.OpenCore and
				// WpfSurfaceHostRpcTests, which assert the full name); this action's own
				// pre-existing contract (predating the OOP cutover, still relied on by
				// WaitForWpfDesignerStatusAsync's expectedRootItemType in the integration suite)
				// is the bare type name, matching what the old in-process
				// DesignContext.RootItem.ComponentType.Name used to report.
				rootItemType = SimpleTypeName(state?.RootType),
				toolboxItemCount = toolboxItems.Length,
				toolboxGroupCount = toolboxItems.Select(i => i.CategoryName).Distinct().Count(),
				toolboxFilterText = SharedToolbox.Instance.FilterText,
				toolboxDrag = SharedToolbox.Instance.DragDiagnostic,
				toolboxSearchHosted = (SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HasToolboxSearch == true,
				outlineRootName = outlineNodes.FirstOrDefault()?.Name ?? outlineNodes.FirstOrDefault()?.Type,
				outlineChildCount = outlineNodes.Count(node => node.Depth == 1),
				// WPF's source-authoritative undo stack lives in WpfViewContent, not the child
				// host. Report that real command state so this general status action agrees with
				// od.wpf-designer.undo/redo and with the visible Edit commands.
				canUndo = viewContent.CanUndo,
				canRedo = viewContent.CanRedo,
				selectedIds = viewContent.SurfaceControl?.SelectedElementIds ?? Array.Empty<string>(),
				// The controller's actual drag settings, not the toolbar's display state.
				snapToGrid = viewContent.SurfaceControl?.SnapToGrid ?? false,
				snapToGuides = viewContent.SurfaceControl?.SnapToGuides ?? false,
				gridCellSize = viewContent.SurfaceControl?.GridCellSizeForDrag ?? 0,
				// Whether the opened project embeds any design-time theme
				// (see WpfSurfaceHostService.ResolveThemes) - the toolbar's theme combo
				// is only shown when this is true.
				supportsThemeSwitch = state?.SupportsThemeSwitch ?? false,
				// Flattened (root + every descendant, depth-first) so tests can assert a named
				// element shows up in the outline tree without knowing its exact nesting depth.
				outlineNames = outlineNodes.Select(node => string.IsNullOrEmpty(node.Name) ? node.Type : node.Name).ToArray()
			});
		}

		[DevFlowAction("od.wpf-designer.terminate-host", Description = "Terminate the shared WPF child from the IDE process to verify normal automatic recovery; this does not add a test RPC to the production protocol")]
		public static string TerminateHost()
		{
			var viewContent = FindWpfViewContent();
			var oldHostProcessId = viewContent?.HostProcessId ?? 0;
			var success = viewContent?.TerminateDesignHost() == true;
			return JsonSerializer.Serialize(new { success, oldHostProcessId });
		}

		[DevFlowAction("od.wpf-designer.toolbox.filter", Description = "Filter the active WPF Toolbox by control or category name")]
		public static string FilterToolbox(string text)
		{
			_ = WpfToolbox.Instance.ToolboxControl;
			SharedToolbox.Instance.Filter(text);
			return DesignerDevFlowResults.ToolboxFilter(true, SharedToolbox.Instance.FilterText, SharedToolbox.Instance.VisibleItemCount);
		}

		[DevFlowAction("od.wpf-designer.toolbox.drag-diagnostics", Description = "Report the last real Toolbox mouse/drag transition for WPF designer drag-drop diagnosis")]
		public static string GetToolboxDragDiagnostics()
			=> JsonSerializer.Serialize(new { diagnostic = SharedToolbox.Instance.DragDiagnostic });

		[DevFlowAction("od.wpf-designer.tab-order", Description = "Toggle the tab-order badge overlay - shows each element's TabIndex, matching the WinForms designer's own tab-order view")]
		public static string SetTabOrder(bool show)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			surface.SetTabOrderMode(show);
			return JsonSerializer.Serialize(new { success = true, showTabOrder = surface.ShowTabOrder });
		}

		[DevFlowAction("od.wpf-designer.select", Description = "Select a named element in the active WPF designer so the real Properties pad is populated")]
		public static string SelectElement(string elementName)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var node = surface.FindNodeByName(elementName);
			if (node == null)
				return JsonSerializer.Serialize(new { success = false, error = "Designer element not found: " + elementName });

			surface.SelectElementId(node.Id);

			var selectedObject = viewContent!.PropertyContainer.SelectedObject as WpfSurfaceElementPropertyAdapter;
			return JsonSerializer.Serialize(new {
				success = true,
				selectedName = node.Name,
				propertiesPadSelectedName = selectedObject?.GetComponentName(),
				propertiesPadSelectedType = selectedObject?.GetClassName()
			});
		}

		[DevFlowAction("od.wpf-designer.outline-select", Description = "Select a named WPF element through the live Document Outline control and report the resulting canvas selection")]
		public static string SelectOutlineElement(string elementName)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var node = surface.FindNodeByName(elementName);
			if (node == null)
				return Failure("Designer element not found: " + elementName);
			var outline = viewContent!.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			if (outline == null)
				return Failure("WPF Document Outline is unavailable");

			// Do not call surface.SelectElementId here: this must exercise the same
			// Outline SelectionCommitted -> WpfViewContent -> adorner path as a user click.
			outline.SelectNodeById(node.Id);
			var selected = surface.SelectedElementId;
			return JsonSerializer.Serialize(new {
				success = StringComparer.Ordinal.Equals(selected, node.Id),
				outlineSelectedId = outline.SelectedNode?.Id,
				surfaceSelectedId = selected,
				selectedName = surface.FindNodeByName(elementName)?.Name
			});
		}

		[DevFlowAction("od.wpf-designer.outline-move", Description = "Move one or comma-separated selected named elements into a named container through the live Document Outline's drop path, after the named child (empty = first)")]
		public static string MoveInOutline(string elementName, string targetName, string afterName)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var outline = viewContent!.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			var names = elementName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var nodes = names.Select(surface.FindNodeByName).ToArray();
			var target = surface.FindNodeByName(targetName);
			var after = string.IsNullOrEmpty(afterName) ? null : surface.FindNodeByName(afterName);
			if (outline == null || nodes.Length == 0 || nodes.Any(node => node == null) || target == null || !string.IsNullOrEmpty(afterName) && after == null)
				return Failure("Element, target or Document Outline not found");
			if (!outline.AllowElementMoves || !outline.RequestMove(nodes.Select(node => node!.Id), target.Id, after?.Id))
				return Failure("The Document Outline did not accept the move");
			// The drop path is synchronous: report what the host decided, not merely that it was asked.
			var result = surface.LastMoveResult;
			return JsonSerializer.Serialize(new {
				success = result?.Accepted == true,
				error = result?.Error,
				status = surface.StatusText,
				selectedId = surface.SelectedElementId,
				outlineSelectedId = outline.SelectedNode?.Id,
				movedId = result?.MovedElementId
			});
		}

		[DevFlowAction("od.wpf-designer.outline-copy", Description = "Copy a named element into a named container through the live Document Outline's Ctrl-drop path, after the named child (empty = first)")]
		public static string CopyInOutline(string elementName, string targetName, string afterName)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var outline = viewContent!.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			var node = surface.FindNodeByName(elementName);
			var target = surface.FindNodeByName(targetName);
			var after = string.IsNullOrEmpty(afterName) ? null : surface.FindNodeByName(afterName);
			if (outline == null || node == null || target == null || !string.IsNullOrEmpty(afterName) && after == null)
				return Failure("Element, target or Document Outline not found");
			if (!outline.AllowElementCopies || !outline.RequestCopy(node.Id, target.Id, after?.Id))
				return Failure("The Document Outline did not accept the copy");
			// The Ctrl-drop path is synchronous: report the host result, not merely that it was asked.
			var result = surface.LastCopyResult;
			return JsonSerializer.Serialize(new {
				success = result?.Accepted == true,
				error = result?.Error,
				status = surface.StatusText,
				selectedId = surface.SelectedElementId,
				outlineSelectedId = outline.SelectedNode?.Id,
				createdIds = result?.CreatedElementIds
			});
		}

		[DevFlowAction("od.wpf-designer.outline-toggle", Description = "Click a named element's design-time Hidden or Locked toggle in the live Document Outline (flag: Hidden|Locked) and report the element's flags afterwards")]
		public static string ToggleOutlineFlag(string elementName, string flag)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var node = surface.FindNodeByName(elementName);
			var outline = viewContent!.OutlineContent as ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl;
			if (node == null || outline == null)
				return Failure("Designer element or Document Outline not found: " + elementName);
			var toggleName = flag == ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl.HiddenFlag
				? ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl.HiddenToggleName
				: ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl.LockedToggleName;
			TreeViewItem? Find(ItemsControl items)
			{
				foreach (var entry in items.Items) {
					if (entry is not TreeViewItem item)
						continue;
					if ((item.Tag as DesignerElementNode)?.Id == node.Id)
						return item;
					if (Find(item) is { } match)
						return match;
				}
				return null;
			}
			var toggle = (Find(outline)?.Header as Panel)?.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()
				.FirstOrDefault(candidate => candidate.Name == toggleName);
			if (toggle == null)
				return Failure("The outline row has no " + flag + " toggle: " + elementName);
			// The toggle's own click path, as a user click: flip, then raise Click.
			toggle.IsChecked = toggle.IsChecked != true;
			toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
			var after = surface.FindNodeByName(elementName);
			return JsonSerializer.Serialize(new {
				success = after != null,
				hidden = after?.IsDesignTimeHidden,
				locked = after?.IsDesignTimeLocked,
				status = surface.StatusText
			});
		}

		/// <summary>
		/// Real screen bounds for a Toolbox row, computed the same way AvalonDock's own
		/// avd.query.bounds DevFlow action does (plain UIElement.PointToScreen - works reliably
		/// here since, unlike AvalonDock's floating windows, the Toolbox and the design surface it
		/// drags onto are always in the same single main window). Lets a test drive a REAL
		/// synthetic mouse press/move/release (od.ui/actions/{press,drag-move,release}) starting
		/// from the actual on-screen toolbox row, exercising DragDrop.DoDragDrop end to end -
		/// PortableDragDropOperation (LibreWPF) now implements that for real, so this no longer
		/// needs to fall back to od.wpf-designer.toolbox.drop's direct calls.
		/// </summary>
		[DevFlowAction("od.wpf-designer.set-theme", Description = "Switch the active WPF designer between Light/Dark theme via the design/theme RPC directly (bypasses the toolbar button click, which synthetic pointer input cannot reliably hit on this toolbar) - only has an effect when od.wpf-designer.status reports supportsThemeSwitch=true")]
		public static string SetTheme(string theme)
		{
			var viewContent = FindWpfViewContent();
			var surface = viewContent?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var result = surface.SetThemeAsync(theme).GetAwaiter().GetResult();
			return JsonSerializer.Serialize(new {
				success = result.Accepted,
				error = result.Error,
				renderData = result.Render?.Data,
				renderSequence = result.Render?.Sequence
			});
		}

		[DevFlowAction("od.wpf-designer.child-log", Description = "TEMPORARY diagnostic: return the out-of-process WPF design host's captured stderr log")]
		public static string GetChildLog()
		{
			var viewContent = FindWpfViewContent();
			var field = viewContent == null ? null : typeof(WpfViewContent).GetField("client",
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			var hostClient = field?.GetValue(viewContent) as IDesignHostClient;
			return JsonSerializer.Serialize(new { childLog = hostClient?.ChildLog ?? "" });
		}

		[DevFlowAction("od.wpf-designer.export-frame", Description = "TEMPORARY diagnostic: write the current WPF design render frame to a PNG file")]
		public static string ExportFrame(string path)
		{
			var state = FindWpfViewContent()?.SurfaceControl?.State;
			var render = state?.Render;
			if (render == null || string.IsNullOrEmpty(render.Data))
				return JsonSerializer.Serialize(new { success = false, error = "no render data" });
			var pixels = ICSharpCode.SharpDevelop.Designer.Remote.DesignerFrameCodec.DecodeBgra32(render);
			var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
				render.Width, render.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
				pixels, render.Width * 4);
			var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
			encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
			using (var stream = System.IO.File.Create(path))
				encoder.Save(stream);
			return JsonSerializer.Serialize(new { success = true, path, width = render.Width, height = render.Height });
		}

		[DevFlowAction("od.wpf-designer.toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a Toolbox row for a given control type, for driving a synthetic mouse drag")]
		public static string QueryToolboxItemBounds(string typeName)
		{
			// ToolsPadViewModel populates its content from SD.GetActiveViewContentService<IToolsHost>()
			// - the ACTIVE view content's service, not just "some WPF designer is open somewhere".
			// FindWpfViewContent() switches to the WPF secondary view/tab if it isn't already active
			// (same reason od.wpf-designer.select/status need it), which is what makes the pad
			// resolve WpfViewContent's IToolsHost.ToolsContent (WpfToolbox) in the first place.
			var viewContent = FindWpfViewContent();
			if (viewContent?.SurfaceControl?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var toolboxControl = WpfToolbox.Instance.ToolboxControl as ListBox;
			var item = toolboxControl?.Items.OfType<SharedToolboxItem>()
				.FirstOrDefault(i => string.Equals(i.DisplayName, typeName, StringComparison.Ordinal));
			if (toolboxControl == null || item == null)
				return JsonSerializer.Serialize(new { success = false, error = "Toolbox item not found: " + typeName });

			// WpfToolbox.Instance is a process-lifetime singleton shared by every open .xaml file's
			// view, so its ListBox.SelectedItem is whatever some EARLIER drag (in this test run or
			// a completely different test) last left selected - a synthetic mouse press at this
			// row's coordinates does not reliably reselect it itself (unlike a real click, which
			// goes through ListBoxItem's own selection handling before WpfToolbox.OnPreviewMouseMove
			// ever reads SelectedItem). Select explicitly so the drag that follows this query always
			// picks up the CreateComponentTool for the type asked for, not a stale prior selection.
			toolboxControl.SelectedItem = item;
			toolboxControl.ScrollIntoView(item);
			toolboxControl.UpdateLayout();

			if (!(FindRealizedContainer(toolboxControl, item) is FrameworkElement container))
				return JsonSerializer.Serialize(new { success = false, error = "Toolbox row has no realized container (not scrolled into view?): " + typeName });

			container.BringIntoView();
			toolboxControl.UpdateLayout();

			return JsonSerializer.Serialize(GetScreenBounds(container));
		}

		/// <summary>
		/// Same as <see cref="QueryToolboxItemBounds"/>, but for dragging a toolbox item onto the
		/// plain XAML source/text editor (AvalonEditViewContent.TextArea_Drop) rather than the
		/// WpfDesign canvas - it deliberately does NOT call FindWpfViewContent(), since that always
		/// switches the active tab to WpfViewContent (the Design view). AvalonEditViewContent's own
		/// IToolsHost.ToolsContent already resolves to this SAME WpfToolbox.Instance singleton for
		/// any .xaml file, so the ToolsPad realizes the identical toolbox regardless of which view
		/// (source or design) is currently active - switching away is unnecessary here and, worse,
		/// switching a WPF secondary view's tab away and back does not reliably reconnect its
		/// Control to a PresentationSource (a real bug, tracked separately - not this action's job
		/// to work around by forcing an unnecessary Design-view detour).
		/// </summary>
		[DevFlowAction("od.wpf-toolbox.query-item-bounds", Description = "Get the real on-screen bounds of a Toolbox row for a given control type WITHOUT switching the active view to the WpfDesign canvas - use this (instead of od.wpf-designer.toolbox.query-item-bounds) when the drag target is the plain XAML source/text editor, not the Design surface")]
		public static string QueryToolboxItemBoundsWithoutActivatingDesigner(string typeName)
		{
			var toolboxControl = WpfToolbox.Instance.ToolboxControl as ListBox;
			var item = toolboxControl?.Items.OfType<SharedToolboxItem>()
				.FirstOrDefault(i => string.Equals(i.DisplayName, typeName, StringComparison.Ordinal));
			if (toolboxControl == null || item == null)
				return JsonSerializer.Serialize(new { success = false, error = "Toolbox item not found: " + typeName });

			toolboxControl.SelectedItem = item;
			toolboxControl.ScrollIntoView(item);
			toolboxControl.UpdateLayout();

			if (!(FindRealizedContainer(toolboxControl, item) is FrameworkElement container))
				return JsonSerializer.Serialize(new { success = false, error = "Toolbox row has no realized container (not scrolled into view?): " + typeName });

			container.BringIntoView();
			toolboxControl.UpdateLayout();

			if (!WaitUntilHitTestableAt(container))
				return JsonSerializer.Serialize(new { success = false, error = "Toolbox row never became hit-testable at its own layout position (compositor did not catch up): " + typeName });

			return JsonSerializer.Serialize(GetScreenBounds(container));
		}

		/// <summary>
		/// Blocks until a hit-test at <paramref name="element"/>'s own layout position actually
		/// resolves back to it, so the bounds this action returns are ones a real click will land on.
		///
		/// Necessary because layout and hit-testing are served by two different, independently
		/// updated structures in this portable stack: ScrollIntoView/BringIntoView + UpdateLayout()
		/// update WPF's visual tree synchronously (so TransformToAncestor/PointToScreen immediately
		/// report the post-scroll position), but VisualTreeHelper.HitTest is routed through
		/// PortablePresentationSource.HitTestOverride into the ProGPU compositor's scene graph
		/// (see PortablePresentationSource.TryPointHitTestOverride), which is only rebuilt on a
		/// render frame - and the native render loop cannot tick while a DevFlow action is running
		/// synchronously on the UI thread. Measured directly: right after scrolling the toolbox to
		/// its last item, a hit-test at that item's reported position returned an item exactly
		/// ScrollViewer.VerticalOffset away (i.e. the pre-scroll scene); after ~1.5s of pumped
		/// render frames the same hit-test returned the correct item. Pumping a nested
		/// DispatcherFrame lets those frames run, and re-checking (rather than sleeping a fixed
		/// amount) keeps this as short as possible and self-verifying.
		/// </summary>
		static bool WaitUntilHitTestableAt(FrameworkElement element, int timeoutMilliseconds = 4000)
		{
			var source = System.Windows.PresentationSource.FromVisual(element);
			if (!(source?.RootVisual is System.Windows.Media.Visual rootVisual))
				return true; // Not composited (no PresentationSource) - nothing to wait for.

			for (int elapsed = 0; ; elapsed += 100) {
				if (HitTestResolvesToElement(rootVisual, element))
					return true;
				if (elapsed >= timeoutMilliseconds)
					return false;
				PumpRenderFrames(100);
			}
		}

		static bool HitTestResolvesToElement(System.Windows.Media.Visual rootVisual, FrameworkElement element)
		{
			var center = element.TransformToAncestor(rootVisual)
				.Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

			var hit = System.Windows.Media.VisualTreeHelper.HitTest(rootVisual, center)?.VisualHit;
			for (DependencyObject node = hit; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node)) {
				if (ReferenceEquals(node, element))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Runs the dispatcher (and with it the ProGPU native render loop, via
		/// Dispatcher.NativeInputPump - see WorkbenchStartup's own comment on it) for roughly
		/// <paramref name="milliseconds"/>, so pending render frames actually execute.
		/// </summary>
		static void PumpRenderFrames(int milliseconds)
		{
			var frame = new System.Windows.Threading.DispatcherFrame();
			var timer = new System.Windows.Threading.DispatcherTimer {
				Interval = TimeSpan.FromMilliseconds(milliseconds)
			};
			timer.Tick += (sender, e) => {
				timer.Stop();
				frame.Continue = false;
			};
			timer.Start();
			System.Windows.Threading.Dispatcher.PushFrame(frame);
		}

		/// <summary>
		/// ItemContainerGenerator.ContainerFromItem(item) is not trustworthy here - confirmed by
		/// direct hit-testing that, for a deeply-scrolled row in this grouped ListBox, it can
		/// report a container whose ACTUAL on-screen position (via PointToScreen) doesn't match
		/// where that item visually renders; a real click at the reported bounds lands on a
		/// different item's row instead. This bypasses the generator's mapping entirely and finds
		/// the realized ListBoxItem by walking the live visual tree and matching DataContext by
		/// reference - which is what a real click's hit-test would actually find.
		/// </summary>
		static ListBoxItem FindRealizedContainer(ItemsControl itemsControl, object item)
		{
			return FindInVisualTree(itemsControl);

			ListBoxItem FindInVisualTree(DependencyObject node)
			{
				int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
				for (int i = 0; i < count; i++) {
					var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
					if (child is ListBoxItem listBoxItem && ReferenceEquals(listBoxItem.DataContext, item))
						return listBoxItem;
					if (FindInVisualTree(child) is ListBoxItem found)
						return found;
				}
				return null;
			}
		}

		/// <summary>
		/// Same idea as <see cref="QueryToolboxItemBounds"/>, but for an already-placed element on
		/// the active design surface - the drop target for a synthetic drag. Unlike the live
		/// in-process version, the element's rendered position now comes straight from the DDP
		/// tree (<see cref="WpfSurfaceDesignerControl.ScreenBoundsOf(string)"/>) rather than a live
		/// FrameworkElement's own PointToScreen.
		/// </summary>
		[DevFlowAction("od.wpf-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a named element in the active WPF designer, for driving a synthetic mouse drag")]
		public static string QueryElementScreenBounds(string elementName)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var node = surface.FindNodeByName(elementName);
			if (node == null || surface.ScreenBoundsOf(node.Id) is not { } bounds)
				return JsonSerializer.Serialize(new { success = false, error = "Designer element not found: " + elementName });

			return JsonSerializer.Serialize(new {
				success = true,
				x = bounds.X,
				y = bounds.Y,
				width = bounds.Width,
				height = bounds.Height,
				centerX = bounds.X + bounds.Width / 2,
				centerY = bounds.Y + bounds.Height / 2
			});
		}

		static object GetScreenBounds(UIElement element)
		{
			var topLeft = element.PointToScreen(new Point(0, 0));
			var bottomRight = element.PointToScreen(new Point(element.RenderSize.Width, element.RenderSize.Height));
			// Raw DevFlow press/drag-move/release endpoints feed their coordinates straight
			// to cliclick on macOS. LibreWPF can expose client-relative PointToScreen values
			// while its portable presentation source is active, so calibrate both corners to
			// the real NSWindow content origin just as WpfAgentService does for element taps.
			// Without this, the reported Toolbox row is shifted by the main-window origin and
			// a real drag never even raises the Toolbox's PreviewMouseDown handler.
			if (OperatingSystem.IsMacOS()
				&& Window.GetWindow(element) is { } window
				&& MacOSWindowOrigin.TryGetContentOrigin() is { } nativeOrigin)
			{
				var reportedOrigin = window.PointToScreen(new Point());
				var delta = new Vector(nativeOrigin.X - reportedOrigin.X, nativeOrigin.Y - reportedOrigin.Y);
				topLeft += delta;
				bottomRight += delta;
			}
			return new {
				success = true,
				x = topLeft.X,
				y = topLeft.Y,
				width = bottomRight.X - topLeft.X,
				height = bottomRight.Y - topLeft.Y,
				centerX = (topLeft.X + bottomRight.X) / 2,
				centerY = (topLeft.Y + bottomRight.Y) / 2
			};
		}

		/// <summary>
		/// Mirrors what actually happens when a user drags a SharedToolboxItem from the Toolbox onto
		/// the design surface, but calls <see cref="WpfSurfaceDesignerControl.AddElementAsync"/>
		/// directly (design/add-element) instead of simulating DragDrop/mouse events, since DevFlow
		/// has no synthetic-drag primitive and the real drag path is mouse-coordinate driven, not
		/// something a test can address deterministically. Builds the same
		/// <see cref="DesignerToolboxItemInfo"/> a real drop's DataObject now carries
		/// (WpfToolbox.BuildToolboxItemInfo), so this exercises the identical child-side type
		/// resolution a real drag would.
		/// </summary>
		[DevFlowAction("od.wpf-designer.toolbox.drop", Description = "Create a control (mirroring a toolbox drag-drop) and insert it into a container element in the active WPF designer, without needing simulated mouse input")]
		public static string DropToolboxItem(string typeName, string containerElementName = null, string elementName = null, double width = 100, double height = 25)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var type = ResolveControlType(typeName);
			if (type == null)
				return JsonSerializer.Serialize(new { success = false, error = "Could not resolve control type: " + typeName });

			string parentId;
			if (!string.IsNullOrEmpty(containerElementName)) {
				var container = surface.FindNodeByName(containerElementName);
				if (container == null)
					return JsonSerializer.Serialize(new { success = false, error = "Container element not found: " + containerElementName });
				parentId = container.Id;
			} else {
				parentId = surface.OutlineRoot?.Id ?? "";
			}

			var item = WpfToolbox.BuildToolboxItemInfo(type);
			DesignerSessionState state;
			try {
				state = surface.AddElementAsync(parentId, item, elementName ?? "", 0, 0).GetAwaiter().GetResult();
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}
			// AddElementAsync deliberately does not render internally (see
			// WpfSurfaceDesignerControl.Show's remarks) - GetResult() above resumed this DevFlow
			// action on the dispatcher thread it was invoked on, so calling Show() directly here
			// is correct and safe.
			surface.Show(state);
			surface.NotifyDocumentChanged(state);
			if (!state.Accepted)
				return JsonSerializer.Serialize(new { success = false, error = state.Error });
			// Matches what a real toolbox drag-drop does (WpfSurfaceDesignerControl.DropAsync) -
			// select the element this action just created.
			if (state.CreatedElementId != null)
				surface.SelectElementId(state.CreatedElementId);

			var createdName = string.IsNullOrEmpty(elementName) ? null : elementName;
			var created = createdName != null ? surface.FindNodeByName(createdName) : null;
			return JsonSerializer.Serialize(new {
				success = true,
				createdTypeName = created?.Type ?? type.Name,
				createdName = created?.Name,
				containerName = surface.FindNodeByName(containerElementName ?? "")?.Name ?? containerElementName
			});
		}

		/// <summary>
		/// Historical no-op, kept for callers that still invoke it defensively after a real
		/// toolbox-drag-drop or a batch of Properties-pad edits: the in-process designer used to
		/// leave an unfinished ChangeGroup open on its undo transaction stack after certain
		/// mutations (see this technote's Phase 0 notes), requiring an explicit flush before a
		/// save would see the change. Each design/* RPC to the out-of-process child now commits
		/// (or rejects) its own mutation synchronously - there is no lingering client-side
		/// transaction to flush anymore, so this always reports committed = 0.
		/// </summary>
		[DevFlowAction("od.wpf-designer.flush-pending-transaction", Description = "No-op under the out-of-process WPF designer (kept for backward-compatible callers) - every design/* mutation now commits synchronously, so there is never a pending transaction to flush")]
		public static string FlushPendingTransaction()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.SurfaceControl?.State?.Accepted != true)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			return JsonSerializer.Serialize(new { success = true, committed = 0 });
		}

		static Type ResolveControlType(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
				return null;

			var type = Type.GetType(typeName);
			if (type != null)
				return type;

			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				type = assembly.GetType(typeName, throwOnError: false);
				if (type != null)
					return type;
			}

			// Bare simple name (e.g. "Button" instead of "System.Windows.Controls.Button") -
			// try the namespace most toolbox controls actually live in before giving up.
			if (!typeName.Contains(".")) {
				type = typeof(Button).Assembly.GetType("System.Windows.Controls." + typeName, throwOnError: false);
				if (type != null)
					return type;
			}

			return null;
		}

		// Descriptor names are binding-path-safe; callers address rows by their protocol name.
		static bool IsPropertyItem(PropertyItem candidate, string propertyName)
			=> candidate.PropertyName == propertyName
				|| (candidate.PropertyDescriptor as WpfSurfacePropertyDescriptor)?.ProtocolName == propertyName;

		[DevFlowAction("od.wpf-designer.properties-pad.edit", Description = "Edit a property through the real Xceed Properties pad PropertyItem; does not access the DDP property list directly")]
		public static string EditPropertyThroughPropertiesPad(string propertyName, string value)
		{
			var grid = PropertyPadGrid;
			if (grid == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad is not available" });
			if (!(grid.SelectedObject is WpfSurfaceElementPropertyAdapter selectedObject))
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });

			// Use the PropertyItem generated and owned by the visible Xceed PropertyGrid. Setting its
			// Value exercises the pad's normal binding -> PropertyDescriptor -> designer adapter path;
			// deliberately do not call WpfSurfaceHostClient.SetPropertyAsync directly here.
			var item = grid.Properties?.OfType<PropertyItem>()
				.FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			if (item == null)
				return JsonSerializer.Serialize(new {
					success = false,
					error = "Properties pad property not found: " + propertyName,
					propertyCount = grid.Properties?.Count ?? 0,
					propertyNames = grid.Properties?.Cast<object>().Select(candidate =>
						(candidate as PropertyItem)?.PropertyName ?? candidate?.GetType().FullName).ToArray()
				});
			if (item.PropertyDescriptor?.IsReadOnly == true)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad property is read-only: " + propertyName });

			var before = item.Value;
			object convertedValue;
			try {
				var converter = item.PropertyDescriptor?.Converter;
				convertedValue = item.PropertyType == typeof(object) || item.PropertyType == typeof(string)
					? value
					: converter != null && converter.CanConvertFrom(typeof(string))
					? converter.ConvertFrom(null, CultureInfo.InvariantCulture, value)
					: TypeDescriptor.GetConverter(item.PropertyType).ConvertFromInvariantString(value);
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}

			item.Value = convertedValue;
			var after = item.Value;

			// The descriptor definition's binding to the selected object has
			// ValidatesOnExceptions=true (DescriptorPropertyDefinition.CreateValueBinding), so if
			// its own SetValue - which calls
			// WpfSurfaceElementPropertyAdapter.SetProperty, i.e. the real design/set-property RPC -
			// throws, WPF's binding engine swallows it into a validation error rather than letting
			// it propagate here. `after` still reflects the target-side DP's own local value
			// regardless, so a caller checking only before/after cannot tell a real commit from a
			// silently-failed one. Surface that explicitly instead of assuming success.
			string bindingError = null;
			var bindingTarget = item.GetBindingExpression(PropertyItem.ValueProperty)?.DataItem as DependencyObject;
			if (bindingTarget != null && System.Windows.Controls.Validation.GetHasError(bindingTarget)) {
				var errors = System.Windows.Controls.Validation.GetErrors(bindingTarget);
				bindingError = errors.Count > 0 ? errors[0].ErrorContent?.ToString() : "Unknown validation error";
			}

			return JsonSerializer.Serialize(new {
				success = bindingError == null,
				error = bindingError,
				selectedName = selectedObject.GetComponentName(),
				propertyName = item.PropertyName,
				propertyItemType = item.GetType().FullName,
				editorType = item.Editor?.GetType().FullName,
				before = before?.ToString(),
				after = after?.ToString()
			});
		}

		[DevFlowAction("od.wpf-designer.properties-pad.edit-items", Description = "Click a ListBox/ComboBox/ListView Items row's real \"…\" button and apply an edit script to the collection dialog's model instead of the modal dialog: ';'-separated add:<afterIndex>:<text>, remove:<i>, up:<i>, down:<i>, rename:<i>:<text>, or 'cancel'")]
		public static string EditItemsThroughPropertiesPad(string propertyName, string script)
		{
			var grid = PropertyPadGrid;
			if (grid?.SelectedObject is not WpfSurfaceElementPropertyAdapter)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });
			var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			var button = (item?.Editor as DockPanel)?.Children.OfType<Button>()
				.FirstOrDefault(candidate => candidate.Name == WpfItemsCollectionEditor.EditButtonName);
			if (button == null || !button.IsEnabled)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad did not render an enabled items editor button: " + propertyName });
			var result = CollectionItemsEditing.RunScriptedEdit(button, script);
			return result.Success
				? JsonSerializer.Serialize(new { success = true, before = result.Before, after = result.After, committed = result.Committed })
				: JsonSerializer.Serialize(new { success = false, error = result.Error });
		}

		[DevFlowAction("od.wpf-designer.properties-pad.edit-brush", Description = "Click a Brush row's real \"…\" button and apply an edit script to the brush editor's model instead of the modal dialog: ';'-separated kind:<Null|Solid|LinearGradient|RadialGradient>, color:<color>, add:<offset>, select:<i>, offset:<i>:<offset>, remove:<i>, reverse, opacity:<0-1>, start/end/center/origin/radius:x,y, mapping:<mode>, spread:<method>, or 'cancel'")]
		public static string EditBrushThroughPropertiesPad(string propertyName, string script)
		{
			var grid = PropertyPadGrid;
			if (grid?.SelectedObject is not WpfSurfaceElementPropertyAdapter)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });
			var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			var button = (item?.Editor as DockPanel)?.Children.OfType<Button>()
				.FirstOrDefault(candidate => candidate.Name == WpfBrushEditor.EditButtonName);
			if (button == null || !button.IsEnabled)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad did not render an enabled brush editor button: " + propertyName });
			var result = BrushEditing.RunScriptedEdit(button, script);
			return result.Success
				? JsonSerializer.Serialize(new { success = true, before = result.Before, after = result.After, committed = result.Committed })
				: JsonSerializer.Serialize(new { success = false, error = result.Error });
		}

		[DevFlowAction("od.wpf-designer.properties-pad.edit-transform", Description = "Click a Transform row's real \"…\" button (RenderTransform, LayoutTransform) and apply an edit script to the transform editor's model instead of the modal dialog: ';'-separated add:<Rotate|Scale|Skew|Translate>, remove:<i>, up:<i>, down:<i>, select:<i>, set:<i>:<Field>:<value>, or 'cancel'")]
		public static string EditTransformThroughPropertiesPad(string propertyName, string script)
		{
			var grid = PropertyPadGrid;
			if (grid?.SelectedObject is not WpfSurfaceElementPropertyAdapter)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });
			var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			var button = (item?.Editor as DockPanel)?.Children.OfType<Button>()
				.FirstOrDefault(candidate => candidate.Name == WpfTransformEditor.EditButtonName);
			if (button == null || !button.IsEnabled)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad did not render an enabled transform editor button: " + propertyName });
			var result = TransformEditing.RunScriptedEdit(button, script);
			return result.Success
				? JsonSerializer.Serialize(new { success = true, before = result.Before, after = result.After, committed = result.Committed })
				: JsonSerializer.Serialize(new { success = false, error = result.Error });
		}

		[DevFlowAction("od.wpf-designer.properties-pad.pick-resource", Description = "List a resource-reference row's real drop-down ('(none)', then each in-scope resource as {StaticResource key} and {DynamicResource key}), and when <entry> is given select it as a user pick does")]
		public static string PickResourceThroughPropertiesPad(string propertyName, string entry = "")
		{
			var grid = PropertyPadGrid;
			if (grid?.SelectedObject is not WpfSurfaceElementPropertyAdapter)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });
			var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			if (item?.Editor is not ComboBox { Name: WpfResourceReferenceEditor.ComboName } combo)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad did not render a resource-reference picker: " + propertyName });
			var items = combo.Items.OfType<string>().ToArray();
			if (!string.IsNullOrEmpty(entry)) {
				if (!items.Contains(entry))
					return JsonSerializer.Serialize(new { success = false, error = "Not offered: " + entry, items });
				combo.SelectedItem = entry;
			}
			return JsonSerializer.Serialize(new { success = true, items, selected = combo.SelectedItem as string });
		}

		[DevFlowAction("od.wpf-designer.properties-pad.pick-image", Description = "List an ImageSource row's real project-image drop-down, and when <path> is given select that item as a user pick does")]
		public static string PickImageThroughPropertiesPad(string propertyName, string path = "")
		{
			var grid = PropertyPadGrid;
			if (grid?.SelectedObject is not WpfSurfaceElementPropertyAdapter)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });
			var item = grid.Properties?.OfType<PropertyItem>().FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			if (item?.Editor is not ComboBox { Name: WpfImageSourceEditor.ComboName } combo)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad did not render an image-source picker: " + propertyName });
			var items = combo.Items.OfType<string>().ToArray();
			if (!string.IsNullOrEmpty(path)) {
				if (!items.Contains(path))
					return JsonSerializer.Serialize(new { success = false, error = "Not offered: " + path, items });
				combo.SelectedItem = path;
			}
			return JsonSerializer.Serialize(new { success = true, items, value = item.Value?.ToString() });
		}

		[DevFlowAction("od.wpf-designer.properties-pad.bind-event", Description = "Double-click a WPF event through the live Properties pad adapter: binds the XAML handler attribute if unhandled, then the IDE creates (or finds) the method in the code-behind and opens it. The isolated host never touches project code")]
		public static string BindEventThroughPropertiesPad(string eventName)
		{
			var grid = PropertyPadGrid;
			if (grid == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad is not available" });
			if (!(grid.SelectedObject is WpfSurfaceElementPropertyAdapter selectedObject))
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });

			var events = TypeDescriptor.GetEvents(selectedObject);
			if (events.Find(eventName, ignoreCase: false) == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad event not found: " + eventName });

			var eventSource = (IPropertyGridEventSource)selectedObject;
			var before = eventSource.GetEventHandler(eventName);
			((IEventBindingHost)selectedObject).BindEvent(eventName);
			var handler = eventSource.GetEventHandler(eventName);

			return JsonSerializer.Serialize(new {
				success = !string.IsNullOrEmpty(handler),
				selectedName = selectedObject.GetComponentName(),
				eventName,
				before,
				handler
			});
		}

		[DevFlowAction("od.wpf-designer.properties-pad.reset", Description = "Reset a serializable WPF property through the live Properties pad descriptor; uses the same ResetValue path as the Properties pad reset command")]
		public static string ResetPropertyThroughPropertiesPad(string propertyName)
		{
			var grid = PropertyPadGrid;
			if (grid == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad is not available" });
			if (!(grid.SelectedObject is WpfSurfaceElementPropertyAdapter selectedObject))
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });

			var item = grid.Properties?.OfType<PropertyItem>()
				.FirstOrDefault(candidate => IsPropertyItem(candidate, propertyName));
			if (item?.PropertyDescriptor == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad property not found: " + propertyName });
			if (!item.PropertyDescriptor.CanResetValue(selectedObject))
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad property cannot be reset: " + propertyName });

			try {
				item.PropertyDescriptor.ResetValue(selectedObject);
				return JsonSerializer.Serialize(new {
					success = true,
					selectedName = selectedObject.GetComponentName(),
					propertyName = item.PropertyName
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}
		}

		/// <summary>
		/// The WPF designer registers as a secondary view content alongside the primary AvalonEdit
		/// text view for .xaml files. Both are mounted in the split layout, but this helper still
		/// makes the designer pane active when an action is explicitly aimed at it: active view is
		/// the document/save authority and determines where Toolbox invocations are delivered.
		/// </summary>
		/// <summary>
		/// The live Properties pad's Xceed grid, reached via <c>IPropertyPadHost</c> (Base
		/// project) rather than a compile-time reference to <c>PropertyPad</c>/
		/// <c>PropertyPadViewModel</c> - doc/technotes/ilspy.md "Docking and layout replacement":
		/// the Properties pad's real implementation was migrated to the App project, which this
		/// AddIn (like most) doesn't reference.
		/// </summary>
		static Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid PropertyPadGrid => (SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;

		static WpfViewContent FindWpfViewContent()
		{
			var active = SD.Workbench.ActiveViewContent;
			if (active == null)
				return null;

			if (active is WpfViewContent activeWpfViewContent)
				return activeWpfViewContent;

			var window = active.WorkbenchWindow;
			if (window != null) {
				for (int i = 0; i < window.ViewContents.Count; i++) {
					if (window.ViewContents[i] is WpfViewContent wpfViewContent) {
						window.SwitchView(i);
						return wpfViewContent;
					}
				}
			}

			// The active document can be a DIFFERENT file the designer itself opened while loading
			// the WPF application definition (App.xaml): that window has no design view of its own,
			// so searching only the active window reported the designer as absent even though its
			// design view was open and loaded. Callers that recovered by reopening the document
			// then drove an open/reload cycle (each reload re-activates App.xaml), so the status
			// never settled - see OpenXamlFile_LoadsDesignerWithToolboxAndOutline's 30s poll
			// timeout in a full-suite run, which passed in isolation. Fall back to the open design
			// view when there is exactly one, which is unambiguous.
			var openDesignViews = SD.Workbench.ViewContentCollection.OfType<WpfViewContent>().ToArray();
			if (openDesignViews.Length != 1)
				return null;

			var owner = openDesignViews[0].WorkbenchWindow;
			if (owner != null) {
				for (int i = 0; i < owner.ViewContents.Count; i++) {
					if (ReferenceEquals(owner.ViewContents[i], openDesignViews[0])) {
						owner.SwitchView(i);
						break;
					}
				}
			}
			return openDesignViews[0];
		}

		static readonly System.Reflection.PropertyInfo UserContentProperty =
			typeof(WpfViewContent).GetProperty("UserContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
			?? typeof(WpfViewContent).BaseType?.GetProperty("UserContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
			?? typeof(WpfViewContent).BaseType?.BaseType?.GetProperty("UserContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

		static string GetLoadErrorIfAny(WpfViewContent viewContent)
		{
			if (UserContentProperty?.GetValue(viewContent) is WpfDocumentError documentError) {
				var textBox = documentError.FindName("additionalInfo") as System.Windows.Controls.TextBox;
				return textBox?.Text ?? "XAML failed to load (no further detail available)";
			}
			return null;
		}

		static string SimpleTypeName(string fullName)
			=> string.IsNullOrEmpty(fullName) ? fullName : fullName.Substring(fullName.LastIndexOf('.') + 1);

		static void CollectOutlineNames(DesignerElementNode node, List<string> names)
		{
			if (node == null)
				return;

			names.Add(string.IsNullOrEmpty(node.Name) ? node.Type : node.Name);
			foreach (var child in node.Children)
				CollectOutlineNames(child, names);
		}

		static string Failure(string error) => DesignerDevFlowResults.Failure(error);

		[DevFlowAction("od.wpf-designer.delete", Description = "Delete the currently selected element in the active WPF designer - mirrors od.winui-designer.delete")]
		public static string Delete()
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			DesignerSessionState state;
			try {
				state = surface.DeleteSelectedAsync().GetAwaiter().GetResult()
					?? throw new InvalidOperationException("No element is selected to delete");
			} catch (Exception ex) {
				return Failure(ex.Message);
			}
			surface.Show(state);
			surface.NotifyDocumentChanged(state);
			if (!state.Accepted)
				return Failure(state.Error);
			var names = new List<string>();
			if (state.Tree != null)
				CollectOutlineNames(state.Tree, names);
			return JsonSerializer.Serialize(new { success = true, outlineNames = names.ToArray() });
		}

		[DevFlowAction("od.wpf-designer.activate-design", Description = "Make the already-visible WPF Design pane the active drop/command target - mirrors od.winui-designer.activate-design")]
		public static string ActivateDesign()
		{
			var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
			if (window == null)
				return Failure("No active document window");
			for (var index = 0; index < window.ViewContents.Count; index++) {
				if (window.ViewContents[index] is WpfViewContent) {
					window.SwitchView(index);
					return JsonSerializer.Serialize(new {
						success = true,
						activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
					});
				}
			}
			return Failure("No design view in the active document");
		}

		[DevFlowAction("od.wpf-designer.switch-to-source", Description = "Make the already-visible primary Source pane the active drop/command target - mirrors od.winui-designer.switch-to-source")]
		public static string SwitchToSource()
		{
			var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
			if (window == null)
				return Failure("No active document window");
			for (var index = 0; index < window.ViewContents.Count; index++) {
				if (window.ViewContents[index] is not WpfViewContent) {
					window.SwitchView(index);
					return JsonSerializer.Serialize(new {
						success = true,
						activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName
					});
				}
			}
			return Failure("This document has no non-designer view to switch to");
		}

		/// <summary>
		/// The actions below share the same canvas operations as user input.  Keep failures
		/// deterministic for an operation a host truly cannot provide instead of exposing a
		/// missing action at the automation boundary.
		/// </summary>
		static string NotSupported(string capability)
			=> JsonSerializer.Serialize(new { success = false, supported = false, error = "Not supported by the out-of-process WPF design host: " + capability });

		[DevFlowAction("od.wpf-designer.undo", Description = "Undo the last WPF designer edit - restores the whole document's XAML text as of just before that edit via session/update, matching the WinForms designer's own whole-document undo")]
		public static string Undo()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.SurfaceControl?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			if (!viewContent.CanUndo)
				return JsonSerializer.Serialize(new { success = false, error = "Nothing to undo" });
			viewContent.Undo();
			return JsonSerializer.Serialize(new { success = true, canUndo = viewContent.CanUndo, canRedo = viewContent.CanRedo });
		}

		[DevFlowAction("od.wpf-designer.redo", Description = "Redo the last undone WPF designer edit - restores the whole document's XAML text as of just after that edit via session/update, matching the WinForms designer's own whole-document undo")]
		public static string Redo()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.SurfaceControl?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			if (!viewContent.CanRedo)
				return JsonSerializer.Serialize(new { success = false, error = "Nothing to redo" });
			viewContent.Redo();
			return JsonSerializer.Serialize(new { success = true, canUndo = viewContent.CanUndo, canRedo = viewContent.CanRedo });
		}

		[DevFlowAction("od.wpf-designer.multi-select", Description = "Set the design-surface selection to the named elements (comma-separated), driving the same Ctrl-click/marquee multi-selection state a real user gesture would")]
		public static string MultiSelect(string names)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");

			var requested = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			surface.SetMultiSelection(requested);
			return DesignerDevFlowResults.Selection(true, surface.SelectedElementIds);
		}

		[DevFlowAction("od.wpf-designer.context-commands", Description = "List the design surface context menu's commands (header path, command, enabled) as opening the menu would evaluate them")]
		public static string GetContextCommands()
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface == null)
				return Failure("WPF designer is not loaded");
			return JsonSerializer.Serialize(new {
				success = true,
				commands = surface.DescribeContextCommands().Select(entry => new { header = entry.Header, command = entry.Command, enabled = entry.Enabled })
			});
		}

		[DevFlowAction("od.wpf-designer.context-menu-point", Description = "Record the context menu as opened at (x, y) relative to the selected element's design bounds, as a right-click there does, for pointer-dependent commands such as Grid Row/Column units")]
		public static string SetContextMenuPoint(double x, double y)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			if (surface.SelectedNode is not { } node)
				return Failure("Nothing is selected");
			surface.ContextMenuDesignPoint = new System.Numerics.Vector2((float)(node.X + x), (float)(node.Y + y));
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.wpf-designer.clipboard", Description = "Run the design surface's Edit command (cut|copy|paste) or its designer-local duplicate operation, and report the selection afterwards")]
		public static string RunClipboardCommand(string command)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var routed = command switch {
				"cut" => System.Windows.Input.ApplicationCommands.Cut,
				"copy" => System.Windows.Input.ApplicationCommands.Copy,
				"paste" => System.Windows.Input.ApplicationCommands.Paste,
				_ => null
			};
			if (command is not ("duplicate" or "paste-here") && routed == null)
				return Failure("Unknown clipboard command: " + command);
			try {
				if (command == "duplicate") {
					var duplicate = surface.DuplicateSelection();
					return JsonSerializer.Serialize(new {
						success = duplicate?.Accepted == true,
						error = duplicate?.Accepted == true ? null : duplicate?.Error ?? "duplicate is not available",
						status = surface.StatusText,
						selectedIds = surface.SelectedElementIds
					});
				}
				if (command == "paste-here") {
					var pasted = surface.PasteAtContextPoint();
					return JsonSerializer.Serialize(new {
						success = pasted?.Accepted == true,
						error = pasted?.Accepted == true ? null : pasted?.Error ?? "Paste Here is not available",
						status = surface.StatusText,
						selectedIds = surface.SelectedElementIds
					});
				}
				if (!routed.CanExecute(null, surface))
					return JsonSerializer.Serialize(new { success = false, error = command + " is not available" });
				var lastPaste = surface.LastPasteResult;
				routed.Execute(null, surface);
				// A copy, cut or paste the host refused is a failure, not a silent no-op.
				var refused = command == "paste"
					? surface.LastPasteResult == lastPaste || surface.LastPasteResult?.Accepted != true
					: !surface.LastCopySucceeded;
				return JsonSerializer.Serialize(new {
					success = !refused,
					error = refused ? (command == "paste" ? surface.LastPasteResult?.Error : surface.StatusText) : null,
					status = surface.StatusText,
					selectedIds = surface.SelectedElementIds
				});
			} catch (Exception exception) {
				return Failure(command + " failed: " + exception.Message);
			}
		}

		[DevFlowAction("od.wpf-designer.context-command", Description = "Run a design surface context menu command through the same path as clicking it; fails when the menu does not offer it or it is disabled")]
		public static string InvokeContextCommand(string command)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			return JsonSerializer.Serialize(new { success = surface.InvokeContextCommand(command), status = surface.StatusText });
		}

		[DevFlowAction("od.wpf-designer.align", Description = "Align every other selected element's edge/center to the primary selection's - mode: left/center/right/top/middle/bottom")]
		public static string Align(string mode)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			surface.AlignSelection(mode);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.wpf-designer.distribute", Description = "Distribute the selected elements with equal center-to-center spacing (requires 3+ selected) - axis: horizontal/vertical")]
		public static string Distribute(string axis)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			surface.DistributeSelection(axis);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.wpf-designer.match-size", Description = "Resize every other selected element to match the primary selection's size, keeping its own position - mode: width/height/both")]
		public static string MatchSize(string mode)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			surface.MatchSizeSelection(mode);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.wpf-designer.nudge", Description = "Nudge the selected elements by a design-space delta, using the same atomic placement path as the arrow-key gesture")]
		public static string Nudge(double dx, double dy)
		{
			var surface = FindWpfViewContent()?.SurfaceControl;
			if (surface?.State?.Accepted != true)
				return Failure("WPF designer is not loaded");
			var state = surface.NudgeSelection(dx, dy);
			return JsonSerializer.Serialize(new {
				success = state?.Accepted == true,
				error = state?.Error ?? (state == null ? "No movable WPF designer selection" : ""),
				selectedIds = surface.SelectedElementIds
			});
		}
	}
}
