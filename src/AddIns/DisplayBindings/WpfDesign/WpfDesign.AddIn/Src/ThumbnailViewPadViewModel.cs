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
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="ThumbnailViewPad"/> (AddInTree pad id
	/// "ThumbnailViewPad"). Not a MEF part - the AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> - so it is constructed with a plain <c>new</c> by the
	/// <see cref="ThumbnailViewPad"/> shim on first real use and registered with the real docking
	/// host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	sealed class ThumbnailViewPadViewModel : ToolPaneModel
	{
		readonly ContentPresenter contentControl = new ContentPresenter();
		readonly TextBlock notAvailableTextBlock = new TextBlock {
			Text = StringParser.Parse("${res:ICSharpCode.SharpDevelop.Gui.OutlinePad.NotAvailable}"),
			TextWrapping = TextWrapping.Wrap
		};

		public ThumbnailViewPadViewModel()
		{
			Title = "Thumbnail";
			ContentId = "ThumbnailViewPad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Right, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(ThumbnailViewPad).FullName;
			PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Right;
			Content = contentControl;

			SD.Workbench.ActiveViewContentChanged += WorkbenchActiveViewContentChanged;
			WorkbenchActiveViewContentChanged(null, null);
		}

		void WorkbenchActiveViewContentChanged(object sender, EventArgs e)
		{
			// WpfViewContent moved to the out-of-process design host (doc/technotes/
			// wpf-designer.md): there is no live in-process DesignSurface visual tree for
			// ThumbnailView to render a minimap of anymore - only a decoded frame bitmap. A real
			// thumbnail for the new surface is a separate, later feature (it would need its own
			// small preview, not ThumbnailView's live-visual-tree approach); until then this pad
			// shows the same "not available" placeholder it already shows for every other
			// document type rather than pretending to support WPF.
			contentControl.Content = notAvailableTextBlock;
		}
	}
}
