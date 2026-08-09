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
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.ILSpyX.TreeView.PlatformAbstractions;
using ICSharpCode.ILSpy.Controls.TreeView;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09) - the real implementation is now <see cref="WatchPadViewModel"/>.
	/// Constructed once with a plain <c>new</c> and cached in a static field (Debugger.AddIn's
	/// assembly is never scanned by <c>OpenDevelopMefHost</c>), then registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Must stay a real, constructible
	/// <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in
	/// this migration - and because <c>WatchRootNode.Drop</c> and
	/// <c>AddWatchExpressionCommand</c> still route through
	/// <c>SD.Workbench.GetPad(typeof(WatchPad)).PadContent as WatchPad</c>.
	/// </summary>
	public class WatchPad : AbstractPadContent
	{
		static WatchPadViewModel viewModel;

		public WatchPad()
		{
			if (viewModel == null) {
				viewModel = new WatchPadViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel?.Content;

		public SharpTreeView Tree => viewModel?.Tree;

		public SharpTreeNodeCollection Items => viewModel?.Items;

		public void AddWatch(string expression = null, bool focus = false)
		{
			viewModel?.AddWatch(expression, focus);
		}
	}

	class WatchRootNode : SharpTreeNode
	{
		// SharpTreeView duplicate resolution (2026-08-02): ILSpyX's SharpTreeNode folds
		// GetDropEffect/CanPaste/Paste into a single CanDrop/Drop pair (see
		// doc/technotes/ilspy.md "SharpTreeView duplicate resolution"), so what used to be a
		// paste-via-clipboard-format check plus a separate drop-effect getter is now one
		// CanDrop/Drop pair operating on the drag event's own IPlatformDataObject.
		public override bool CanDrop(IPlatformDragEventArgs e, int index)
		{
			return e.Data.GetDataPresent(DataFormats.StringFormat);
		}

		public override void Drop(IPlatformDragEventArgs e, int index)
		{
			var watchValue = e.Data.GetData(DataFormats.StringFormat) as string;
			if (string.IsNullOrEmpty(watchValue)) return;

			var pad = SD.Workbench.GetPad(typeof(WatchPad)).PadContent as WatchPad;
			if (pad == null) return;

			pad.AddWatch(watchValue);
		}
	}

	static class WpfExtensions
	{
		public static T FindAncestor<T>(this DependencyObject d) where T : class
		{
			return AncestorsAndSelf(d).OfType<T>().FirstOrDefault();
		}

		public static IEnumerable<DependencyObject> AncestorsAndSelf(this DependencyObject d)
		{
			while (d != null) {
				yield return d;
				d = VisualTreeHelper.GetParent(d);
			}
		}

	}
}
