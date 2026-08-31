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
using System.Collections.ObjectModel;
using System.Collections.Specialized;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.TypeSystem;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Stores the entries in the icon bar margin. Multiple icon bar margins
	/// can use the same manager if split view is used.
	/// </summary>
	public class IconBarManager : IBookmarkMargin
	{
		ObservableCollection<IBookmark> bookmarks = new ObservableCollection<IBookmark>();
		
		public IconBarManager()
		{
			bookmarks.CollectionChanged += bookmarks_CollectionChanged;
		}
		
		public IList<IBookmark> Bookmarks {
			get { return bookmarks; }
		}
		
		void bookmarks_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			Redraw();
		}
		
		public void Redraw()
		{
			if (RedrawRequested != null)
				RedrawRequested(this, EventArgs.Empty);
		}
		
		public event EventHandler RedrawRequested;
		
		public void UpdateClassMemberBookmarks(IUnresolvedFile parseInfo, IDocument document)
		{
			for (int i = bookmarks.Count - 1; i >= 0; i--) {
				if (bookmarks[i] is EntityBookmark)
					bookmarks.RemoveAt(i);
			}
			if (parseInfo == null)
				return;
			foreach (var c in parseInfo.TopLevelTypeDefinitions) {
				AddEntityBookmarks(c, document, parseInfo.FileName);
			}
		}

		// A partial class's Members (and, in principle, NestedTypes) can span every file the type
		// is declared across - e.g. a WinForms Form1's Members include InitializeComponent and the
		// designer-generated fields declared in Form1.Designer.cs, not just the ones in Form1.cs.
		// EntityBookmark.LineNumber falls back to the entity's own (unrelated) line number whenever
		// it doesn't fit within the currently open document (EntityBookmark's constructor only
		// clamps, it never rejects), so without this filter a member declared in a sibling file can
		// still render its icon at whatever line number happens to coincide in the open document -
		// observed as an extra/duplicate icon on Form1.cs at lines that are actually meaningless for
		// that file. Only bookmark entities that are actually declared in the file being edited.
		void AddEntityBookmarks(IUnresolvedTypeDefinition c, IDocument document, string fileName)
		{
			if (c.IsSynthetic) return;
			if (!c.Region.IsEmpty && FileUtility.IsEqualFileName(c.Region.FileName, fileName)) {
				bookmarks.Add(new EntityBookmark(c, document));
			}
			foreach (var innerClass in c.NestedTypes) {
				AddEntityBookmarks(innerClass, document, fileName);
			}
			foreach (var m in c.Members) {
				if (m.Region.IsEmpty || m.IsSynthetic) continue;
				if (!FileUtility.IsEqualFileName(m.Region.FileName, fileName)) continue;
				bookmarks.Add(new EntityBookmark(m, document));
			}
		}
	}
}
