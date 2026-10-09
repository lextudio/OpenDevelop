using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;

namespace ICSharpCode.WinUIXamlDesigner.ProGPUHost;

/// <summary>
/// The design tree of a rendered ProGPU WinUI page, in the shape the shared design canvas expects
/// from every backend (the same one the Uno/WinUI design host builds): one node per visual element
/// with its design-unit bounds, keyed by its visual-child path ("0,2,1"; the root is ""), with
/// template parts marked non-designable so a click never selects something the document does not
/// declare.
/// </summary>
sealed class ProGpuDesignTree
{
	const int MaxTreeDepth = 64;

	readonly Dictionary<Visual, string> pathOf = new(ReferenceEqualityComparer.Instance);
	readonly HashSet<Visual> sourceBacked = new(ReferenceEqualityComparer.Instance);
	readonly FrameworkElement root;

	ProGpuDesignTree(FrameworkElement root) => this.root = root;

	public DesignerElementNode Tree { get; private set; } = new();

	/// <param name="names">Rendered elements to their x:Name (the generated program publishes names
	/// through the WinUI namescope; the emitter never assigns FrameworkElement.Name).</param>
	public static ProGpuDesignTree Build(FrameworkElement root, IReadOnlyDictionary<FrameworkElement, string> names)
	{
		var tree = new ProGpuDesignTree(root);
		tree.CollectSourceBacked(root, 0);
		tree.Tree = tree.BuildNode(root, "", 0, names, DesignerLayoutMode.Unknown);
		return tree;
	}

	/// <summary>
	/// The innermost document-declared element under a design point, as a canvas hit: its path,
	/// plus the named elements from it up to the root. A hit on a template part walks up to the
	/// element that owns it.
	/// </summary>
	public (bool Hit, string? PickPath, List<string> Chain) HitTest(Vector2 point, IReadOnlyDictionary<FrameworkElement, string> names)
	{
		Microsoft.UI.Xaml.Input.InputSystem.Current.Root = root;
		var hit = (Visual?)Microsoft.UI.Xaml.Input.InputSystem.HitTest(point);
		string? pickPath = null;
		var chain = new List<string>();
		for (var node = hit; node != null; node = node.Parent)
		{
			if (pickPath == null && sourceBacked.Contains(node) && pathOf.TryGetValue(node, out var path))
				pickPath = path;
			if (node is FrameworkElement element && names.TryGetValue(element, out var name) && !chain.Contains(name))
				chain.Add(name);
			if (ReferenceEquals(node, root))
				break;
		}
		return (pickPath != null, pickPath, chain);
	}

	DesignerElementNode BuildNode(Visual node, string path, int depth, IReadOnlyDictionary<FrameworkElement, string> names, string layoutMode)
	{
		pathOf[node] = path;
		var info = new DesignerElementNode {
			Path = path,
			Id = path,
			Type = node.GetType().Name,
			IsDesignable = sourceBacked.Contains(node),
			IsTemplatePart = !sourceBacked.Contains(node),
			IsVisible = IsEffectivelyVisible(node),
			LayoutMode = layoutMode,
			ZIndex = path.Length == 0 ? null : node is FrameworkElement zOrdered ? Canvas.GetZIndex(zOrdered) : null,
			BaselineOffset = node is TextBlock text ? text.BaselineOffset : null,
		};
		if (node is FrameworkElement element)
		{
			info.Name = names.TryGetValue(element, out var name) ? name : string.IsNullOrEmpty(element.Name) ? null : element.Name;
			var origin = element.TransformToVisual(root).TransformPoint(Vector2.Zero);
			info.X = origin.X;
			info.Y = origin.Y;
			info.Width = element.Size.X;
			info.Height = element.Size.Y;
			// TabIndex is the one value the tab-order badges need without selecting the element.
			if (element is Microsoft.UI.Xaml.Controls.Control control)
				info.Properties.Add(new DesignerPropertyInfo { Name = "TabIndex", Value = control.TabIndex.ToString(CultureInfo.InvariantCulture) });
		}
		if (depth >= MaxTreeDepth || node is not ContainerVisual container)
			return info;
		var index = 0;
		foreach (var child in container.Children)
		{
			if (child is UIElement)
				info.Children.Add(BuildNode(child, path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "," + index, depth + 1, names,
					DesignerLayoutMode.InferFromContainerType(info.Type)));
			index++;
		}
		return info;
	}

	/// <summary>Collapsed anywhere up the chain means not on screen (the root excluded: it is the
	/// design itself, whatever its own Visibility says).</summary>
	bool IsEffectivelyVisible(Visual node)
	{
		for (var current = node; current != null && !ReferenceEquals(current, root); current = current.Parent)
		{
			if (current is UIElement element && element.Visibility == Visibility.Collapsed)
				return false;
		}
		return true;
	}

	/// <summary>Everything the document's markup could have declared: reached from the root through
	/// markup-settable child properties (Panel.Children, Border.Child, ContentControl.Content, ...)
	/// and never through a template. What is left - control-template parts - is not designable.</summary>
	void CollectSourceBacked(object node, int depth)
	{
		if (node is not Visual visual || depth >= MaxTreeDepth || !sourceBacked.Add(visual))
			return;
		foreach (var child in EnumerateMarkupChildren(node))
			CollectSourceBacked(child, depth + 1);
	}

	static IEnumerable<object> EnumerateMarkupChildren(object node)
	{
		if (node is Microsoft.UI.Xaml.Controls.Panel panel)
		{
			foreach (var child in panel.Children)
				yield return child;
		}
		foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.GetIndexParameters().Length != 0 || !property.CanRead)
				continue;
			// Reading these either walks back up, or forces template inflation / item
			// materialisation as a side effect, or reaches template parts.
			if (property.Name is "Parent" or "Children" or "VisualChildren" or "Resources" or "Template"
				or "ContentTemplate" or "ContentTemplateRoot" or "ContentVisual" or "ItemTemplate"
				or "HeaderTemplate" or "FooterTemplate" or "ItemsPanel" or "ItemContainerStyle" or "Style"
				or "DataContext" or "TemplateSettings" or "Transitions" or "ContentTransitions" or "Font"
				or "LayerTexture" or "Effect" or "OpacityMask" or "Transform" or "RenderTransform" or "ToolTip")
				continue;
			var type = property.PropertyType;
			if (!typeof(UIElement).IsAssignableFrom(type) && type != typeof(object)
				&& !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
				continue;
			if (type == typeof(string))
				continue;
			object? value;
			try
			{
				value = property.GetValue(node);
			}
			catch
			{
				continue;
			}
			if (value is UIElement element)
				yield return element;
			else if (value is System.Collections.IEnumerable items && value is not string)
			{
				foreach (var item in items.OfType<UIElement>())
					yield return item;
			}
		}
	}
}
