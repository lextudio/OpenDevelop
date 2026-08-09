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
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.AvalonEdit;
using ICSharpCode.SharpDevelop.Workbench;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Legacy interactive-console pad base (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09): the console body now lives in <see cref="ConsolePadCore"/> (shared with the
	/// migrated <c>ConsolePadViewModel</c>), this class keeps its historical
	/// <see cref="AbstractPadContent"/> surface and delegates to a core built from the subclass's
	/// overrides. Still the base of the unmigrated <c>FSharpInteractive</c> pad.
	/// </summary>
	public abstract class AbstractConsolePad : AbstractPadContent, IConsolePadHost, IEditable, IPositionable, IToolsHost
	{
		const string toolBarTreePath = "/SharpDevelop/Pads/CommonConsole/ToolBar";

		readonly ConsolePadCore core;

		protected ConsoleControl console => core.Console;

		protected AbstractConsolePad()
		{
			core = new ConsolePadCore(() => Prompt, AcceptCommand, e => AbstractConsolePadTextEntered(this, e), BuildToolBar);
		}

		protected virtual ToolBar BuildToolBar(ConsoleControl console)
		{
			return ToolBarService.CreateToolBar(console, this, toolBarTreePath);
		}

		public virtual ITextEditor TextEditor {
			get {
				return core.TextEditor;
			}
		}

		public override object Control {
			get { return core.Content; }
		}

		public override object InitiallyFocusedControl {
			get { return console.editor; }
		}

		/// <summary>
		/// Creates a snapshot of the editor content.
		/// This method is thread-safe.
		/// </summary>
		public ITextSource CreateSnapshot()
		{
			return core.CreateSnapshot();
		}

		string IEditable.Text {
			get {
				return core.Text;
			}
		}

		public virtual IDocument GetDocumentForFile(OpenedFile file)
		{
			return null;
		}

		#region IPositionable implementation
		void IPositionable.JumpTo(int line, int column)
		{
			this.TextEditor.JumpTo(line, column);
		}

		int IPositionable.Line {
			get {
				return this.TextEditor.Caret.Line;
			}
		}

		int IPositionable.Column {
			get {
				return this.TextEditor.Caret.Column;
			}
		}
		#endregion

		object IToolsHost.ToolsContent {
			// TextEditorSideBar (WinForms) is out of MVP scope - no tools content in this build.
			get { return null; }
		}

		protected virtual bool HandleInput(Key key)
		{
			return core.HandleInput(key);
		}

		/// <summary>
		/// Deletes the content of the console and prints a new prompt.
		/// </summary>
		public void ClearConsole()
		{
			core.ClearConsole();
		}

		/// <summary>
		/// Deletes the console history.
		/// </summary>
		public void DeleteHistory()
		{
			core.DeleteHistory();
		}

		public void SetHighlighting(string language)
		{
			core.SetHighlighting(language);
		}

		public bool WordWrap {
			get { return core.WordWrap; }
			set { core.WordWrap = value; }
		}

		protected abstract string Prompt {
			get;
		}

		protected abstract bool AcceptCommand(string command);

		protected virtual void InitializeConsole()
		{
			// The prompt is now appended by ConsolePadCore itself; kept as a hook for subclasses.
		}

		protected virtual void AppendPrompt()
		{
			core.AppendPrompt();
		}

		protected void AppendLine(string text)
		{
			core.AppendLine(text);
		}

		protected void Append(string text)
		{
			core.Append(text);
		}

		protected void InsertBeforePrompt(string text)
		{
			core.InsertBeforePrompt(text);
		}

		protected virtual void AbstractConsolePadTextEntered(object sender, TextCompositionEventArgs e)
		{
		}

		protected virtual void Clear()
		{
			this.ClearConsole();
		}
	}

	public class ConsoleControl : Grid
	{
		internal AvalonEdit.TextEditor editor;
		internal ITextEditor editorAdapter;
		internal BeginReadOnlySectionProvider readOnlyRegion;

		public event TextCompositionEventHandler TextAreaTextEntered;
		public event KeyEventHandler TextAreaPreviewKeyDown;

		static TextEditorOptions consoleOptions;

		public ConsoleControl()
		{
			this.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
			this.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });

			object tmp;

			this.editorAdapter = SD.EditorControlService.CreateEditor(out tmp);

			this.editor = (AvalonEdit.TextEditor)tmp;
			this.editor.SetValue(Grid.ColumnProperty, 0);
			this.editor.SetValue(Grid.RowProperty, 0);
			this.editor.ShowLineNumbers = false;

			if (consoleOptions == null) {
				consoleOptions = new TextEditorOptions(editor.Options);
				consoleOptions.AllowScrollBelowDocument = false;
			}

			this.editor.Options = consoleOptions;

			this.Children.Add(editor);

			editor.TextArea.ReadOnlySectionProvider = readOnlyRegion = new BeginReadOnlySectionProvider();
			editor.TextArea.TextEntered += new TextCompositionEventHandler(editor_TextArea_TextEntered);
			editor.TextArea.PreviewKeyDown += new KeyEventHandler(editor_TextArea_PreviewKeyDown);
		}

		public ITextEditor TextEditor {
			get {
				return editorAdapter;
			}
		}

		public Encoding Encoding {
			get {
				return this.editor.Encoding;
			}
			set {
				this.editor.Encoding = value;
			}
		}

		public void SelectText(int line, int column, int length)
		{
			int offset = this.editor.Document.GetOffset(new TextLocation(line, column));
			this.editor.Select(offset, length);
		}

		public void SetHighlighting(string language)
		{
			editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(language);
		}

		public void Append(string text)
		{
			editor.AppendText(text);
		}

		/// <summary>
		/// Sets the readonly region to a specified offset.
		/// </summary>
		public void SetReadonly(int offset)
		{
			readOnlyRegion.EndOffset = offset;
		}

		/// <summary>
		/// Sets the readonly region to the end of the document.
		/// </summary>
		public void SetReadonly()
		{
			readOnlyRegion.EndOffset = editor.Document.TextLength;
		}

		/// <summary>
		/// Hides the scroll bar.
		/// </summary>
		public void HideScrollBar()
		{
			this.editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
			this.editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		}

		public void JumpToLine(int line)
		{
			this.editor.ScrollToLine(line);
		}

		public int CommandOffset {
			get { return readOnlyRegion.EndOffset; }
		}

		/// <summary>
		/// Gets/sets the command text displayed at the command prompt.
		/// </summary>
		public string CommandText {
			get {
				return editor.Document.GetText(new TextSegment() { StartOffset = readOnlyRegion.EndOffset, EndOffset = editor.Document.TextLength });
			}
			set {
				editor.Document.Replace(new TextSegment() { StartOffset = readOnlyRegion.EndOffset, EndOffset = editor.Document.TextLength }, value);
			}
		}

		void editor_TextArea_TextEntered(object sender, TextCompositionEventArgs e)
		{
			TextCompositionEventHandler handler = TextAreaTextEntered;

			if (handler != null)
				handler(this, e);
		}

		void editor_TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			KeyEventHandler handler = TextAreaPreviewKeyDown;

			if (handler != null)
				handler(this, e);
		}

		public void Clear()
		{
			editor.Clear();
		}
	}

	public class BeginReadOnlySectionProvider : IReadOnlySectionProvider
	{
		public int EndOffset { get; set; }

		public bool CanInsert(int offset)
		{
			return offset >= EndOffset;
		}

		public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
		{
			if (segment.EndOffset <= this.EndOffset)
				return Enumerable.Empty<ISegment>();

			return new[] {
				new TextSegment() {
					StartOffset = Math.Max(this.EndOffset, segment.Offset),
					EndOffset = segment.EndOffset
				}
			};
		}
	}

	class ClearConsoleCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var pad = this.Owner as IConsolePadHost;
			if (pad != null)
				pad.ClearConsole();
		}
	}

	class DeleteHistoryCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var pad = this.Owner as IConsolePadHost;
			if (pad != null)
				pad.DeleteHistory();
		}
	}

	class ToggleConsoleWordWrapCommand : ICheckableMenuCommand
	{
		public event EventHandler IsCheckedChanged = delegate {};

		public event EventHandler CanExecuteChanged { add {} remove {} }

		public bool IsChecked(object parameter)
		{
			var pad = parameter as IConsolePadHost;
			return pad != null && pad.WordWrap;
		}

		public bool CanExecute(object parameter)
		{
			return true;
		}

		public void Execute(object parameter)
		{
			var pad = parameter as IConsolePadHost;
			if (pad == null)
				return;
			pad.WordWrap = !pad.WordWrap;
			IsCheckedChanged(this, EventArgs.Empty);
		}
	}
}
