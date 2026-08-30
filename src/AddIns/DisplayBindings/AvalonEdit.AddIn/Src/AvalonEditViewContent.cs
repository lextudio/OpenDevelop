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
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Utils;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Parser;
using ProjectService = ICSharpCode.SharpDevelop.Project.ProjectService;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.AvalonEdit.AddIn
{
	public class AvalonEditViewContent
		: AbstractViewContent, IMementoCapable, IToolsHost
	{
		readonly CodeEditor codeEditor = new CodeEditor();
		IAnalyticsMonitorTrackedFeature trackedFeature;
		
		public AvalonEditViewContent(OpenedFile file, Encoding fixedEncodingForLoading = null)
		{
			// Use common service container for view content and primary text editor.
			// This makes all text editor services available as view content services and vice versa.
			// (with the exception of the interfaces implemented directly by this class,
			// those are available as view-content services only)
			this.Services = codeEditor.PrimaryTextEditor.GetRequiredService<IServiceContainer>();
			if (fixedEncodingForLoading != null) {
				codeEditor.UseFixedEncoding = true;
				codeEditor.PrimaryTextEditor.Encoding = fixedEncodingForLoading;
			}
			this.TabPageText = "${res:FormsDesigner.DesignTabPages.SourceTabPage}";
			
			if (file.FileName != null) {
				string filetype = Path.GetExtension(file.FileName);
				if (!IsKnownFileExtension(filetype))
					filetype = ".?";
				trackedFeature = SD.AnalyticsMonitor.TrackFeature(typeof(AvalonEditViewContent), "open" + filetype.ToLowerInvariant());
				if (filetype.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
					SetupXamlDragDrop();
			}
			
			this.Files.Add(file);
			file.ForceInitializeView(this);
			
			file.IsDirtyChanged += PrimaryFile_IsDirtyChanged;
			codeEditor.Document.UndoStack.PropertyChanged += codeEditor_Document_UndoStack_PropertyChanged;
		}
		
		bool IsKnownFileExtension(string filetype)
		{
			return ProjectService.GetFileFilters().Any(f => f.ContainsExtension(filetype)) ||
				IconService.HasImageForFile(filetype);
		}
		
		void codeEditor_Document_UndoStack_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (!isLoading)
				PrimaryFile.IsDirty = !codeEditor.Document.UndoStack.IsOriginalFile;
		}
		
		void PrimaryFile_IsDirtyChanged(object sender, EventArgs e)
		{
			var document = codeEditor.Document;
			if (document != null) {
				var undoStack = document.UndoStack;
				if (this.PrimaryFile.IsDirty) {
					if (undoStack.IsOriginalFile)
						undoStack.DiscardOriginalFileMarker();
				} else {
					undoStack.MarkAsOriginalFile();
				}
			}
		}

		public override object Control {
			get { return codeEditor; }
		}
		
		public override object InitiallyFocusedControl {
			get { return codeEditor.PrimaryTextEditor.TextArea; }
		}
		
		public override void Save(OpenedFile file, Stream stream)
		{
			if (file != PrimaryFile)
				return;
			
			if (codeEditor.CanSaveWithCurrentEncoding()) {
				codeEditor.Save(stream);
			} else {
				int r = MessageService.ShowCustomDialog(
					"${res:Dialog.Options.IDEOptions.TextEditor.General.FontGroupBox.FileEncodingGroupBox}",
					StringParser.Parse("${res:AvalonEdit.FileEncoding.EncodingCausesDataLoss}",
					                   new StringTagPair("encoding", codeEditor.Encoding.EncodingName)),
					0, -1,
					"${res:AvalonEdit.FileEncoding.EncodingCausesDataLoss.UseUTF8}",
					"${res:AvalonEdit.FileEncoding.EncodingCausesDataLoss.Continue}");
				if (r == 1) {
					// continue saving with data loss
					MemoryStream ms = new MemoryStream();
					codeEditor.Save(ms);
					ms.Position = 0;
					ms.WriteTo(stream);
					ms.Position = 0;
					// Read back the version we just saved to show the data loss to the user (he'll be able to press Undo).
					using (StreamReader reader = new StreamReader(ms, codeEditor.Encoding, false)) {
						codeEditor.Document.Text = reader.ReadToEnd();
					}
					return;
				} else {
					// unfortunately we don't support cancel within IViewContent.Save, so we'll use the safe choice of UTF-8 instead
					codeEditor.Encoding = System.Text.Encoding.UTF8;
					codeEditor.Save(stream);
				}
			}
		}
		
		bool isLoading;
		
		public override void Load(OpenedFile file, Stream stream)
		{
			if (file != PrimaryFile)
				return;
			isLoading = true;
			try {
				if (!file.IsUntitled) {
					codeEditor.PrimaryTextEditor.IsReadOnly = (File.GetAttributes(file.FileName) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
				}
				
				codeEditor.Load(stream);
				// Load() causes the undo stack to think stuff changed, so re-mark the file as original if necessary
				if (!this.PrimaryFile.IsDirty) {
					codeEditor.Document.UndoStack.MarkAsOriginalFile();
				}
				
				// we set the file name after loading because this will place the fold markers etc.
				codeEditor.FileName = file.FileName;
				BookmarksAttach();
			} finally {
				isLoading = false;
			}
		}
		
		protected override void OnFileNameChanged(OpenedFile file)
		{
			base.OnFileNameChanged(file);
			if (file == PrimaryFile) {
				FileName oldFileName = codeEditor.FileName;
				FileName newFileName = file.FileName;
				
				if (!string.IsNullOrEmpty(oldFileName))
					SD.ParserService.ClearParseInformation(oldFileName);
				
				
				BookmarksNotifyNameChange(oldFileName, newFileName);
				// changing the filename on the codeEditor raises several events; ensure
				// we got our state updated first (bookmarks, persistent anchors) before other code
				// processes the file name change
				
				codeEditor.FileName = newFileName;
				
				SD.ParserService.ParseAsync(file.FileName, codeEditor.Document).FireAndForget();
			}
		}
		
		public override INavigationPoint BuildNavPoint()
		{
			return codeEditor.BuildNavPoint();
		}
		
		public override bool IsReadOnly {
			get { return codeEditor.PrimaryTextEditor.IsReadOnly; }
		}
		
		#region Bookmark Handling
		bool bookmarksAttached;
		
		void BookmarksAttach()
		{
			if (bookmarksAttached) return;
			bookmarksAttached = true;
			foreach (SDBookmark bookmark in SD.BookmarkManager.GetBookmarks(codeEditor.FileName)) {
				bookmark.Document = codeEditor.Document;
				codeEditor.IconBarManager.Bookmarks.Add(bookmark);
			}
			SD.BookmarkManager.BookmarkAdded += BookmarkManager_Added;
			SD.BookmarkManager.BookmarkRemoved += BookmarkManager_Removed;
			
			PermanentAnchorService.AttachDocument(codeEditor.FileName, codeEditor.Document);
		}

		void BookmarksDetach()
		{
			if (codeEditor.FileName != null) {
				PermanentAnchorService.DetachDocument(codeEditor.FileName, codeEditor.Document);
			}
			
			SD.BookmarkManager.BookmarkAdded -= BookmarkManager_Added;
			SD.BookmarkManager.BookmarkRemoved -= BookmarkManager_Removed;
			foreach (SDBookmark bookmark in codeEditor.IconBarManager.Bookmarks.OfType<SDBookmark>()) {
				if (bookmark.Document == codeEditor.Document) {
					bookmark.Document = null;
				}
			}
			codeEditor.IconBarManager.Bookmarks.Clear();
		}
		
		void BookmarkManager_Removed(object sender, BookmarkEventArgs e)
		{
			codeEditor.IconBarManager.Bookmarks.Remove(e.Bookmark);
			if (e.Bookmark.Document == codeEditor.Document) {
				e.Bookmark.Document = null;
			}
		}
		
		void BookmarkManager_Added(object sender, BookmarkEventArgs e)
		{
			if (FileUtility.IsEqualFileName(this.PrimaryFileName, e.Bookmark.FileName)) {
				codeEditor.IconBarManager.Bookmarks.Add(e.Bookmark);
				e.Bookmark.Document = codeEditor.Document;
			}
		}
		
		void BookmarksNotifyNameChange(FileName oldFileName, FileName newFileName)
		{
			PermanentAnchorService.RenameDocument(oldFileName, newFileName, codeEditor.Document);
			
			foreach (SDBookmark bookmark in codeEditor.IconBarManager.Bookmarks.OfType<SDBookmark>()) {
				bookmark.FileName = newFileName;
			}
		}
		#endregion
		
		public override void Dispose()
		{
			if (trackedFeature != null)
				trackedFeature.EndTracking();
			if (PrimaryFile != null)
				this.PrimaryFile.IsDirtyChanged -= PrimaryFile_IsDirtyChanged;
			base.Dispose();
			BookmarksDetach();
			codeEditor.Dispose();
		}
		
		public override string ToString()
		{
			return "[" + GetType().Name + " " + this.PrimaryFileName + "]";
		}
		
		#region IMementoCapable
		public Properties CreateMemento()
		{
			Properties memento = new Properties();
			memento.Set("CaretOffset", codeEditor.ActiveTextEditor.CaretOffset);
			memento.Set("ScrollPositionY", codeEditor.ActiveTextEditor.VerticalOffset);
			return memento;
		}
		
		public void SetMemento(Properties memento)
		{
			codeEditor.PrimaryTextEditor.ScrollToVerticalOffset(memento.Get("ScrollPositionY", 0.0));
			try {
				codeEditor.PrimaryTextEditor.CaretOffset = memento.Get("CaretOffset", 0);
			} catch (ArgumentOutOfRangeException) {
				// ignore caret out of range - maybe file was changed externally?
			}
		}
		#endregion
		
		void SetupXamlDragDrop()
		{
			var textArea = codeEditor.PrimaryTextEditor.TextArea;
			// Toolbox drags are OLE drags originating outside AvalonEdit.  Subscribing
			// to Drop alone is not sufficient: WPF will not route such a drop unless
			// the target opts into it and advertises a permitted effect during the drag.
			textArea.AllowDrop = true;
			textArea.DragEnter += TextArea_XamlDragOver;
			textArea.DragOver += TextArea_XamlDragOver;
			textArea.Drop += TextArea_Drop;
			XamlDropLog("Setup", textArea, null);
		}

		void TextArea_XamlDragOver(object sender, DragEventArgs e)
		{
			XamlDropLog("DragOver", (TextArea)sender, e);
			if (e.Data.GetDataPresent("ComponentTypeName")) {
				e.Effects = e.AllowedEffects & DragDropEffects.Copy;
				e.Handled = true;
			}
		}

		void TextArea_Drop(object sender, DragEventArgs e)
		{
			XamlDropLog("Drop", (TextArea)sender, e);
			string typeName = e.Data.GetData("ComponentTypeName") as string;
			if (typeName == null)
				return;

			// Use unqualified class name as the tag (e.g. "Button" for Button).
			int lastDot = typeName.LastIndexOf('.');
			string tagName = lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;

			try {
				string xaml = $"<{tagName} />";
				int offset = GetDropOffset((TextArea)sender, e);
				XamlDropLog($"Insert offset={offset} len={codeEditor.Document.TextLength}", (TextArea)sender, e);
				codeEditor.Document.Insert(offset, xaml);
				// Leave the caret after the inserted markup, matching where a typed edit would end up.
				codeEditor.PrimaryTextEditor.CaretOffset = offset + xaml.Length;
				e.Handled = true;
				XamlDropLog("Inserted", (TextArea)sender, e);
			} catch (Exception ex) {
				XamlDropLog("Insert failed: " + ex, (TextArea)sender, e);
			}
		}

		static void XamlDropLog(string stage, TextArea textArea, DragEventArgs e)
		{
			try {
				var type = e?.Data?.GetData("ComponentTypeName") as string ?? "<none>";
				var position = e?.GetPosition(textArea) ?? new Point();
				File.AppendAllText(Path.Combine(Path.GetTempPath(), "opendevelop-xaml-drop.log"),
					$"[{DateTime.Now:HH:mm:ss.fff}] {stage} allowDrop={textArea.AllowDrop} type={type} pos=({position.X:F1},{position.Y:F1}) handled={e?.Handled}{Environment.NewLine}");
			} catch { }
		}

		/// <summary>
		/// Resolves the document offset the pointer was actually released over. Previously this
		/// inserted at the caret instead, which is wherever the user last clicked/typed - so a drop
		/// landed several lines away from the mouse unless the caret happened to already be there.
		/// </summary>
		int GetDropOffset(TextArea textArea, DragEventArgs e)
		{
			var textView = textArea.TextView;
			var position = e.GetPosition(textView);

			// Clamp into the visible area and then into the document before hit-testing, the same
			// way AvalonEdit's own drag-drop handling does (SelectionMouseHandler): a drop just
			// past the last line or below the text is a normal gesture, and should resolve to the
			// nearest real position rather than "outside the document" (null).
			position.Y = Math.Max(0, Math.Min(position.Y, textView.ActualHeight));
			var documentPoint = position + textView.ScrollOffset;
			if (documentPoint.Y >= textView.DocumentHeight)
				documentPoint.Y = Math.Max(0, textView.DocumentHeight - 1);

			var dropPosition = textView.GetPositionFloor(documentPoint);
			return dropPosition.HasValue
				? codeEditor.Document.GetOffset(dropPosition.Value.Location)
				: codeEditor.Document.TextLength;
		}

		object IToolsHost.ToolsContent {
			get {
				var fileName = this.PrimaryFileName;
				if (fileName == null || !fileName.ToString().EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
					return null;
				// A WinUI/Uno document's Source tab must show that framework's own Toolbox, not
				// WPF's - dragging a WPF-only control (e.g. RadioButton's WPF ContentControl shape)
				// onto a Uno document's markup would silently produce something the Uno compiler
				// can't resolve. Same detector the Design-tab secondary view binding itself uses
				// (WinUIXamlDesignerDisplayBinding.CanAttachTo) to decide WinUI/Uno vs WPF.
				var kind = ICSharpCode.SharpDevelop.LanguageServices.Xaml.XamlFrameworkDetector.Detect(fileName.ToString()).Kind;
				if (kind is ICSharpCode.SharpDevelop.LanguageServices.Xaml.XamlFrameworkKind.WinUI
					or ICSharpCode.SharpDevelop.LanguageServices.Xaml.XamlFrameworkKind.Uno)
					return ICSharpCode.WinUIXamlDesigner.WinUIXamlToolbox.Instance.ToolboxControl;
				return ICSharpCode.WpfDesign.AddIn.WpfToolbox.Instance.ToolboxControl;
			}
		}
	}
}
