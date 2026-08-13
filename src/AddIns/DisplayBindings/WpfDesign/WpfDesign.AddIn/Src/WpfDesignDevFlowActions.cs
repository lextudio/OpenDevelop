// DevFlow actions used by tests/OpenDevelop.IntegrationTests to inspect the WPF designer's
// runtime state (designer surface, toolbox, outline) without a native UI automation pipeline.
// Static methods on a [DevFlowUIThread]-annotated class are auto-discovered by
// LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread — see
// src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs for the base set of actions.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.WpfDesign;
using ICSharpCode.WpfDesign.Designer.OutlineView;
using ICSharpCode.WpfDesign.Designer.Services;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WpfDesign.AddIn.DevFlow
{
	[DevFlowUIThread]
	public static class WpfDesignDevFlowActions
	{
		[DevFlowAction("od.wpf-designer.status", Description = "Inspect the active WPF designer view: whether the design surface loaded, the toolbox's item/group counts, and the outline pad's element tree")]
		public static string GetDesignerStatus()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent == null)
				return JsonSerializer.Serialize(new { active = false });

			// If the XAML failed to parse, WpfViewContent swallows the exception into a
			// WpfDocumentError placeholder (see WpfViewContent.LoadInternal's catch-all) rather than
			// leaving DesignContext/RootItem null with no clue why - surface that reason here too.
			string loadError = GetLoadErrorIfAny(viewContent);
			if (loadError != null)
				return JsonSerializer.Serialize(new { active = true, designerLoaded = false, loadError });

			bool designerLoaded = viewContent.DesignContext != null && viewContent.DesignContext.RootItem != null;

			var toolboxControl = WpfToolbox.Instance.ToolboxControl as ListBox;
			var toolboxItems = toolboxControl?.Items.OfType<WpfSideTabItem>().ToArray() ?? System.Array.Empty<WpfSideTabItem>();

			IOutlineNode outlineRoot = viewContent.Outline?.Root;
			var outlineNames = new List<string>();
			CollectOutlineNames(outlineRoot, outlineNames);

			return JsonSerializer.Serialize(new {
				active = true,
				designerLoaded,
				rootItemType = viewContent.DesignContext?.RootItem?.ComponentType?.Name,
				toolboxItemCount = toolboxItems.Length,
				toolboxGroupCount = toolboxItems.Select(i => i.CategoryName).Distinct().Count(),
				outlineRootName = outlineRoot?.Name,
				outlineChildCount = outlineRoot?.Children.Count ?? 0,
				// Flattened (root + every descendant, depth-first) so tests can assert a named
				// element shows up in the outline tree without knowing its exact nesting depth.
				outlineNames = outlineNames.ToArray()
			});
		}

		[DevFlowAction("od.wpf-designer.select", Description = "Select a named element in the active WPF designer so the real Properties pad is populated")]
		public static string SelectElement(string elementName)
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.DesignContext?.RootItem == null)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });
			var designItem = FindDesignItem(viewContent.DesignContext.RootItem, elementName);
			if (designItem == null)
				return JsonSerializer.Serialize(new { success = false, error = "Designer element not found: " + elementName });

			viewContent.DesignContext.Services.Selection.SetSelectedComponents(
				new[] { designItem }, SelectionTypes.Replace);

			var selectedObject = PropertyPadGrid?.SelectedObject as DesignItemPropertyGridAdapter;
			return JsonSerializer.Serialize(new {
				success = true,
				selectedName = viewContent.DesignContext.Services.Selection.PrimarySelection?.Name,
				propertiesPadSelectedName = selectedObject?.DesignItem?.Name,
				propertiesPadSelectedType = selectedObject?.DesignItem?.ComponentType?.Name
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
		/// needs to fall back to od.wpf-designer.toolbox.drop's direct CreateComponentTool calls.
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
			if (viewContent?.DesignContext?.RootItem == null)
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
		/// the active design surface - the drop target for a synthetic drag.
		/// </summary>
		[DevFlowAction("od.wpf-designer.query-element-screen-bounds", Description = "Get the real on-screen bounds of a named element in the active WPF designer, for driving a synthetic mouse drag")]
		public static string QueryElementScreenBounds(string elementName)
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.DesignContext?.RootItem == null)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var designItem = FindDesignItem(viewContent.DesignContext.RootItem, elementName);
			if (!(designItem?.View is FrameworkElement view))
				return JsonSerializer.Serialize(new { success = false, error = "Designer element not found: " + elementName });

			return JsonSerializer.Serialize(GetScreenBounds(view));
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
		/// the design surface (WpfToolbox.cs's OnPreviewMouseMove -> DragDrop.DoDragDrop ->
		/// CreateComponentTool.designPanel_Drop), but calls the vendored designer engine's
		/// mouse-independent primitives directly (CreateComponentTool.CreateItem +
		/// AddItemsWithDefaultSize - see externals/vscode-wpf/.../CreateComponentTool.cs) instead of
		/// simulating DragDrop/mouse events, since DevFlow has no synthetic-drag primitive and the
		/// real drag path is mouse-coordinate driven, not something a test can address deterministically.
		/// </summary>
		[DevFlowAction("od.wpf-designer.toolbox.drop", Description = "Create a control (mirroring a toolbox drag-drop) and insert it into a container element in the active WPF designer, without needing simulated mouse input")]
		public static string DropToolboxItem(string typeName, string containerElementName = null, string elementName = null, double width = 100, double height = 25)
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.DesignContext?.RootItem == null)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			var type = ResolveControlType(typeName);
			if (type == null)
				return JsonSerializer.Serialize(new { success = false, error = "Could not resolve control type: " + typeName });

			DesignItem container = viewContent.DesignContext.RootItem;
			if (!string.IsNullOrEmpty(containerElementName)) {
				container = FindDesignItem(container, containerElementName);
				if (container == null)
					return JsonSerializer.Serialize(new { success = false, error = "Container element not found: " + containerElementName });
			}

			// AddItemWithCustomSizePosition's own CreateItem() call (instance method, on the
			// throwaway CreateComponentTool it constructs internally) opens a ChangeGroup that
			// nothing ever commits or aborts afterwards (only the real mouse-driven drag path
			// - designPanel_Drop - finishes the ChangeGroup CreateItemWithPosition opens; this
			// static single-call helper has no equivalent finish step). Left open, the change
			// never becomes durable: it's visible in-memory (selectable/editable) but Save
			// serializes the pre-drop document, and the leaked transaction blocks any later
			// OpenGroup from committing correctly ("Invalid transaction finish, nested
			// transactions must finish first" - the leaked one is always on top of the stack).
			// Finish it ourselves via UndoService's transaction stack (UndoTransaction itself is
			// internal to the designer engine assembly, but its base ChangeGroup - which is all
			// we need to call Commit() - is public).
			bool added;
			try {
				added = CreateComponentTool.AddItemWithCustomSizePosition(
					container, type, new Size(width, height), new Point(0, 0));
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}

			if (!added)
				return JsonSerializer.Serialize(new { success = false, error = "Container does not accept a dropped " + type.Name + " (no placement behavior)" });

			CommitLeakedChangeGroup(viewContent.DesignContext);

			// AddItemWithCustomSizePosition (via AddItemsWithCustomSize) already selected the
			// newly created item as a side effect - that's the only handle back to it.
			var createdItem = container.Services.Selection.PrimarySelection;
			if (createdItem != null && !string.IsNullOrEmpty(elementName))
				createdItem.Name = elementName;

			return JsonSerializer.Serialize(new {
				success = true,
				createdTypeName = createdItem?.ComponentType?.Name,
				createdName = createdItem?.Name,
				containerName = container.Name
			});
		}

		/// <summary>
		/// Also reachable directly as od.wpf-designer.flush-pending-transaction: the real
		/// toolbox-drag-drop path (WpfToolbox -> DragDrop.DoDragDrop -> CreateComponentTool's
		/// DragOver/Drop handlers) can leave the SAME kind of unfinished ChangeGroup open as
		/// DropToolboxItem's direct CreateComponentTool calls do (see that method's comment) -
		/// nothing about it is specific to calling AddItemWithCustomSizePosition directly, it's
		/// inherent to how the underlying transaction stack is used. A test driving a real
		/// synthetic mouse drag has no single call site to hang this off of, so expose it
		/// standalone to call once after the drag (and any property edits through the Properties
		/// pad, which land in the same still-open transaction) before saving.
		/// </summary>
		[DevFlowAction("od.wpf-designer.flush-pending-transaction", Description = "Commit any ChangeGroup left open on the active WPF designer's undo transaction stack (e.g. after a real toolbox drag-drop), so subsequent edits/saves see a consistent document instead of one still mid-transaction")]
		public static string FlushPendingTransaction()
		{
			var viewContent = FindWpfViewContent();
			if (viewContent?.DesignContext == null)
				return JsonSerializer.Serialize(new { success = false, error = "WPF designer is not loaded" });

			int committed = 0;
			while (CommitLeakedChangeGroup(viewContent.DesignContext))
				committed++;

			return JsonSerializer.Serialize(new { success = true, committed });
		}

		static bool CommitLeakedChangeGroup(DesignContext context)
		{
			var undoService = context.Services.GetService(typeof(UndoService)) as UndoService;
			var stack = undoService?.GetType()
				.GetField("_transactionStack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
				?.GetValue(undoService);
			var count = (int?)(stack?.GetType().GetProperty("Count")?.GetValue(stack)) ?? 0;
			if (count == 0)
				return false;
			var top = stack?.GetType().GetMethod("Peek")?.Invoke(stack, null) as ChangeGroup;
			top?.Commit();
			return true;
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

		[DevFlowAction("od.wpf-designer.properties-pad.edit", Description = "Edit a property through the real Xceed Properties pad PropertyItem; does not access DesignItemProperty directly")]
		public static string EditPropertyThroughPropertiesPad(string propertyName, string value)
		{
			var grid = PropertyPadGrid;
			if (grid == null)
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad is not available" });
			if (!(grid.SelectedObject is DesignItemPropertyGridAdapter selectedObject))
				return JsonSerializer.Serialize(new { success = false, error = "Properties pad has no selected WPF design item" });

			// Use the PropertyItem generated and owned by the visible Xceed PropertyGrid. Setting its
			// Value exercises the pad's normal binding -> PropertyDescriptor -> designer adapter path;
			// deliberately do not reach into DesignItem.Properties here.
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
			} catch (System.Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}

			item.Value = convertedValue;
			var after = item.Value;
			return JsonSerializer.Serialize(new {
				success = true,
				selectedName = selectedObject.DesignItem.Name,
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
		/// content isn't enough either: SharpDevelop only calls LoadInternal (which constructs the
		/// DesignSurface) on a secondary view when its tab actually becomes active, so
		/// WpfViewContent.DesignContext throws NullReferenceException (designer surface field never
		/// set) until we switch to it via IWorkbenchWindow.SwitchView.
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

		static void CollectOutlineNames(IOutlineNode node, List<string> names)
		{
			if (node == null)
				return;

			// node.Name is a human-readable display string ("Border (PaneBorder)" via
			// OutlineNodeNameService.GetOutlineNodeName), not the bare x:Name. DevFlow
			// callers want the actual element identifiers, so prefer DesignItem.Name
			// (falling back to the display string for unnamed/root nodes).
			var name = node.DesignItem?.Name;
			names.Add(string.IsNullOrEmpty(name) ? node.Name : name);
			foreach (var child in node.Children)
				CollectOutlineNames(child, names);
		}

		static DesignItem FindDesignItem(DesignItem item, string elementName)
		{
			if (item == null || item.Name == elementName)
				return item;
			var content = item.ContentProperty;
			if (content == null)
				return null;
			if (content.IsCollection) {
				foreach (var child in content.CollectionElements) {
					var match = FindDesignItem(child, elementName);
					if (match != null)
						return match;
				}
				return null;
			}
			return FindDesignItem(content.Value, elementName);
		}
	}
}
