using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>Edits the portable direct-Run subset used by XAML RichTextBlock source.</summary>
static class RichTextRunEditorDialog
{
	public static bool Edit(Window owner, IReadOnlyList<DesignerRichTextRun> initial, out IReadOnlyList<DesignerRichTextRun> result)
	{
		result = null;
		IReadOnlyList<DesignerRichTextRun> edited = null;
		var editor = new RichTextBox { Margin = new Thickness(8), AcceptsReturn = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		var paragraph = new Paragraph();
		foreach (var run in initial)
			paragraph.Inlines.Add(CreateRun(run));
		editor.Document.Blocks.Add(paragraph);
		var dialog = new Window {
			Title = "Edit formatted text", Owner = owner, Width = 560, Height = 360,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		var root = new DockPanel();
		var toolbar = new ToolBar { Margin = new Thickness(8, 8, 8, 0) };
		var family = new ComboBox { Width = 140, ItemsSource = Fonts.SystemFontFamilies, ToolTip = "Font family" };
		family.SelectionChanged += (_, _) => {
			if (family.SelectedItem is FontFamily value)
				editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, value);
		};
		toolbar.Items.Add(family);
		var size = new ComboBox { Width = 54, IsEditable = true, ToolTip = "Font size", ItemsSource = new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36" } };
		void ApplySize() {
			if (Double.TryParse(size.Text, out var value) && Double.IsFinite(value) && value > 0)
				editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, value);
		}
		size.SelectionChanged += (_, _) => ApplySize();
		size.LostKeyboardFocus += (_, _) => ApplySize();
		toolbar.Items.Add(size);
		toolbar.Items.Add(Button("B", "Bold", () => editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold)));
		toolbar.Items.Add(Button("I", "Italic", () => editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic)));
		toolbar.Items.Add(Button("U", "Underline", () => ToggleDecoration(editor, TextDecorationLocation.Underline)));
		toolbar.Items.Add(Button("S", "Strikethrough", () => ToggleDecoration(editor, TextDecorationLocation.Strikethrough)));
		DockPanel.SetDock(toolbar, Dock.Top);
		root.Children.Add(toolbar);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) };
		ok.Click += (_, _) => {
			if (!TryExtract(editor, out var runs)) {
				MessageBox.Show(dialog, "Only one paragraph containing direct text runs can be saved.", "Formatted text", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			edited = runs;
			dialog.DialogResult = true;
		};
		buttons.Children.Add(ok);
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) };
		buttons.Children.Add(cancel);
		DockPanel.SetDock(buttons, Dock.Bottom);
		root.Children.Add(buttons);
		root.Children.Add(editor);
		dialog.Content = root;
		if (dialog.ShowDialog() != true)
			return false;
		result = edited;
		return true;
	}

	static Button Button(string text, string tip, Action action)
	{
		var button = new Button { Content = text, ToolTip = tip, MinWidth = 26 };
		button.Click += (_, _) => action();
		return button;
	}

	static void ToggleDecoration(RichTextBox editor, TextDecorationLocation location)
	{
		var decorations = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
		var updated = new TextDecorationCollection(decorations ?? new TextDecorationCollection());
		var existing = updated.FirstOrDefault(value => value.Location == location);
		if (existing != null) updated.Remove(existing);
		else updated.Add(location == TextDecorationLocation.Underline ? TextDecorations.Underline[0] : TextDecorations.Strikethrough[0]);
		editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, updated.Count == 0 ? null : updated);
	}

	static Run CreateRun(DesignerRichTextRun value)
	{
		var run = new Run(value.Text ?? "");
		if (value.Bold) run.FontWeight = FontWeights.Bold;
		if (value.Italic) run.FontStyle = FontStyles.Italic;
		if (value.Underline || value.Strikethrough) {
			var decorations = new TextDecorationCollection();
			if (value.Underline) decorations.Add(TextDecorations.Underline[0]);
			if (value.Strikethrough) decorations.Add(TextDecorations.Strikethrough[0]);
			run.TextDecorations = decorations;
		}
		if (!String.IsNullOrEmpty(value.Foreground)) run.Foreground = (Brush)new BrushConverter().ConvertFromString(value.Foreground);
		if (!String.IsNullOrEmpty(value.FontFamily)) run.FontFamily = new FontFamily(value.FontFamily);
		if (!Double.IsNaN(value.FontSize)) run.FontSize = value.FontSize;
		return run;
	}

	static bool TryExtract(RichTextBox editor, out IReadOnlyList<DesignerRichTextRun> result)
	{
		result = null;
		if (editor.Document.Blocks.Count != 1 || editor.Document.Blocks.FirstBlock is not Paragraph paragraph || paragraph.Inlines.Any(inline => inline is not Run and not LineBreak)) return false;
		var extracted = new List<DesignerRichTextRun>();
		foreach (var inline in paragraph.Inlines)
		{
			if (inline is LineBreak) {
				extracted.Add(new DesignerRichTextRun { Text = Environment.NewLine });
				continue;
			}
			var run = (Run)inline;
			if (HasLocalValue(run, TextElement.BackgroundProperty) || HasLocalValue(run, TextElement.FontStretchProperty) || HasLocalValue(run, TextElement.TextEffectsProperty) || HasLocalValue(run, FrameworkContentElement.StyleProperty) || HasLocalValue(run, TextElement.ForegroundProperty) && run.Foreground is not SolidColorBrush)
				return false;
			var decorations = run.TextDecorations;
			if (HasLocalValue(run, Inline.TextDecorationsProperty) && decorations?.Any(value => value.Location is not TextDecorationLocation.Underline and not TextDecorationLocation.Strikethrough || value.Pen != null) == true)
				return false;
			extracted.Add(new DesignerRichTextRun {
			Text = run.Text, Bold = run.FontWeight == FontWeights.Bold, Italic = run.FontStyle == FontStyles.Italic,
			Underline = HasLocalValue(run, Inline.TextDecorationsProperty) && HasDecoration(run, TextDecorationLocation.Underline), Strikethrough = HasLocalValue(run, Inline.TextDecorationsProperty) && HasDecoration(run, TextDecorationLocation.Strikethrough),
			Foreground = HasLocalValue(run, TextElement.ForegroundProperty) && run.Foreground is SolidColorBrush brush ? brush.Color.ToString() : "",
			FontFamily = HasLocalValue(run, TextElement.FontFamilyProperty) ? run.FontFamily?.Source ?? "" : "",
			FontSize = HasLocalValue(run, TextElement.FontSizeProperty) ? run.FontSize : Double.NaN
			});
		}
		result = extracted.Count == 0 ? new[] { new DesignerRichTextRun() } : extracted;
		return true;
	}

	static bool HasLocalValue(DependencyObject target, DependencyProperty property)
		=> target.ReadLocalValue(property) != DependencyProperty.UnsetValue;

	static bool HasDecoration(Run run, TextDecorationLocation location)
		=> run.TextDecorations?.Any(decoration => decoration.Location == location) == true;
}
