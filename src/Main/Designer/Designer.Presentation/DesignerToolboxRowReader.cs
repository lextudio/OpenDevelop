// Reads what a Toolbox pad is literally rendering, so a test can assert on it.
//
// The two Toolbox shapes cannot be merged: the deserializing designers (GTK, MewUI) bind a plain
// ListBox straight to the protocol's DesignerToolboxItemInfo, while WPF, WinUI and WinForms share
// one ListBox over SharedToolboxItem. Rather than force one of them to change, this reports the
// rendered row for either - label and glyph kind per row, in the same flattened shape
// od.outline-pad.content reports for the Outline, so a test reads both the same way.
//
// Reading the realized rows rather than the bound data is deliberate: a row template that fails to
// bind its icon compiles, passes every unit test, and shows a blank row at runtime. Only the visual
// tree knows.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfVisualTreeHelper = System.Windows.Media.VisualTreeHelper;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>One rendered Toolbox row: the label it shows and what its glyph actually is.</summary>
	public sealed record DesignerToolboxRow(string Label, string? GlyphKind);

	/// <summary>Reads the rows a Toolbox pad is displaying.</summary>
	public static class DesignerToolboxRowReader
	{
		/// <summary>The rendered rows of an items control, in visual order. Returns an empty list when
		/// the control has not been laid out yet - a caller that needs rows should wait for a
		/// non-empty result rather than treat empty as "no icons", which is the failure it is meant
		/// to detect.</summary>
		public static IReadOnlyList<DesignerToolboxRow> Read(ItemsControl? items)
		{
			var rows = new List<DesignerToolboxRow>();
			if (items == null)
				return rows;
			// ContainerFromItem returns null for a virtualizing ListBox until a row is realized, and
			// a freshly-shown pad may have realized none - reading only containers then reports "no
			// icons" for a pad that is merely not scrolled yet. So read what is actually on screen
			// (the realized ListBoxItem children) and, if nothing is realized yet, resolve each item
			// through its own ItemTemplate instead, which is the same visual the pad would build.
			foreach (var realized in RealizedRows(items)) {
				rows.Add(new DesignerToolboxRow(LabelOf(realized), GlyphKindOf(realized)));
			}
			if (rows.Count > 0)
				return rows;
			var template = items.ItemTemplate;
			foreach (var item in items.Items) {
				if (template == null)
					break;
				DependencyObject? built = null;
				try {
					built = template.LoadContent();
				} catch (InvalidOperationException) {
					// The template binds against the row's DataContext; without one it may throw, and
					// an unreadable row is a real finding rather than something to swallow silently.
					break;
				}
				if (built != null)
					rows.Add(new DesignerToolboxRow(LabelOf(built), GlyphKindOf(built)));
			}
			return rows;
		}

		/// <summary>The rows currently realized in the visual tree, outermost first.</summary>
		static IEnumerable<DependencyObject> RealizedRows(ItemsControl items)
		{
			foreach (var realized in Descendants(items)) {
				if (realized is ListBoxItem or ContentPresenter or HeaderedContentControl)
					yield return realized;
			}
		}

		static IEnumerable<DependencyObject> Descendants(DependencyObject node)
		{
			var count = WpfVisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++) {
				var child = WpfVisualTreeHelper.GetChild(node, i);
				if (child is ScrollViewer)
					continue; // a scrollviewer has no rows of its own
				yield return child;
				foreach (var nested in Descendants(child))
					yield return nested;
			}
		}

		/// <summary>The row's text, taken from the header/row itself rather than the data item, so a
		/// template that renders nothing is reported as an empty label instead of the item's name.</summary>
		static string LabelOf(DependencyObject row)
		{
			var header = (row as HeaderedContentControl)?.Header ?? row;
			if (header == null)
				return "";
			var text = string.Join("", TextBlocks(header).Select(block => block.Text)).Trim();
			if (text.Length > 0)
				return text;
			return (row as ContentControl)?.Content as string ?? "";
		}

		static IEnumerable<TextBlock> TextBlocks(object node)
		{
			var count = WpfVisualTreeHelper.GetChildrenCount(node as DependencyObject);
			for (var i = 0; i < count; i++) {
				var child = WpfVisualTreeHelper.GetChild(node as DependencyObject, i);
				if (child is TextBlock block)
					yield return block;
				else if (child is Panel or Decorator)
					foreach (var nested in TextBlocks(child))
						yield return nested;
			}
		}

		/// <summary>"vector" for a VS Image Library DrawingImage, "bitmap" for a real framework icon
		/// (e.g. a WinForms toolbox bitmap), or null when the row has no glyph at all.</summary>
		static string? GlyphKindOf(DependencyObject row)
		{
			var image = FirstImage(row);
			var source = image?.Source;
			if (source == null)
				return null;
			return source is DrawingImage ? "vector" : "bitmap";
		}

		static Image? FirstImage(DependencyObject node)
		{
			if (node is Image direct)
				return direct;
			var count = WpfVisualTreeHelper.GetChildrenCount(node as DependencyObject);
			for (var i = 0; i < count; i++) {
				var found = FirstImage(WpfVisualTreeHelper.GetChild(node as DependencyObject, i));
				if (found != null)
					return found;
			}
			return null;
		}
	}
}
