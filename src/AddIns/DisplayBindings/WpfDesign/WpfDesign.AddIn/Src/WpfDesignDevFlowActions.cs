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
using ICSharpCode.WpfDesign.AddIn.OutOfProcess;
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
			var toolboxItems = toolboxControl?.Items.OfType<WpfSideTabItem>().ToArray() ?? Array.Empty<WpfSideTabItem>();

			var outlineNames = new List<string>();
			if (state?.Tree != null)
				CollectOutlineNames(state.Tree, outlineNames);

			return JsonSerializer.Serialize(new {
				active = true,
				designerLoaded,
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
				outlineRootName = state?.Tree?.Name ?? state?.Tree?.Type,
				outlineChildCount = state?.Tree?.Children.Count ?? 0,
				// Flattened (root + every descendant, depth-first) so tests can assert a named
				// element shows up in the outline tree without knowing its exact nesting depth.
				outlineNames = outlineNames.ToArray()
			});
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
			var item = toolboxControl?.Items.OfType<WpfSideTabItem>()
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
			var item = toolboxControl?.Items.OfType<WpfSideTabItem>()
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
		/// Mirrors what actually happens when a user drags a WpfSideTabItem from the Toolbox onto
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
				.FirstOrDefault(candidate => candidate.PropertyName == propertyName);
			if (item == null)
				return JsonSerializer.Serialize(new {
					success = false,
					error = "Properties pad property not found: " + propertyName,
					propertyCount = grid.Properties?.Count ?? 0,
					propertyNames = grid.Properties?.Cast<object>().Select(candidate =>
						(candidate as PropertyItem)?.PropertyName ?? candidate?.GetType().FullName).ToArray()
				});

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

			// The Value binding has ValidatesOnExceptions=true (DescriptorPropertyDefinition.
			// CreateValueBinding), so if the descriptor's own SetValue - which calls
			// WpfSurfaceElementPropertyAdapter.SetProperty, i.e. the real design/set-property RPC -
			// throws, WPF's binding engine swallows it into a validation error rather than letting
			// it propagate here. `after` still reflects the target-side DP's own local value
			// regardless, so a caller checking only before/after cannot tell a real commit from a
			// silently-failed one. Surface that explicitly instead of assuming success.
			var bindingExpression = item.GetBindingExpression(PropertyItem.ValueProperty);
			string bindingError = null;
			if (bindingExpression?.DataItem is System.Windows.DependencyObject dataItem && System.Windows.Controls.Validation.GetHasError(dataItem)) {
				var errors = System.Windows.Controls.Validation.GetErrors(dataItem);
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

		/// <summary>
		/// The WPF designer registers as a secondary view content alongside the primary AvalonEdit
		/// text view for .xaml files, and the "Source" tab is the default active sub-view - so
		/// ActiveViewContent alone won't find it, and merely finding the (inactive) secondary view
		/// content isn't enough either: SharpDevelop only calls LoadInternal (which spawns the
		/// out-of-process child) on a secondary view when its tab actually becomes active, so
		/// WpfViewContent.SurfaceControl is null until we switch to it via IWorkbenchWindow.SwitchView.
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
			if (window == null)
				return null;

			for (int i = 0; i < window.ViewContents.Count; i++) {
				if (window.ViewContents[i] is WpfViewContent wpfViewContent) {
					window.SwitchView(i);
					return wpfViewContent;
				}
			}

			return null;
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
	}
}
