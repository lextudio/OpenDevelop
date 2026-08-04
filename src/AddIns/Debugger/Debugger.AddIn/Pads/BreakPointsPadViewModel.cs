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

using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using Debugger.AddIn.Breakpoints;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-04) replacement for the
	/// legacy AddInTree-registered <see cref="BreakPointsPad"/> (AddInTree pad id
	/// "BreakPointsPad"). Not a MEF part - Debugger.AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> (it only scans the App project's own assembly), and this AddIn
	/// correctly only references the Base project, not the App project - so this is constructed
	/// with a plain <c>new</c> by the <see cref="BreakPointsPad"/> shim on first real use, and
	/// registered with the real docking host via <c>IPaneModelHost.Add</c> (added to that
	/// interface specifically for this pad, see its doc comment in <c>ViewModels/PaneModel.cs</c>).
	/// </summary>
	sealed class BreakPointsPadViewModel : BookmarkPadViewModelBase
	{
		public BreakPointsPadViewModel()
		{
			Title = "Breakpoints";
			ContentId = "BreakPointsPad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(BreakPointsPad).FullName;
		}

		protected override void CreateToolBarContent()
		{
			var res = new CommonResources();
			res.InitializeComponent();

			ToolBar toolbar = ToolBarService.CreateToolBar(control, this, "/SharpDevelop/Pads/BreakpointPad/Toolbar");
			control.Children.Add(toolbar);

			control.listView.View = (GridView)res["breakpointsGridView"];
			control.listView.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "35;50%;50%");
		}

		protected override bool ShowBookmarkInThisPad(SDBookmark mark)
		{
			return mark.ShowInPad(this) && mark is BreakpointBookmark;
		}
	}
}
