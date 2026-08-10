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
using System.Drawing;
using System.Drawing.Printing;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.Core.WinForms;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.WinForms
{
	/// <summary>
	/// Allows printing using the IPrintable interface.
	/// </summary>
	sealed class WinFormsService : IWinFormsService
	{
		// IPrintable.PrintDocument is typed against the real netcore System.Drawing.Common (matching
		// ICSharpCode.SharpDevelop.csproj, where IPrintable/IWinFormsService are declared), but
		// PrintDialog/PrintPreviewDialog.Document here comes from LibreWinForms' merged assembly and
		// wants the unrelated ProGPU.System.Drawing.Common PrintDocument instead - the two aren't
		// convertible. No IPrintable implementer exists yet in this fork (grep the tree), so rather
		// than invent an unverified cross-implementation GDI+ print bridge, this is out of MVP scope
		// for now, same call as IResourceService.GetBitmap/GetIcon's "MVP mock" in
		// ResourceServiceWinFormsExtensions.cs.
		public void Print(IPrintable printable)
		{
			MessageService.ShowError("${res:ICSharpCode.SharpDevelop.Commands.Print.CreatePrintDocumentError}");
		}

		public void PrintPreview(IPrintable printable)
		{
			MessageService.ShowError("${res:ICSharpCode.SharpDevelop.Commands.Print.CreatePrintDocumentError}");
		}
		
		public IWinFormsToolbarService ToolbarService {
			get {
				return SD.GetRequiredService<IWinFormsToolbarService>();
			}
		}
		
		public IWinFormsMenuService MenuService {
			get {
				return SD.GetRequiredService<IWinFormsMenuService>();
			}
		}
		
		public Font DefaultMonospacedFont {
			get {
				return LoadDefaultMonospacedFont(FontStyle.Regular);
			}
		}
		
		public IWin32Window MainWin32Window {
			get {
				// WpfWorkbench never implemented IWin32Window - on the real .NET Framework/WPF-on-
				// Windows stack this used to go through an HWND interop adapter (WindowInteropHelper),
				// but LibreWPF has no real Win32 HWND to hand out on this platform at all (it's a
				// from-scratch WebGPU compositor, not hosted in a Win32 window). Callers only use
				// this to parent PrintDialog/PrintPreviewDialog - null just means those come up
				// unparented instead of centered over the main window.
				return null;
			}
		}
		
		// WinFormsResourceService (Core.WinForms) itself is compiled against LibreWinForms'
		// ProGPU-backed System.Drawing.Common (needed to interoperate with the real
		// System.Windows.Forms.Control API it works with there), but IWinFormsService - and this
		// class's implementation of it - is compiled against the real netcore System.Drawing.Common
		// instead, to match IconService.GetBitmap and friends elsewhere in ICSharpCode.SharpDevelop.
		// The two Font/Bitmap/Icon families are unrelated types with no conversion between them, so:
		// - Font is just constructed directly by name/size/style, which works identically either way.
		// - Bitmap/Icon resource lookups can't be bridged without real GDI+ interop, which this
		//   fork doesn't need for a WPF-first workbench - same "MVP mock, return null" call already
		//   made for IResourceService.GetBitmap/GetIcon (see ResourceServiceWinFormsExtensions.cs).
		public Font LoadDefaultMonospacedFont(FontStyle style)
		{
			return LoadFont("Courier New", 10, style);
		}

		public Font LoadFont(Font baseFont, FontStyle newStyle)
		{
			return LoadFont(baseFont.Name, (int)baseFont.Size, newStyle);
		}

		public Font LoadFont(string fontName, int size, FontStyle style)
		{
			try {
				return new Font(fontName, size, style);
			} catch (Exception ex) {
				LoggingService.Warn(ex);
				return SystemFonts.MenuFont;
			}
		}

		public Bitmap GetResourceServiceBitmap(string resourceName)
		{
			return null;
		}

		public Icon GetResourceServiceIcon(string resourceName)
		{
			return null;
		}

		public Icon BitmapToIcon(Bitmap bitmap)
		{
			return null;
		}
		
		public void ApplyRightToLeftConverter(Control control, bool recurse)
		{
			if (recurse)
				RightToLeftConverter.ConvertRecursive(control);
			else
				RightToLeftConverter.Convert(control);
		}
		
		public void SetContent(System.Windows.Controls.ContentControl contentControl, object content, IServiceProvider serviceProvider)
		{
			if (contentControl == null)
				throw new ArgumentNullException("contentControl");
			// serviceObject = object implementing the old clipboard/undo interfaces
			// to allow WinForms AddIns to handle WPF commands
			
			var host = contentControl.Content as SDWindowsFormsHost;
			if (host != null) {
				if (host.Child == content) {
					host.ServiceProvider = serviceProvider;
					return;
				}
				host.Dispose();
			}
			if (content is System.Windows.Forms.Control) {
				contentControl.Content = new SDWindowsFormsHost {
					Child = (System.Windows.Forms.Control)content,
					ServiceProvider = serviceProvider,
					DisposeChild = false
				};
			} else if (content is string) {
				contentControl.Content = new System.Windows.Controls.TextBlock {
					Text = content.ToString(),
					TextWrapping = System.Windows.TextWrapping.Wrap
				};
			} else {
				contentControl.Content = content;
			}
		}
		
		public void SetContent(System.Windows.Controls.ContentPresenter contentControl, object content, IServiceProvider serviceProvider)
		{
			if (contentControl == null)
				throw new ArgumentNullException("contentControl");
			// serviceObject = object implementing the old clipboard/undo interfaces
			// to allow WinForms AddIns to handle WPF commands
			
			var host = contentControl.Content as SDWindowsFormsHost;
			if (host != null) {
				if (host.Child == content) {
					host.ServiceProvider = serviceProvider;
					return;
				}
				host.Dispose();
			}
			if (content is System.Windows.Forms.Control) {
				contentControl.Content = new SDWindowsFormsHost {
					Child = (System.Windows.Forms.Control)content,
					ServiceProvider = serviceProvider,
					DisposeChild = false
				};
			} else if (content is string) {
				contentControl.Content = new System.Windows.Controls.TextBlock {
					Text = content.ToString(),
					TextWrapping = System.Windows.TextWrapping.Wrap
				};
			} else {
				contentControl.Content = content;
			}
		}
		
		public CustomWindowsFormsHost CreateWindowsFormsHost(IServiceProvider serviceProvider = null, bool processShortcutsInWPF = false)
		{
			return new SDWindowsFormsHost(processShortcutsInWPF) {
				ServiceProvider = serviceProvider,
				DisposeChild = false
			};
		}

		public void InvalidateCommands()
		{
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
		}
	}
}
