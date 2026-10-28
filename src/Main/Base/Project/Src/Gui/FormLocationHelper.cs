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
using System.Windows;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Static helper class that loads and stores the position and size of a Window in the
	/// PropertyService.
	/// </summary>
	/// <remarks>
	/// The WinForms Form overload and the original multi-monitor-aware Screen.* validation
	/// (System.Windows.Forms.Screen) were removed per the WinForms strip policy; validation here
	/// is simplified to clamp against the primary screen's work area only (SystemParameters.WorkArea).
	/// </remarks>
	public static class FormLocationHelper
	{
		public static void ApplyWindow(Window window, string propertyName, bool isResizable)
		{
			window.WindowStartupLocation = WindowStartupLocation.Manual;
			if (isResizable) {
				window.SourceInitialized += delegate {
					var ownerLocation = GetOwnerLocation(window);
					Rect bounds = PropertyService.Get(propertyName, GetDefaultBounds(window));
					bounds.Offset(ownerLocation.X, ownerLocation.Y);
					bounds = Validate(bounds);
					window.Left = bounds.X;
					window.Top = bounds.Y;
					window.Width = bounds.Width;
					window.Height = bounds.Height;
				};
			} else {
				window.SourceInitialized += delegate {
					var ownerLocation = GetOwnerLocation(window);
					Size size = new Size(window.ActualWidth, window.ActualHeight);
					Point location = PropertyService.Get(propertyName, GetDefaultLocation(window));
					location.Offset(ownerLocation.X, ownerLocation.Y);
					var bounds = Validate(new Rect(location, size));
					window.Left = bounds.X;
					window.Top = bounds.Y;
				};
			}
			window.Closing += delegate {
				var relativeToOwner = GetLocationRelativeToOwner(window);
				if (isResizable) {
					if (window.WindowState == System.Windows.WindowState.Normal) {
						PropertyService.Set(propertyName, new Rect(relativeToOwner, new Size(window.ActualWidth, window.ActualHeight)));
					}
				} else {
					PropertyService.Set(propertyName, relativeToOwner);
				}
			};
		}

		static Point GetLocationRelativeToOwner(Window window)
		{
			Point ownerLocation = GetOwnerLocation(window);
			return new Point(window.Left - ownerLocation.X, window.Top - ownerLocation.Y);
		}

		static Point GetOwnerLocation(Window window)
		{
			var owner = window.Owner ?? SD.Workbench.MainWindow;
			if (owner == null)
				return new Point(0, 0);
			return new Point(owner.Left, owner.Top);
		}

		/// <summary>
		/// Clamps the given bounds to the primary screen's work area (single-monitor only in this MVP build).
		/// </summary>
		public static Rect Validate(Rect bounds)
			=> Validate(bounds, SystemParameters.WorkArea);

		/// <summary>
		/// <see cref="Validate(Rect)"/> against an explicit work area. Reading <see cref="SystemParameters.WorkArea"/>
		/// initialises the windowing platform, which GLFW only allows on the main thread (on macOS it blocks any other
		/// thread forever), so the clamping itself is kept free of it and can be exercised from any thread.
		/// </summary>
		public static Rect Validate(Rect bounds, Rect workArea)
		{
			if (double.IsNaN(bounds.Left) || double.IsInfinity(bounds.Left)
				|| double.IsNaN(bounds.Top) || double.IsInfinity(bounds.Top)
				|| double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width)
				|| double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height)
				|| bounds.Width <= 0 || bounds.Height <= 0) {
				bounds = new Rect(10, 10, 1024, 768);
			}
			if (bounds.Right > workArea.Left && bounds.Left < workArea.Right
				&& bounds.Bottom > workArea.Top && bounds.Top < workArea.Bottom) {
				// Fit the bounds into the work area: shrink what is larger than it, then slide the rest inside.
				// Leaving an overhanging window as it is lets some desktops reshape the native window behind the
				// framework's back (macOS moves a titled window below the menu bar and shrinks it to the visible
				// frame), after which the window's reported position and size no longer match what is on screen
				// and anything computed from them, such as an automation click point, is off.
				double width = Math.Min(bounds.Width, workArea.Width);
				double height = Math.Min(bounds.Height, workArea.Height);
				double left = Math.Max(workArea.Left, Math.Min(bounds.Left, workArea.Right - width));
				double top = Math.Max(workArea.Top, Math.Min(bounds.Top, workArea.Bottom - height));
				return new Rect(left, top, width, height);
			}
			// center on the primary screen's work area
			return new Rect(
				workArea.Left + (workArea.Width - bounds.Width) / 2,
				workArea.Top + (workArea.Height - bounds.Height) / 2,
				bounds.Width, bounds.Height);
		}

		static Rect GetDefaultBounds(Window window)
		{
			Size size = new Size(window.Width, window.Height);
			return new Rect(GetDefaultLocation(size), size);
		}

		static Point GetDefaultLocation(Window window)
		{
			Size size = new Size(window.Width, window.Height);
			return GetDefaultLocation(size);
		}

		static Point GetDefaultLocation(Size formSize)
		{
			var mainWindow = SD.Workbench.MainWindow;
			if (mainWindow == null) {
				var workArea = SystemParameters.WorkArea;
				return new Point(workArea.Left + (workArea.Width - formSize.Width) / 2,
				                 workArea.Top + (workArea.Height - formSize.Height) / 2);
			}
			Rect parent = new Rect(
				mainWindow.Left, mainWindow.Top, mainWindow.Width, mainWindow.Height
			);
			return new Point(parent.Left + (parent.Width - formSize.Width) / 2,
			                 parent.Top + (parent.Height - formSize.Height) / 2);
		}
	}
}
