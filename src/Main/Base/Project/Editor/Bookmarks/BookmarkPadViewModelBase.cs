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

using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.SharpDevelop.Editor.Bookmarks
{
	/// <summary>
	/// Shared behavior for the two bookmark-list panes (doc/technotes/ilspy.md "Legacy Pad
	/// migration", 2026-08-04): <c>BookmarkPad</c> (Base/App project) and Debugger.AddIn's
	/// <c>BreakPointsPad</c>. Replaces the old <c>BookmarkPadBase : AbstractPadContent</c> - rebased
	/// onto <see cref="ToolPaneModel"/> so it lives here in the Base project and is directly
	/// constructible from any AddIn (Debugger.AddIn included), not just the App project. See
	/// <see cref="ViewModels.IPaneModelHost.Add"/> for how an AddIn-owned subclass gets registered
	/// with the real docking host without a compile-time reference to the App project.
	/// </summary>
	public abstract class BookmarkPadViewModelBase : ToolPaneModel
	{
		protected readonly BookmarkPadContent control;
		bool subscribed;

		protected BookmarkPadViewModelBase()
		{
			control = new BookmarkPadContent();
			control.InitializeComponent();
			Content = control;
		}

		public ListView ListView => control.listView;

		public ItemCollection Items => control.listView.Items;

		public SDBookmark SelectedItem => (SDBookmark)control.listView.SelectedItem;

		public IEnumerable<SDBookmark> SelectedItems => control.listView.SelectedItems.OfType<SDBookmark>();

		/// <summary>
		/// Subscribes to <c>SD.BookmarkManager</c> and wires the list view on first real use rather
		/// than in the constructor - same early-startup hazard already guarded against for every
		/// other migrated pane in this effort. Both <c>Bookmarks</c> and <c>BreakPointsPad</c>
		/// default hidden in the AddInTree, so (like <c>Outline</c>/<c>DefinitionView</c>) deferring
		/// to <see cref="Show"/> alone is enough - nothing reaches this pane before a user/test
		/// explicitly activates it.
		/// </summary>
		public override void Show()
		{
			EnsureSubscribed();
			base.Show();
		}

		protected void EnsureSubscribed()
		{
			if (subscribed || SD.Services.GetService(typeof(Workbench.IWorkbench)) == null)
				return;
			subscribed = true;

			SD.BookmarkManager.BookmarkAdded += BookmarkManagerAdded;
			SD.BookmarkManager.BookmarkRemoved += BookmarkManagerRemoved;

			foreach (SDBookmark bookmark in SD.BookmarkManager.Bookmarks) {
				if (ShowBookmarkInThisPad(bookmark)) {
					this.Items.Add(bookmark);
				}
			}

			control.listView.MouseDoubleClick += delegate {
				SDBookmark bm = control.listView.SelectedItem as SDBookmark;
				if (bm != null)
					OnItemActivated(bm);
			};

			control.listView.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) {
				var selectedItems = this.SelectedItems.ToList();
				if (!selectedItems.Any())
					return;
				switch (e.Key) {
					case System.Windows.Input.Key.Delete:
						foreach (var selectedItem in selectedItems) {
							SD.BookmarkManager.RemoveMark(selectedItem);
						}
						break;
				}
			};

			CreateToolBarContent();
		}

		/// <summary>
		/// Builds this pane's toolbar and any other pad-specific list view setup (different
		/// AddInTree path and column layout per subclass - <c>BookmarkPad</c>'s vs
		/// <c>BreakPointsPad</c>'s). Deferred here, alongside the rest of
		/// <see cref="EnsureSubscribed"/>, same reasoning as <c>TaskListViewModel</c>'s own toolbar
		/// construction.
		/// </summary>
		protected abstract void CreateToolBarContent();

		public void Dispose()
		{
			if (!subscribed)
				return;
			SD.BookmarkManager.BookmarkAdded -= BookmarkManagerAdded;
			SD.BookmarkManager.BookmarkRemoved -= BookmarkManagerRemoved;
		}

		protected abstract bool ShowBookmarkInThisPad(SDBookmark mark);

		protected virtual void OnItemActivated(SDBookmark bm)
		{
			SD.FileService.JumpToFilePosition(bm.FileName, bm.LineNumber, 1);
		}

		void BookmarkManagerAdded(object sender, BookmarkEventArgs e)
		{
			if (ShowBookmarkInThisPad(e.Bookmark)) {
				this.Items.Add(e.Bookmark);
			}
		}

		void BookmarkManagerRemoved(object sender, BookmarkEventArgs e)
		{
			this.Items.Remove(e.Bookmark);
		}
	}
}
