using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ICSharpCode.IconEditor
{
	public partial class IconPanel : UserControl
	{
		Size iconSize;
		int colorDepth;
		IconEntry entry;
		BitmapSource bitmap;

		public Size IconSize
		{
			get { return iconSize; }
		}

		public int ColorDepth
		{
			get { return colorDepth; }
		}

		public IconEntry Entry
		{
			get { return entry; }
			set
			{
				bitmap = null;
				entry = value;
				if (entry != null)
				{
					bitmap = entry.ExportArgbBitmap();
				}
				UpdateSize();
				InvalidateVisual();
			}
		}

		public event EventHandler EntryChanged = delegate { };

		public IconPanel(Size size, int colorDepth)
		{
			this.iconSize = size;
			this.colorDepth = colorDepth;
			InitializeComponent();
			DragDrop.AddDragEnterHandler(this, IconPanelDragEnter);
			DragDrop.AddDropHandler(this, IconPanelDragDrop);
			UpdateSize();
		}

		void UpdateSize()
		{
			Width = LimitSizeIfNoEntry((int)iconSize.Width) + 2;
			Height = LimitSizeIfNoEntry((int)iconSize.Height) + 2;
		}

		int LimitSizeIfNoEntry(int size)
		{
			if (entry != null)
				return size;
			else
				return Math.Min(size, 32);
		}

		protected override void OnRender(DrawingContext dc)
		{
			base.OnRender(dc);
			int drawOffset = 1;
			if (bitmap != null)
			{
				dc.DrawImage(bitmap, new Rect(drawOffset, drawOffset, iconSize.Width, iconSize.Height));
			}
		}

		void IconPanelContextMenuOpening(object sender, ContextMenuEventArgs e)
		{
			if (ContextMenu == null)
				return;

			bool hasEntry = entry != null;
			SetMenuItemVisibility(ContextMenu.Items[0] as UIElement, hasEntry);
			SetMenuItemVisibility(ContextMenu.Items[1] as UIElement, hasEntry);
			SetMenuItemVisibility(ContextMenu.Items[2] as UIElement, hasEntry);
			var compressedItem = ContextMenu.Items[2] as MenuItem;
			if (compressedItem != null)
			{
				compressedItem.IsChecked = entry != null && entry.IsCompressed;
				compressedItem.IsEnabled = entry == null || entry.IsCompressed || entry.ColorDepth == 32;
			}
			SetMenuItemVisibility(ContextMenu.Items[3] as UIElement, hasEntry);
			SetMenuItemVisibility(ContextMenu.Items[4] as UIElement, hasEntry);
			SetMenuItemVisibility(ContextMenu.Items[5] as UIElement, hasEntry && entry != null && entry.Type == IconEntryType.Classic);
			SetMenuItemVisibility(ContextMenu.Items[6] as UIElement, hasEntry && entry != null && entry.Type == IconEntryType.Classic);
		}

		void SetMenuItemVisibility(UIElement element, bool visible)
		{
			if (element != null)
				element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}

		void ExportToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			if (entry == null) return;
			var dlg = new Microsoft.Win32.SaveFileDialog
			{
				DefaultExt = "png",
				Filter = "PNG images|*.png|All files|*.*"
			};
			if (dlg.ShowDialog() == true)
			{
				using (var stream = dlg.OpenFile())
				{
					PngBitmapEncoder encoder = new PngBitmapEncoder();
					encoder.Frames.Add(BitmapFrame.Create(entry.ExportArgbBitmap()));
					encoder.Save(stream);
				}
			}
		}

		void ExportANDMaskToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			if (entry == null) return;
			var dlg = new Microsoft.Win32.SaveFileDialog
			{
				DefaultExt = "bmp",
				Filter = "BMP images|*.bmp|All files|*.*"
			};
			if (dlg.ShowDialog() == true)
			{
				using (var stream = dlg.OpenFile())
				{
					entry.GetMaskImageData().CopyTo(stream);
				}
			}
		}

		void ExportXORMaskToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			if (entry == null) return;
			var dlg = new Microsoft.Win32.SaveFileDialog
			{
				DefaultExt = "bmp",
				Filter = "BMP images|*.bmp|All files|*.*"
			};
			if (dlg.ShowDialog() == true)
			{
				using (var stream = dlg.OpenFile())
				{
					entry.GetImageData().CopyTo(stream);
				}
			}
		}

		void DeleteToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			this.Entry = null;
			EntryChanged(this, e);
		}

		void ReplaceWithImageToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			var dlg = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "Images|*.png;*.bmp;*.gif;*.jpg|All files|*.*"
			};
			if (dlg.ShowDialog() == true)
			{
				try
				{
					BitmapSource newBitmap = BitmapFrame.Create(new Uri(dlg.FileName));
					SetImage(newBitmap);
				}
				catch (Exception ex)
				{
					ICSharpCode.Core.MessageService.ShowHandledException(ex);
				}
			}
		}

		void CompressedToolStripMenuItemClick(object sender, RoutedEventArgs e)
		{
			this.Entry = new IconEntry((int)iconSize.Width, (int)iconSize.Height, colorDepth, entry.ExportArgbBitmap(), !entry.IsCompressed);
			EntryChanged(this, EventArgs.Empty);
		}

		void SetImage(BitmapSource newBitmap)
		{
			if ((int)iconSize.Width != newBitmap.PixelWidth || (int)iconSize.Height != newBitmap.PixelHeight)
			{
				int r = ICSharpCode.Core.MessageService.ShowCustomDialog(
					"Import Image",
					string.Format("The image has size {0}x{1}, but size {2}x{3} is expected.",
						newBitmap.PixelWidth, newBitmap.PixelHeight, iconSize.Width, iconSize.Height),
					0, 1, "Convert", "Cancel");
				if (r != 0)
				{
					return;
				}
			}
			bool? compress = entry != null ? entry.IsCompressed : (bool?)null;
			this.Entry = new IconEntry((int)iconSize.Width, (int)iconSize.Height, colorDepth, newBitmap, compress);
			EntryChanged(this, EventArgs.Empty);
		}

		Point mouseDownLocation;

		void IconPanelMouseDown(object sender, MouseButtonEventArgs e)
		{
			if (e.ChangedButton == MouseButton.Left)
			{
				mouseDownLocation = e.GetPosition(this);
			}
		}

		void IconPanelMouseMove(object sender, MouseEventArgs e)
		{
			if (e.LeftButton == MouseButtonState.Pressed && entry != null)
			{
				Point pos = e.GetPosition(this);
				if (!mouseDownLocation.Equals(default(Point)))
				{
					double dx = Math.Abs(pos.X - mouseDownLocation.X);
					double dy = Math.Abs(pos.Y - mouseDownLocation.Y);
					if (dx > SystemParameters.MinimumHorizontalDragDistance ||
						dy > SystemParameters.MinimumVerticalDragDistance)
					{
						mouseDownLocation = default(Point);
						DragDrop.DoDragDrop(this, entry.ExportArgbBitmap(), DragDropEffects.Copy);
					}
				}
			}
		}

		void IconPanelDragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.Bitmap))
			{
				e.Effects = DragDropEffects.Copy;
			}
			else if (e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				e.Effects = DragDropEffects.None;
				string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
				if (files != null && files.Length == 1)
				{
					string ext = Path.GetExtension(files[0]);
					if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
						ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
						ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
						ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
					{
						e.Effects = DragDropEffects.Copy;
					}
				}
			}
			else
			{
				e.Effects = DragDropEffects.None;
			}
			e.Handled = true;
		}

		void IconPanelDragDrop(object sender, DragEventArgs e)
		{
			try
			{
				BitmapSource bmp = null;
				if (e.Data.GetDataPresent(DataFormats.Bitmap))
				{
					bmp = e.Data.GetData(DataFormats.Bitmap) as BitmapSource;
				}
				else if (e.Data.GetDataPresent(DataFormats.FileDrop))
				{
					string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
					if (files != null && files.Length == 1)
					{
						bmp = BitmapFrame.Create(new Uri(files[0]));
					}
				}
				if (bmp != null)
				{
					SetImage(bmp);
				}
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.MessageService.ShowHandledException(ex);
			}
			e.Handled = true;
		}
	}
}
