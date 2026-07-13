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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using HexEditor.Util;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Widgets;

namespace HexEditor.View
{
	/// <summary>
	/// Options page for the hex editor. Built entirely in code (no .xaml) - the WPF markup
	/// compiler's temp "_wpftmp" build pass duplicated the generated .g.cs for this project's
	/// custom OutputPath/obj layout, so this sidesteps that by not using x:Class/Page at all,
	/// same approach already used for <see cref="HexEditContainer"/> and
	/// <see cref="FontChooserDialog"/>.
	/// </summary>
	public class HexEditOptionsPanel : OptionPanel
	{
		readonly CheckBox sizeToFitBox;
		readonly NumericUpDown bytesPerLine;
		readonly ComboBox viewModes;
		readonly ColorPickerButton offsetColorPicker;
		readonly ColorPickerButton dataColorPicker;
		readonly Button offsetFontButton;
		readonly Button dataFontButton;
		readonly TextBlock offsetPreview;
		readonly TextBlock dataPreview;

		FontSettings offsetFont, dataFont;

		public HexEditOptionsPanel()
		{
			var grid = new Grid();
			for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			sizeToFitBox = new CheckBox {
				Content = StringParser.Parse("${res:AddIns.HexEditor.SizeToFit}"),
				Margin = new Thickness(0, 5, 0, 0),
			};
			Grid.SetColumn(sizeToFitBox, 0);
			Grid.SetRow(sizeToFitBox, 0);
			Grid.SetColumnSpan(sizeToFitBox, 2);
			grid.Children.Add(sizeToFitBox);

			var numeralSystemLabel = new Label {
				Content = StringParser.Parse("${res:AddIns.HexEditor.NumeralSystem}:"),
				Margin = new Thickness(0, 5, 0, 0),
			};
			Grid.SetColumn(numeralSystemLabel, 0);
			Grid.SetRow(numeralSystemLabel, 1);
			grid.Children.Add(numeralSystemLabel);

			viewModes = new ComboBox { Margin = new Thickness(0, 5, 0, 0), DisplayMemberPath = "Text", SelectedValuePath = "Value" };
			viewModes.ItemsSource = new[] {
				new { Value = ViewMode.Hexadecimal, Text = StringParser.Parse("${res:AddIns.HexEditor.NumeralSystem.Hexadecimal}") },
				new { Value = ViewMode.Octal, Text = StringParser.Parse("${res:AddIns.HexEditor.NumeralSystem.Octal}") },
				new { Value = ViewMode.Decimal, Text = StringParser.Parse("${res:AddIns.HexEditor.NumeralSystem.Decimal}") },
			};
			Grid.SetColumn(viewModes, 1);
			Grid.SetRow(viewModes, 1);
			Grid.SetColumnSpan(viewModes, 2);
			grid.Children.Add(viewModes);

			var bytesPerLineLabel = new Label {
				Content = StringParser.Parse("${res:AddIns.HexEditor.DefaultBytesPerLine}:"),
				Margin = new Thickness(0, 5, 0, 0),
			};
			Grid.SetColumn(bytesPerLineLabel, 0);
			Grid.SetRow(bytesPerLineLabel, 2);
			grid.Children.Add(bytesPerLineLabel);

			bytesPerLine = new NumericUpDown { Minimum = 1, Margin = new Thickness(0, 5, 0, 0) };
			Grid.SetColumn(bytesPerLine, 1);
			Grid.SetRow(bytesPerLine, 2);
			Grid.SetColumnSpan(bytesPerLine, 2);
			grid.Children.Add(bytesPerLine);

			var offsetLabel = new Label {
				Content = StringParser.Parse("${res:AddIns.HexEditor.Display.Elements.Offset}:"),
				Margin = new Thickness(0, 5, 0, 0),
			};
			Grid.SetColumn(offsetLabel, 0);
			Grid.SetRow(offsetLabel, 3);
			grid.Children.Add(offsetLabel);

			offsetColorPicker = new ColorPickerButton { Margin = new Thickness(0, 5, 0, 0) };
			Grid.SetColumn(offsetColorPicker, 1);
			Grid.SetRow(offsetColorPicker, 3);
			grid.Children.Add(offsetColorPicker);

			offsetFontButton = new Button { Margin = new Thickness(5, 5, 0, 0) };
			offsetFontButton.Click += FontChooserClick;
			Grid.SetColumn(offsetFontButton, 2);
			Grid.SetRow(offsetFontButton, 3);
			grid.Children.Add(offsetFontButton);

			var dataLabel = new Label {
				Content = StringParser.Parse("${res:AddIns.HexEditor.Display.Elements.Data}:"),
				Margin = new Thickness(0, 5, 0, 0),
			};
			Grid.SetColumn(dataLabel, 0);
			Grid.SetRow(dataLabel, 4);
			grid.Children.Add(dataLabel);

			dataColorPicker = new ColorPickerButton { Margin = new Thickness(0, 5, 0, 0) };
			Grid.SetColumn(dataColorPicker, 1);
			Grid.SetRow(dataColorPicker, 4);
			grid.Children.Add(dataColorPicker);

			dataFontButton = new Button { Margin = new Thickness(5, 5, 0, 0) };
			dataFontButton.Click += FontChooserClick;
			Grid.SetColumn(dataFontButton, 2);
			Grid.SetRow(dataFontButton, 4);
			grid.Children.Add(dataFontButton);

			var previewGroup = new GroupBox { Header = StringParser.Parse("${res:Global.Preview}") };
			var previewPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
			offsetPreview = new TextBlock { Text = StringParser.Parse("${res:AddIns.HexEditor.Display.Elements.Offset}") };
			dataPreview = new TextBlock { Text = StringParser.Parse("${res:AddIns.HexEditor.Display.Elements.Data}") };
			previewPanel.Children.Add(offsetPreview);
			previewPanel.Children.Add(dataPreview);
			previewGroup.Content = previewPanel;
			Grid.SetColumn(previewGroup, 0);
			Grid.SetRow(previewGroup, 5);
			Grid.SetColumnSpan(previewGroup, 3);
			grid.Children.Add(previewGroup);

			var displayGroup = new GroupBox { Header = StringParser.Parse("${res:AddIns.HexEditor.Display}"), Content = grid };
			Content = new StackPanel { Children = { displayGroup } };

			DependencyPropertyDescriptor.FromProperty(ColorPickerButton.ValueProperty, typeof(ColorPickerButton))
				.AddValueChanged(offsetColorPicker, (s, e) => offsetPreview.Foreground = new SolidColorBrush(offsetColorPicker.Value));
			DependencyPropertyDescriptor.FromProperty(ColorPickerButton.ValueProperty, typeof(ColorPickerButton))
				.AddValueChanged(dataColorPicker, (s, e) => dataPreview.Foreground = new SolidColorBrush(dataColorPicker.Value));
		}

