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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.Core;
using ColorCanvas = Xceed.Wpf.Toolkit.ColorCanvas;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	/// <summary>
	/// Edit > Insert > Color: picks a color and inserts it at the caret as source text - a
	/// Color.X / Color.FromArgb(...) expression in .cs/.vb/.boo files, a name or #RRGGBB otherwise.
	/// Rewritten on WPF (Xceed ColorCanvas) in place of the WinForms SharpDevelopColorDialog.
	/// </summary>
	public class ShowColorDialog : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor textEditor = SD.GetActiveViewContentService<ITextEditor>();
			if (textEditor == null)
				return;
			
			Color? picked = PickColor();
			if (picked == null)
				return;
			
			Color color = picked.Value;
			string knownName = FindKnownColorName(color);
			string ext = Path.GetExtension(textEditor.FileName).ToLowerInvariant();
			string colorstr;
			if (ext == ".cs" || ext == ".vb" || ext == ".boo") {
				if (knownName != null) {
					colorstr = "Color." + knownName;
				} else if (color.A < 255) {
					colorstr = string.Format("Color.FromArgb({0}, {1}, {2}, {3})", color.A, color.R, color.G, color.B);
				} else {
					colorstr = string.Format("Color.FromArgb({0}, {1}, {2})", color.R, color.G, color.B);
				}
			} else {
				if (knownName != null) {
					colorstr = knownName;
				} else if (color.A < 255) {
					colorstr = string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
				} else {
					colorstr = string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
				}
			}
			
			textEditor.SelectedText = colorstr;
			textEditor.Select(textEditor.SelectionStart + textEditor.SelectionLength, 0);
		}
		
		static Color? PickColor()
		{
			var canvas = new ColorCanvas { SelectedColor = Colors.White, Margin = new Thickness(0, 0, 0, 8) };
			var ok = new Button { Content = "Insert", IsDefault = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
			var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			buttons.Children.Add(ok);
			buttons.Children.Add(cancel);
			var root = new DockPanel { Margin = new Thickness(12) };
			DockPanel.SetDock(buttons, Dock.Bottom);
			root.Children.Add(buttons);
			root.Children.Add(canvas);
			
			var window = new Window {
				Title = "Insert Color",
				Content = root,
				SizeToContent = SizeToContent.WidthAndHeight,
				ResizeMode = ResizeMode.NoResize,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				ShowInTaskbar = false,
				Owner = Application.Current?.MainWindow
			};
			// Posted rather than set inline: on the portable WPF backend a Click handler runs inside the
			// render loop, where closing the window throws (see WpfMessageService.CloseDialog).
			Action<bool> close = result => window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => {
				if (window.IsVisible)
					window.DialogResult = result;
			}));
			ok.Click += delegate { close(true); };
			cancel.Click += delegate { close(false); };
			return window.ShowDialog() == true ? canvas.SelectedColor : null;
		}
		
		static string FindKnownColorName(Color color)
		{
			return typeof(Colors).GetProperties(BindingFlags.Public | BindingFlags.Static)
				.Where(p => p.PropertyType == typeof(Color) && (Color)p.GetValue(null) == color)
				.Select(p => p.Name)
				.FirstOrDefault();
		}
	}
}
