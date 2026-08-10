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

		static void CommitLeakedChangeGroup(DesignContext context)
		{
			var undoService = context.Services.GetService(typeof(UndoService)) as UndoService;
			var stack = undoService?.GetType()
				.GetField("_transactionStack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
				?.GetValue(undoService);
			var top = stack?.GetType().GetMethod("Peek")?.Invoke(stack, null) as ChangeGroup;
			top?.Commit();
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