		public override void LoadOptions()
		{
			base.LoadOptions();
			sizeToFitBox.IsChecked = Settings.FitToWidth;
			viewModes.SelectedValue = Settings.ViewMode;
			bytesPerLine.Value = Settings.BytesPerLine;
			offsetColorPicker.Value = Settings.OffsetForeColor;
			dataColorPicker.Value = Settings.DataForeColor;
			offsetFontButton.Content = offsetFont = Settings.OffsetFont;
			dataFontButton.Content = dataFont = Settings.DataFont;
			offsetPreview.Foreground = new SolidColorBrush(Settings.OffsetForeColor);
			dataPreview.Foreground = new SolidColorBrush(Settings.DataForeColor);
			SetPreview(offsetPreview, offsetFont);
			SetPreview(dataPreview, dataFont);
		}

		public override bool SaveOptions()
		{
			Settings.FitToWidth = sizeToFitBox.IsChecked == true;
			if (viewModes.SelectedValue is ViewMode viewMode) Settings.ViewMode = viewMode;
			Settings.BytesPerLine = (int)bytesPerLine.Value;
			Settings.OffsetForeColor = offsetColorPicker.Value;
			Settings.DataForeColor = dataColorPicker.Value;
			Settings.OffsetFont = offsetFont;
			Settings.DataFont = dataFont;
			return base.SaveOptions();
		}

		void FontChooserClick(object sender, RoutedEventArgs e)
		{
			var current = sender == offsetFontButton ? offsetFont : dataFont;
			var chooser = new FontChooserDialog(current) { Owner = Window.GetWindow(this) };
			if (chooser.ShowDialog() != true)
				return;
			if (sender == offsetFontButton) {
				offsetFont = chooser.SelectedFont;
				SetPreview(offsetPreview, offsetFont);
				offsetFontButton.Content = offsetFont;
			}
			if (sender == dataFontButton) {
				dataFont = chooser.SelectedFont;
				SetPreview(dataPreview, dataFont);
				dataFontButton.Content = dataFont;
			}
		}

		void SetPreview(TextBlock preview, FontSettings font)
		{
			preview.FontFamily = new FontFamily(font.FamilyName);
			preview.FontSize = font.Size;
			preview.FontStyle = font.Italic ? FontStyles.Italic : FontStyles.Normal;
			preview.FontWeight = font.Bold ? FontWeights.Bold : FontWeights.Normal;
			preview.TextDecorations = font.Underline ? TextDecorations.Underline : null;
		}
	}
}
