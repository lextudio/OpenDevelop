using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ICSharpCode.Translations;

/// <summary>The translator's grid over a <see cref="TranslatorModel"/>: a filter and search box on top, one row per entry
/// (state, source, translation, fuzzy, the translator's comment, and the source's own notes and references), and
/// the progress line below. Built in code, like the designers' views.</summary>
public sealed class TranslatorView : Grid
{
	readonly TranslatorModel model;
	readonly DataGrid grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
	readonly ComboBox filter = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
	readonly TextBox search = new TextBox { MinWidth = 220 };
	readonly TextBlock summary = new TextBlock { Margin = new Thickness(8, 4, 8, 4) };

	public TranslatorView(TranslatorModel model)
	{
		this.model = model;
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		RowDefinitions.Add(new RowDefinition());
		RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
		foreach (TranslationFilter value in Enum.GetValues(typeof(TranslationFilter))) filter.Items.Add(new ComboBoxItem { Content = Describe(value), Tag = value });
		filter.SelectedIndex = 0;
		filter.SelectionChanged += (_, _) => { model.Filter = SelectedFilter; Refresh(); };
		search.TextChanged += (_, _) => { model.Search = search.Text; Refresh(); };
		bar.Children.Add(filter);
		bar.Children.Add(new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
		bar.Children.Add(search);
		SetRow(bar, 0);
		Children.Add(bar);

		grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(TranslatorRow.State)) { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 90 });
		grid.Columns.Add(new DataGridTemplateColumn { Header = "Source", IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), CellTemplate = WrappedText(nameof(TranslatorRow.Source)) });
		grid.Columns.Add(new DataGridTemplateColumn {
			Header = "Translation", Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
			CellTemplate = WrappedText(nameof(TranslatorRow.Translation)), CellEditingTemplate = EditingText(nameof(TranslatorRow.Translation))
		});
		grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Fuzzy", Binding = new Binding(nameof(TranslatorRow.IsFuzzy)) { Mode = BindingMode.TwoWay }, Width = 56 });
		grid.Columns.Add(new DataGridTemplateColumn {
			Header = "Comment", Width = new DataGridLength(0.8, DataGridLengthUnitType.Star),
			CellTemplate = WrappedText(nameof(TranslatorRow.TranslatorComment)), CellEditingTemplate = EditingText(nameof(TranslatorRow.TranslatorComment))
		});
		grid.Columns.Add(new DataGridTemplateColumn { Header = "Notes", IsReadOnly = true, Width = new DataGridLength(0.8, DataGridLengthUnitType.Star), CellTemplate = Notes() });
		// A plural or obsolete entry is listed for completeness, but its forms and its fate are the source view's to edit.
		grid.BeginningEdit += (_, e) => { if (e.Row.Item is TranslatorRow { IsReadOnly: true } && e.Column.Header as string != "Fuzzy") e.Cancel = true; };
		SetRow(grid, 1);
		Children.Add(grid);

		SetRow(summary, 2);
		Children.Add(summary);

		model.Reloaded += (_, _) => Refresh();
		Refresh();
	}

	/// <summary>How many rows the grid lists, and its column headers (what a test reads to prove the view shows the model).</summary>
	public int ListedRowCount => grid.Items.Count;
	public IReadOnlyList<string> ColumnHeaders => grid.Columns.Select(c => c.Header as string ?? "").ToList();

	TranslationFilter SelectedFilter => filter.SelectedItem is ComboBoxItem { Tag: TranslationFilter selected } ? selected : TranslationFilter.All;

	static string Describe(TranslationFilter value) => value switch {
		TranslationFilter.NeedsWork => "Needs work",
		TranslationFilter.Untranslated => "Untranslated",
		_ => value.ToString()
	};

	/// <summary>Re-lists the rows for the current filter and search, and the progress line.</summary>
	public void Refresh()
	{
		grid.CommitEdit(DataGridEditingUnit.Row, true);
		grid.ItemsSource = model.VisibleRows;
		var language = model.Language.Length == 0 ? "" : model.Language + ": ";
		summary.Text = $"{language}{model.TranslatedCount} of {model.Total} translated ({model.PercentComplete:0}%), {model.FuzzyCount} fuzzy, {model.UntranslatedCount} untranslated"
			+ (model.ObsoleteCount > 0 ? $", {model.ObsoleteCount} obsolete" : "");
	}

	/// <summary>Updates the progress line after an edit without re-listing (which would drop the row being edited).</summary>
	public void RefreshSummary()
	{
		var language = model.Language.Length == 0 ? "" : model.Language + ": ";
		summary.Text = $"{language}{model.TranslatedCount} of {model.Total} translated ({model.PercentComplete:0}%), {model.FuzzyCount} fuzzy, {model.UntranslatedCount} untranslated"
			+ (model.ObsoleteCount > 0 ? $", {model.ObsoleteCount} obsolete" : "");
	}

	/// <summary>Shows the filter and search a test or a command asked for.</summary>
	public void Show(TranslationFilter shownFilter, string searchText)
	{
		for (var i = 0; i < filter.Items.Count; i++)
			if (filter.Items[i] is ComboBoxItem { Tag: TranslationFilter tag } && tag == shownFilter) filter.SelectedIndex = i;
		search.Text = searchText;
		model.Filter = shownFilter; model.Search = searchText;
		Refresh();
	}

	static DataTemplate WrappedText(string path)
	{
		var factory = new FrameworkElementFactory(typeof(TextBlock));
		factory.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
		factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
		factory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
		return new DataTemplate { VisualTree = factory };
	}

	static DataTemplate EditingText(string path)
	{
		var factory = new FrameworkElementFactory(typeof(TextBox));
		factory.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
		factory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
		factory.SetValue(TextBox.AcceptsReturnProperty, true);
		return new DataTemplate { VisualTree = factory };
	}

	static DataTemplate Notes()
	{
		var factory = new FrameworkElementFactory(typeof(TextBlock));
		var binding = new MultiBinding { Converter = new NotesConverter(), Mode = BindingMode.OneWay };
		binding.Bindings.Add(new Binding(nameof(TranslatorRow.ExtractedComment)));
		binding.Bindings.Add(new Binding(nameof(TranslatorRow.References)));
		factory.SetBinding(TextBlock.TextProperty, binding);
		factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
		factory.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
		factory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
		return new DataTemplate { VisualTree = factory };
	}

	sealed class NotesConverter : IMultiValueConverter
	{
		public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			var parts = new List<string>();
			foreach (var value in values) if (value is string { Length: > 0 } text) parts.Add(text);
			return string.Join("\n", parts);
		}
		public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
	}
}
