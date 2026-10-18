using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Presentation;

namespace ICSharpCode.FormsDesigner.Gui;

/// <summary>The one modal editor for a TableLayoutPanel's authored row and column styles. It keeps
/// editable copies until OK, allowing the host to make both axes one undoable operation.</summary>
sealed class TableLayoutTracksDialog : Window
{
	readonly TableLayoutTracksEditModel model;
	readonly ListBox tracks = new() { MinWidth = 210, Margin = new Thickness(8) };
	readonly ComboBox sizeType = new() { Width = 130, Margin = new Thickness(8, 3, 8, 3) };
	readonly TextBox value = new() { Width = 130, Margin = new Thickness(8, 3, 8, 3) };
	readonly RadioButton rowButton = new() { Content = "Rows", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
	readonly RadioButton columnButton = new() { Content = "Columns" };
	bool editing;

	TableLayoutTracksDialog(Window? owner, DesignerTableLayoutTracks source)
	{
		Owner = owner; Title = "Edit Rows and Columns"; Width = 410; Height = 315;
		WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
		model = new TableLayoutTracksEditModel(source);
		var root = new DockPanel();
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) };
		ok.Click += (_, _) => Accept();
		buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 76, Margin = new Thickness(4, 0, 0, 0) });
		DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
		var axis = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 0) };
		rowButton.Checked += (_, _) => SwitchAxis(true);
		columnButton.Checked += (_, _) => SwitchAxis(false);
		axis.Children.Add(rowButton); axis.Children.Add(columnButton); DockPanel.SetDock(axis, Dock.Top); root.Children.Add(axis);
		var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		tracks.SelectionChanged += (_, _) => SelectTrack(); Grid.SetColumn(tracks, 0); content.Children.Add(tracks);
		var editor = new StackPanel { Margin = new Thickness(4, 8, 8, 8) };
		editor.Children.Add(new TextBlock { Text = "Size type" });
		sizeType.Items.Add("Absolute"); sizeType.Items.Add("Percent"); sizeType.Items.Add("AutoSize");
		sizeType.SelectionChanged += (_, _) => { if (!editing) UpdateValueEnabled(); };
		editor.Children.Add(sizeType); editor.Children.Add(new TextBlock { Text = "Height / width" }); editor.Children.Add(value);
		editor.Children.Add(new TextBlock { Text = "Changes are applied together\nwhen you choose OK.", Margin = new Thickness(8, 12, 8, 0), TextWrapping = TextWrapping.Wrap });
		Grid.SetColumn(editor, 1); content.Children.Add(editor); root.Children.Add(content);
		Content = root; Refresh();
	}

	void Refresh()
	{
		editing = true; tracks.Items.Clear();
		var current = model.ShowingRows ? model.Rows : model.Columns;
		for (var i = 0; i < current.Count; i++) tracks.Items.Add($"{(model.ShowingRows ? "Row" : "Column")} {i + 1}");
		tracks.SelectedIndex = model.SelectedIndex; editing = false; ShowSelection();
	}
	void SwitchAxis(bool rows)
	{
		if (editing || model.ShowingRows == rows) return;
		if (!model.TrySwitchAxis(rows, (string?)sizeType.SelectedItem ?? "AutoSize", value.Text, out var error)) {
			// The click changes the checked state before this event; restore the axis whose
			// invalid editor value is still displayed.
			MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
			editing = true; (model.ShowingRows ? rowButton : columnButton).IsChecked = true; editing = false;
			return;
		}
		Refresh();
	}
	void SelectTrack()
	{
		if (editing) return;
		if (!model.TrySelect(tracks.SelectedIndex, (string?)sizeType.SelectedItem ?? "AutoSize", value.Text, out var error)) {
			MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
			editing = true; tracks.SelectedIndex = model.SelectedIndex; editing = false;
			return;
		}
		ShowSelection();
	}
	void ShowSelection()
	{
		if (editing || tracks.SelectedIndex < 0) return;
		editing = true; sizeType.SelectedItem = model.CurrentSizeType;
		value.Text = model.CurrentValue; editing = false; UpdateValueEnabled();
	}
	void UpdateValueEnabled() => value.IsEnabled = !String.Equals((string?)sizeType.SelectedItem, "AutoSize", StringComparison.Ordinal);
	void Accept()
	{
		if (model.TryCommit((string?)sizeType.SelectedItem ?? "AutoSize", value.Text, out var error)) DialogResult = true;
		else MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	public static bool Show(Window? owner, DesignerTableLayoutTracks source, out IReadOnlyList<DesignerTableLayoutTrackEdit> rows, out IReadOnlyList<DesignerTableLayoutTrackEdit> columns)
	{
		var dialog = new TableLayoutTracksDialog(owner, source); var accepted = dialog.ShowDialog() == true;
		rows = dialog.model.Rows; columns = dialog.model.Columns; return accepted;
	}
}
