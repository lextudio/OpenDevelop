#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Remote;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>Properties-pad entry point for the same portable direct-Run subset as canvas editing.</summary>
	public sealed class WpfRichTextEditor : ITypeEditor
	{
		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button { Content = "…", ToolTip = "Edit formatted text", MinWidth = 22, Padding = new Thickness(3, 0, 3, 0) };
			button.Click += (_, _) => Edit(propertyItem, button);
			DockPanel.SetDock(button, Dock.Right); panel.Children.Add(button);
			panel.Children.Add(new TextBlock { Text = Convert.ToString(propertyItem.Value), Margin = new Thickness(5, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
			return panel;
		}

		static void Edit(PropertyItem item, FrameworkElement owner)
		{
			if (item.Instance is not WpfSurfaceElementPropertyAdapter adapter || item.PropertyDescriptor is not WpfSurfacePropertyDescriptor descriptor)
				return;
			var editor = new RichTextBox { Margin = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
			var paragraph = new Paragraph();
			foreach (var value in descriptor.RichTextRuns) paragraph.Inlines.Add(CreateRun(value));
			editor.Document.Blocks.Add(paragraph);
			var dialog = new Window { Title = "Edit formatted text", Owner = Window.GetWindow(owner), Width = 560, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
			var root = new DockPanel();
			var toolbar = new ToolBar { Margin = new Thickness(8, 8, 8, 0) };
			toolbar.Items.Add(Button("B", () => editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold)));
			toolbar.Items.Add(Button("I", () => editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic)));
			toolbar.Items.Add(Button("U", () => ToggleDecoration(editor, TextDecorationLocation.Underline)));
			toolbar.Items.Add(Button("S", () => ToggleDecoration(editor, TextDecorationLocation.Strikethrough)));
			DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
			var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) };
			ok.Click += (_, _) => { if (TryExtract(editor, out var runs)) { try { adapter.SetRichText(runs); dialog.DialogResult = true; } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Formatted text", MessageBoxButton.OK, MessageBoxImage.Warning); } } else MessageBox.Show(dialog, "Only direct text runs with supported formatting can be saved.", "Formatted text", MessageBoxButton.OK, MessageBoxImage.Warning); };
			buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) });
			DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(editor); dialog.Content = root; dialog.ShowDialog();
		}

		static Button Button(string text, Action action) { var button = new Button { Content = text, MinWidth = 26 }; button.Click += (_, _) => action(); return button; }
		static void ToggleDecoration(RichTextBox editor, TextDecorationLocation location) { var values = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection; var updated = new TextDecorationCollection(values ?? new TextDecorationCollection()); var existing = updated.FirstOrDefault(value => value.Location == location); if (existing != null) updated.Remove(existing); else updated.Add(location == TextDecorationLocation.Underline ? TextDecorations.Underline[0] : TextDecorations.Strikethrough[0]); editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, updated.Count == 0 ? null : updated); }
		static Run CreateRun(DesignerRichTextRun value) { var run = new Run(value.Text ?? ""); if (value.Bold) run.FontWeight = FontWeights.Bold; if (value.Italic) run.FontStyle = FontStyles.Italic; if (value.Underline || value.Strikethrough) { var decorations = new TextDecorationCollection(); if (value.Underline) decorations.Add(TextDecorations.Underline[0]); if (value.Strikethrough) decorations.Add(TextDecorations.Strikethrough[0]); run.TextDecorations = decorations; } if (!String.IsNullOrWhiteSpace(value.Foreground)) run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value.Foreground)!); if (!String.IsNullOrWhiteSpace(value.FontFamily)) run.FontFamily = new FontFamily(value.FontFamily); if (Double.IsFinite(value.FontSize) && value.FontSize > 0) run.FontSize = value.FontSize; return run; }
		static bool TryExtract(RichTextBox editor, out IReadOnlyList<DesignerRichTextRun> result) {
			result = Array.Empty<DesignerRichTextRun>();
			if (editor.Document.Blocks.Count != 1 || editor.Document.Blocks.FirstBlock is not Paragraph paragraph || paragraph.Inlines.Any(inline => inline is not Run and not LineBreak)) return false;
			var list = new List<DesignerRichTextRun>();
			foreach (var inline in paragraph.Inlines) {
				if (inline is LineBreak) {
					list.Add(new DesignerRichTextRun { Text = Environment.NewLine });
					continue;
				}
				var run = (Run)inline;
				if (HasUnsupportedLocalValue(run) || HasLocal(run, TextElement.ForegroundProperty) && run.Foreground is not SolidColorBrush) return false;
				var decorations = run.TextDecorations;
				if (HasLocal(run, Inline.TextDecorationsProperty) && decorations?.Any(value => value.Location is not TextDecorationLocation.Underline and not TextDecorationLocation.Strikethrough || value.Pen != null) == true) return false;
				if (HasLocal(run, TextElement.FontWeightProperty) && run.FontWeight != FontWeights.Bold || HasLocal(run, TextElement.FontStyleProperty) && run.FontStyle != FontStyles.Italic) return false;
				list.Add(new DesignerRichTextRun { Text = run.Text ?? "", Bold = HasLocal(run, TextElement.FontWeightProperty) && run.FontWeight == FontWeights.Bold, Italic = HasLocal(run, TextElement.FontStyleProperty) && run.FontStyle == FontStyles.Italic, Underline = HasLocal(run, Inline.TextDecorationsProperty) && decorations?.Any(value => value.Location == TextDecorationLocation.Underline) == true, Strikethrough = HasLocal(run, Inline.TextDecorationsProperty) && decorations?.Any(value => value.Location == TextDecorationLocation.Strikethrough) == true, Foreground = HasLocal(run, TextElement.ForegroundProperty) ? ((SolidColorBrush)run.Foreground).Color.ToString() : "", FontFamily = HasLocal(run, TextElement.FontFamilyProperty) ? run.FontFamily.Source : "", FontSize = HasLocal(run, TextElement.FontSizeProperty) ? run.FontSize : Double.NaN });
			}
			result = list.Count == 0 ? new[] { new DesignerRichTextRun() } : list;
			return true;
		}
		static bool HasLocal(DependencyObject value, DependencyProperty property) => value.ReadLocalValue(property) != DependencyProperty.UnsetValue;
		static bool HasUnsupportedLocalValue(Run run) {
			var locals = run.GetLocalValueEnumerator();
			while (locals.MoveNext()) if (locals.Current.Property.Name is not "Text" and not "FontWeight" and not "FontStyle" and not "TextDecorations" and not "Foreground" and not "FontFamily" and not "FontSize") return true;
			return false;
		}
	}
}
