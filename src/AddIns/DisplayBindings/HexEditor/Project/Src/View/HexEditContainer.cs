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
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using HexEditor.Util;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Workbench;

namespace HexEditor.View
{
	/// <summary>
	/// Hosts the <see cref="Editor"/> control plus its toolbar (fit-to-window toggle, bytes-per-line,
	/// view-mode selector, load progress). Built entirely in code (no XAML) like the original's
	/// designer-generated layout, since a WPF ToolBar/Grid is simple enough not to need a
	/// dedicated .xaml file.
	/// </summary>
	public class HexEditContainer : Grid
	{
		internal readonly Editor hexEditControl;

		readonly ToggleButton tbSizeToFit;
		readonly NumericUpDown tSTBCharsPerLine;
		readonly ComboBox tCBViewMode;
		readonly ProgressBar toolStripProgressBar1;

		public bool HasSomethingSelected {
			get { return hexEditControl.HasSomethingSelected; }
		}

		public bool CanUndo {
			get { return hexEditControl.CanUndo; }
		}

		public bool CanRedo {
			get { return hexEditControl.CanRedo; }
		}

		public HexEditContainer()
		{
			RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			var toolbar = new ToolBar();

			tbSizeToFit = new ToggleButton {
				Content = StringParser.Parse("${res:AddIns.HexEditor.SizeToFit}"),
				Margin = new Thickness(2),
			};
			tbSizeToFit.Click += TbSizeToFitClick;
			toolbar.Items.Add(tbSizeToFit);

			toolbar.Items.Add(new Separator());

			tSTBCharsPerLine = new NumericUpDown { Minimum = 1, Width = 50, Margin = new Thickness(2) };
			DependencyPropertyDescriptor.FromProperty(NumericUpDown.ValueProperty, typeof(NumericUpDown))
				.AddValueChanged(tSTBCharsPerLine, TSTBCharsPerLineValueChanged);
			toolbar.Items.Add(tSTBCharsPerLine);

			tCBViewMode = new ComboBox { Width = 120, Margin = new Thickness(2) };
			tCBViewMode.Items.Add("Hexadecimal");
			tCBViewMode.Items.Add("Octal");
			tCBViewMode.Items.Add("Decimal");
			tCBViewMode.SelectionChanged += TCBViewModeSelectedIndexChanged;
			toolbar.Items.Add(tCBViewMode);

			toolStripProgressBar1 = new ProgressBar { Width = 150, Margin = new Thickness(6, 2, 2, 2), Visibility = Visibility.Collapsed };
			toolbar.Items.Add(toolStripProgressBar1);

			SetRow(toolbar, 0);
			Children.Add(toolbar);

			hexEditControl = new Editor {
				BytesPerLine = 16,
				FitToWindowWidth = false,
			};
			hexEditControl.ProgressChanged += percentage => Dispatcher.Invoke(() => {
				toolStripProgressBar1.Value = percentage;
				toolStripProgressBar1.Visibility = percentage >= 100 ? Visibility.Collapsed : Visibility.Visible;
			});
			SetRow(hexEditControl, 1);
			Children.Add(hexEditControl);

			GotFocus += (s, e) => hexEditControl.Focus();

			Init();
		}

		bool loaded = false;

		void Init()
		{
			try {
				hexEditControl.Initializing = true;
				if (loaded) return;
				loaded = true;

				hexEditControl.BytesPerLine = Settings.BytesPerLine;
				tSTBCharsPerLine.Value = hexEditControl.BytesPerLine;
				hexEditControl.ContextMenu = MenuService.CreateContextMenu(hexEditControl, "/AddIns/HexEditor/Editor/ContextMenu");
				tCBViewMode.SelectedIndex = (int)Settings.ViewMode;
				hexEditControl.ViewMode = Settings.ViewMode;
				tbSizeToFit.IsChecked = hexEditControl.FitToWindowWidth = Settings.FitToWidth;
				tSTBCharsPerLine.IsEnabled = !Settings.FitToWidth;

				hexEditControl.InvalidateVisual();
			} finally {
				hexEditControl.Initializing = false;
			}
		}

		void TbSizeToFitClick(object sender, RoutedEventArgs e)
		{
			tSTBCharsPerLine.IsEnabled = tbSizeToFit.IsChecked != true;
			hexEditControl.FitToWindowWidth = tbSizeToFit.IsChecked == true;
			tSTBCharsPerLine.Value = hexEditControl.BytesPerLine;
		}

		void TCBViewModeSelectedIndexChanged(object sender, SelectionChangedEventArgs e)
		{
			switch (tCBViewMode.SelectedIndex) {
				case 0:
					hexEditControl.ViewMode = ViewMode.Hexadecimal;
					break;
				case 1:
					hexEditControl.ViewMode = ViewMode.Octal;
					break;
				case 2:
					hexEditControl.ViewMode = ViewMode.Decimal;
					break;
			}
		}

		void TSTBCharsPerLineValueChanged(object sender, EventArgs e)
		{
			int value = (int)tSTBCharsPerLine.Value;
			if (value > 0) {
				hexEditControl.BytesPerLine = value;
				if (hexEditControl.BytesPerLine != value)
					tSTBCharsPerLine.Value = hexEditControl.BytesPerLine;
			}
		}

		public void LoadFile(OpenedFile file, Stream stream)
		{
			hexEditControl.LoadFile(file, stream);
			hexEditControl.InvalidateVisual();
		}

		public void SaveFile(OpenedFile file, Stream stream)
		{
			hexEditControl.SaveFile(file, stream);
			hexEditControl.InvalidateVisual();
		}

		public string Cut()
		{
			string text = hexEditControl.Copy();
			hexEditControl.Delete();
			return text;
		}

		public string Copy()
		{
			return hexEditControl.Copy();
		}

		public void Paste(string text)
		{
			hexEditControl.Paste(text);
		}

		public void Delete()
		{
			hexEditControl.Delete();
		}

		public void SelectAll()
		{
			hexEditControl.SelectAll();
		}

		public void Undo()
		{
			hexEditControl.Undo();
		}

		public void Redo()
		{
			hexEditControl.Redo();
		}
	}
}
