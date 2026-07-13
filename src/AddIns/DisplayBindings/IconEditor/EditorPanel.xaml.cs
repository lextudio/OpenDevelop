using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ICSharpCode.IconEditor
{
	public partial class EditorPanel : UserControl
	{
		IList<Size> availableSizes;
		IList<int> availableColorDepths;
		IconPanel[,] iconPanels;
		IconFile activeIconFile;

		Brush[] backgroundColors = {
			SystemColors.ControlBrush,
			Brushes.Black,
			Brushes.White,
			Brushes.Teal,
			Brushes.DeepSkyBlue,
			Brushes.Red,
			Brushes.Magenta
		};

		public EditorPanel()
		{
			InitializeComponent();
			colorComboBox.ItemsSource = backgroundColors;
			colorComboBox.SelectedIndex = 0;
			ShowFile(new IconFile());
		}

		public void SaveIcon(string fileName)
		{
			activeIconFile.Save(fileName);
		}

		public void SaveIcon(System.IO.Stream stream)
		{
			activeIconFile.Save(stream);
		}

		public void ShowFile(IconFile f)
		{
			this.activeIconFile = f;
			table.Visibility = Visibility.Collapsed;
			table.Children.Clear();
			table.ColumnDefinitions.Clear();
			table.RowDefinitions.Clear();

			table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			TextBlock headerLabel = new TextBlock
			{
				Text = "Icon Editor",
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(3, 0, 3, 0)
			};
			Grid.SetRow(headerLabel, 0);
			Grid.SetColumn(headerLabel, 0);
			table.Children.Add(headerLabel);

			availableSizes = f.AvailableSizes.ToList();
			foreach (Size size in availableSizes)
			{
				table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				TextBlock lbl = new TextBlock
				{
					Text = (int)size.Width + "x" + (int)size.Height,
					HorizontalAlignment = HorizontalAlignment.Right,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(3, 3, 8, 3)
				};
				Grid.SetRow(lbl, table.RowDefinitions.Count - 1);
				Grid.SetColumn(lbl, 0);
				table.Children.Add(lbl);
			}

			availableColorDepths = f.AvailableColorDepths.ToList();
			foreach (int colorDepth in availableColorDepths)
			{
				table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				TextBlock lbl = new TextBlock
				{
					Text = colorDepth + " bit",
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Bottom,
					Margin = new Thickness(3, 0, 3, 3)
				};
				Grid.SetRow(lbl, 0);
				Grid.SetColumn(lbl, table.ColumnDefinitions.Count - 1);
				table.Children.Add(lbl);
			}

			iconPanels = new IconPanel[table.ColumnDefinitions.Count - 1, table.RowDefinitions.Count - 1];
			for (int column = 1; column < table.ColumnDefinitions.Count; column++)
			{
				for (int row = 1; row < table.RowDefinitions.Count; row++)
				{
					IconPanel panel = new IconPanel(availableSizes[row - 1], availableColorDepths[column - 1]);
					panel.Background = backgroundColors[colorComboBox.SelectedIndex];
					panel.Margin = new Thickness(1);
					panel.HorizontalAlignment = HorizontalAlignment.Center;
					panel.VerticalAlignment = VerticalAlignment.Center;
					panel.EntryChanged += EditorPanel_EntryChanged;
					iconPanels[column - 1, row - 1] = panel;
					Grid.SetRow(panel, row);
					Grid.SetColumn(panel, column);
					table.Children.Add(panel);
				}
			}

			foreach (IconEntry e in f.Icons)
			{
				int row = availableSizes.IndexOf(e.Size);
				int column = availableColorDepths.IndexOf(e.ColorDepth);
				if (row >= 0 && column >= 0)
				{
					iconPanels[column, row].Entry = e;
				}
			}

			table.Visibility = Visibility.Visible;
		}

		void EditorPanel_EntryChanged(object sender, EventArgs e)
		{
			var panel = (IconPanel)sender;
			if (panel.Entry == null)
			{
				activeIconFile.RemoveEntry((int)panel.IconSize.Width, (int)panel.IconSize.Height, panel.ColorDepth);
				ShowFile(activeIconFile);
			}
			else
			{
				activeIconFile.AddEntry(panel.Entry);
			}
			IconWasEdited?.Invoke(this, e);
		}

		public event EventHandler IconWasEdited;

		void ColorComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (iconPanels == null) return;
			for (int i = 0; i < iconPanels.GetLength(0); i++)
			{
				for (int j = 0; j < iconPanels.GetLength(1); j++)
				{
					iconPanels[i, j].Background = backgroundColors[colorComboBox.SelectedIndex];
				}
			}
		}

		void AddFormatButtonClick(object sender, RoutedEventArgs e)
		{
			if (activeIconFile == null)
				return;
			PickFormatDialog dlg = new PickFormatDialog();
			dlg.Owner = Window.GetWindow(this);
			if (dlg.ShowDialog() == true)
			{
				int width = dlg.IconWidth;
				int height = dlg.IconHeight;
				int colorDepth = dlg.ColorDepth;
				var sameSizeEntries = activeIconFile.Icons.Where(entry => entry.Width == width && entry.Height == height);
				if (sameSizeEntries.Any(entry => entry.ColorDepth == colorDepth))
				{
					ICSharpCode.Core.MessageService.ShowMessage("This icon already contains an image with the specified format.", "Icon Editor");
					return;
				}
				IconEntry sourceEntry = sameSizeEntries.OrderByDescending(entry => entry.ColorDepth).FirstOrDefault();
				if (sourceEntry == null)
				{
					sourceEntry = (from entry in activeIconFile.Icons
								   orderby entry.Width descending, entry.Height descending, entry.ColorDepth descending
								   select entry).FirstOrDefault();
				}
				System.Windows.Media.Imaging.BitmapSource sourceBitmap = sourceEntry != null
					? sourceEntry.ExportArgbBitmap()
					: new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96,
						System.Windows.Media.PixelFormats.Bgra32, null);
				activeIconFile.AddEntry(new IconEntry(width, height, colorDepth, sourceBitmap));
				IconWasEdited?.Invoke(this, e);
				ShowFile(activeIconFile);
			}
		}
	}
}
