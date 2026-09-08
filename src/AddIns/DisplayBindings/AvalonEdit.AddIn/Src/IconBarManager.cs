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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.TypeSystem;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Stores the entries in the icon bar margin. Multiple icon bar margins
	/// can use the same manager if split view is used.
	/// </summary>
	public class IconBarManager : IBookmarkMargin
	{
		ObservableCollection<IBookmark> bookmarks = new ObservableCollection<IBookmark>();
		long outlineRequestVersion;
		
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

		/// <summary>
		/// Remote parsers intentionally do not expose live type-system entities. Use the same
		/// document-outline DTO as the navigation bar to retain declaration icons without creating
		/// an in-process Roslyn workspace.
		/// </summary>
		public async Task UpdateLanguageServiceBookmarksAsync(FileName fileName, IDocument document, CancellationToken cancellationToken = default)
		{
			if (fileName == null || document == null)
				return;
			var requestVersion = Interlocked.Increment(ref outlineRequestVersion);
			try {
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (registry == null || !registry.TryGetService(fileName, out var service))
					return;
				var outline = await service.GetDocumentOutlineAsync(new DocumentId(fileName), cancellationToken).ConfigureAwait(false);
				await SD.MainThread.InvokeAsync(() => {
					if (requestVersion != Volatile.Read(ref outlineRequestVersion))
						return;
					for (int i = bookmarks.Count - 1; i >= 0; i--)
						if (bookmarks[i] is OutlineBookmark)
							bookmarks.RemoveAt(i);
					foreach (var node in outline)
						AddOutlineBookmarks(node, document);
				});
			} catch (OperationCanceledException) {
			} catch (Exception ex) {
				LoggingService.Warn("Language-service icon bar outline failed for '" + fileName + "': " + ex.Message);
			}
		}

		void AddOutlineBookmarks(DocumentOutlineNode node, IDocument document)
		{
			if (node.Span.Start.Line >= 1 && node.Span.Start.Line <= document.LineCount)
				bookmarks.Add(new OutlineBookmark(node));
			foreach (var child in node.Children)
				AddOutlineBookmarks(child, document);
		}

		sealed class OutlineBookmark : IBookmark
		{
			readonly DocumentOutlineNode node;
			public OutlineBookmark(DocumentOutlineNode node) => this.node = node;
			public int LineNumber => node.Span.Start.Line;
			public IImage Image => node.Kind switch {
				"Interface" => ClassBrowserIconService.Interface,
				"Struct" or "Structure" => ClassBrowserIconService.Struct,
				"Enum" => ClassBrowserIconService.Enum,
				"Delegate" => ClassBrowserIconService.Delegate,
				"Method" or "Function" or "Constructor" => ClassBrowserIconService.Method,
				"Field" => ClassBrowserIconService.Field,
				"Property" => ClassBrowserIconService.Property,
				"Event" => ClassBrowserIconService.Event,
				_ => ClassBrowserIconService.Class
			};
			public int ZOrder => -10;
			public void MouseDown(MouseButtonEventArgs e) { }
			public void MouseUp(MouseButtonEventArgs e) { }
			public bool CanDragDrop => false;
			public void Drop(int lineNumber) => throw new NotSupportedException();
			public bool DisplaysTooltip => true;
			public object CreateTooltipContent() => node.Name;
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
