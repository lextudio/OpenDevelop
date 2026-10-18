using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.FormsDesigner.Services;

/// <summary>Edits a WinForms RichTextBox in its native RTF format.</summary>
public sealed class FormsRichTextEditor : ITypeEditor
{
	public FrameworkElement ResolveEditor(PropertyItem propertyItem)
	{
		var panel = new DockPanel { LastChildFill = true };
		var button = new Button { Content = "…", ToolTip = "Edit formatted text", MinWidth = 22, Padding = new Thickness(3, 0, 3, 0) };
		button.Click += (_, _) => Edit(propertyItem, button);
		DockPanel.SetDock(button, Dock.Right); panel.Children.Add(button);
		var value = new TextBlock { Margin = new Thickness(5, 0, 2, 0), Text = "Edit formatted text…", VerticalAlignment = VerticalAlignment.Center };
		panel.Children.Add(value);
		return panel;
	}

	static void Edit(PropertyItem item, FrameworkElement owner)
	{
		var editor = new RichTextBox { Margin = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
		try {
			using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Convert.ToString(item.Value) ?? ""));
			editor.SelectAll(); editor.Selection.Load(source, DataFormats.Rtf);
		} catch (Exception ex) {
			MessageBox.Show(Window.GetWindow(owner), "This RTF content cannot be edited without losing data.\n\n" + ex.Message, "Formatted text", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		var dialog = new Window { Title = "Edit formatted text", Owner = Window.GetWindow(owner), Width = 560, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
		var root = new DockPanel();
		var toolbar = new ToolBar { Margin = new Thickness(8, 8, 8, 0) };
		toolbar.Items.Add(CommandButton("B", "Bold", EditingCommands.ToggleBold, editor));
		toolbar.Items.Add(CommandButton("I", "Italic", EditingCommands.ToggleItalic, editor));
		toolbar.Items.Add(CommandButton("U", "Underline", EditingCommands.ToggleUnderline, editor));
		DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) };
		ok.Click += (_, _) => {
			try {
				using var output = new MemoryStream(); editor.SelectAll(); editor.Selection.Save(output, DataFormats.Rtf);
				item.PropertyDescriptor.SetValue(item.Instance, System.Text.Encoding.UTF8.GetString(output.ToArray()));
				dialog.DialogResult = true;
			} catch (Exception ex) {
				MessageBox.Show(dialog, ex.Message, "Formatted text", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		};
		buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) });
		DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(editor); dialog.Content = root; dialog.ShowDialog();
	}

	static Button CommandButton(string text, string toolTip, RoutedUICommand command, RichTextBox editor)
	{
		var button = new Button { Content = text, ToolTip = toolTip, MinWidth = 26 };
		button.Click += (_, _) => command.Execute(null, editor);
		return button;
	}
}
