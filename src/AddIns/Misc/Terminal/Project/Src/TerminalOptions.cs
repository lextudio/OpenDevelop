using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Gui;

namespace OpenDevelop.Terminal
{
	/// <summary>Tools &gt; Options &gt; Terminal: default shell, font size, scrollback.</summary>
	public sealed class TerminalOptions : OptionPanel
	{
		const string Automatic = "(login shell)";
		readonly ComboBox shell = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
		readonly TextBox fontSize = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
		readonly TextBox scrollback = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };

		public TerminalOptions()
		{
			var shells = TerminalShells.Detect();
			shell.ItemsSource = new[] { Automatic }.Concat(shells).ToList();
			var stored = TerminalShells.Stored;
			shell.SelectedItem = !string.IsNullOrEmpty(stored) && shells.Contains(stored, StringComparer.OrdinalIgnoreCase) ? stored : Automatic;
			fontSize.Text = TerminalSettings.FontSize.ToString(CultureInfo.InvariantCulture);
			scrollback.Text = TerminalSettings.ScrollbackLines.ToString(CultureInfo.InvariantCulture);

			var panel = new StackPanel { Margin = new Thickness(8) };
			void Row(string label, FrameworkElement editor)
			{
				panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2) });
				panel.Children.Add(editor);
			}
			Row("Default shell for new terminals", shell);
			Row("Font size (points, applies immediately)", fontSize);
			Row("Scrollback lines (applies to terminals started afterwards)", scrollback);
			Content = panel;
		}

		public override bool SaveOptions()
		{
			TerminalShells.Default = Equals(shell.SelectedItem, Automatic) ? "" : (string)shell.SelectedItem;
			if (double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
				TerminalSettings.FontSize = size;
			if (int.TryParse(scrollback.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lines))
				TerminalSettings.ScrollbackLines = lines;
			TerminalSettings.RaiseChanged();
			return base.SaveOptions();
		}
	}
}
