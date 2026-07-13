// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using HexEditor.Util;

namespace HexEditor.View
{
	/// <summary>
	/// Minimal WPF replacement for the WinForms FontDialog the options panel used to show -
	/// OpenDevelop has no WinForms/System.Drawing dependency, so this lists installed font
	/// families (Fonts.SystemFontFamilies) plus size/bold/italic/underline controls.
	/// </summary>
	public class FontChooserDialog : Window
	{
		readonly ComboBox familyBox;
		readonly TextBox sizeBox;
		readonly CheckBox boldBox;
		readonly CheckBox italicBox;
		readonly CheckBox underlineBox;
		readonly TextBlock preview;

		public FontSettings SelectedFont { get; private set; }

		public FontChooserDialog(FontSettings initial)
		{
			Title = "Choose Font";
			Width = 380;
			Height = 320;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;
			ResizeMode = ResizeMode.NoResize;

			var root = new Grid { Margin = new Thickness(12) };
			for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			familyBox = new ComboBox { Margin = new Thickness(0, 0, 0, 8), IsEditable = true };
			familyBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToList();
			familyBox.Text = initial.FamilyName;
			familyBox.SelectionChanged += (s, e) => UpdatePreview();
			Grid.SetRow(familyBox, 0);
			root.Children.Add(familyBox);

			var sizePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
			sizePanel.Children.Add(new TextBlock { Text = "Size:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
			sizeBox = new TextBox { Width = 60, Text = initial.Size.ToString("0.##") };
			sizeBox.TextChanged += (s, e) => UpdatePreview();
			sizePanel.Children.Add(sizeBox);
			Grid.SetRow(sizePanel, 1);
			root.Children.Add(sizePanel);

			var stylePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
			boldBox = new CheckBox { Content = "Bold", IsChecked = initial.Bold, Margin = new Thickness(0, 0, 12, 0) };
			italicBox = new CheckBox { Content = "Italic", IsChecked = initial.Italic, Margin = new Thickness(0, 0, 12, 0) };
			underlineBox = new CheckBox { Content = "Underline", IsChecked = initial.Underline };
			boldBox.Click += (s, e) => UpdatePreview();
			italicBox.Click += (s, e) => UpdatePreview();
			underlineBox.Click += (s, e) => UpdatePreview();
			stylePanel.Children.Add(boldBox);
			stylePanel.Children.Add(italicBox);
			stylePanel.Children.Add(underlineBox);
			Grid.SetRow(stylePanel, 2);
			root.Children.Add(stylePanel);

			var previewBox = new Border { BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(6) };
			preview = new TextBlock { Text = "AaBbYyZz 0123" };
			previewBox.Child = preview;
			Grid.SetRow(previewBox, 4);
			root.Children.Add(previewBox);

			var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
			var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };
			okButton.Click += OkButtonClick;
			buttonPanel.Children.Add(okButton);
			buttonPanel.Children.Add(cancelButton);
			Grid.SetRow(buttonPanel, 6);
			root.Children.Add(buttonPanel);

			Content = root;

			UpdatePreview();
		}

		void UpdatePreview()
		{
			if (!double.TryParse(sizeBox.Text, out double size) || size <= 0)
				size = 12;
			preview.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(familyBox.Text) ? "Consolas" : familyBox.Text);
			preview.FontSize = Math.Min(size, 48);
			preview.FontWeight = boldBox.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
			preview.FontStyle = italicBox.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
			preview.TextDecorations = underlineBox.IsChecked == true ? TextDecorations.Underline : null;
		}

		void OkButtonClick(object sender, RoutedEventArgs e)
		{
			if (!double.TryParse(sizeBox.Text, out double size) || size <= 0) {
				MessageBox.Show(this, "Please enter a valid font size.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			SelectedFont = new FontSettings(
				string.IsNullOrWhiteSpace(familyBox.Text) ? "Consolas" : familyBox.Text,
				size,
				boldBox.IsChecked == true,
				italicBox.IsChecked == true,
				underlineBox.IsChecked == true);
			DialogResult = true;
		}
	}
}
