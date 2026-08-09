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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.SharpDevelop.Editor;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Host-neutral console surface the <c>/SharpDevelop/Pads/CommonConsole/ToolBar</c> commands
	/// operate on (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09): implemented both by
	/// the legacy <see cref="AbstractConsolePad"/> (FSharpInteractive) and by the migrated
	/// <c>ConsolePadViewModel</c> (Debugger.AddIn), so a command can drive whichever object hosts
	/// the toolbar without referencing a concrete pad type.
	/// </summary>
	public interface IConsolePadHost
	{
		void ClearConsole();
		void DeleteHistory();
		bool WordWrap { get; set; }
	}

	/// <summary>
	/// Shared implementation of the interactive console body previously embedded in
	/// <see cref="AbstractConsolePad"/> (extracted 2026-08-09 so the migrated
	/// <c>ConsolePadViewModel</c> can host the same console without inheriting
	/// <see cref="AbstractPadContent"/>). Holds the panel/console/toolbar, the prompt/history
	/// handling and the readonly-command-region behavior; the host supplies the per-console
	/// pieces (prompt text, command acceptance, text-entered hook, toolbar construction) as
	/// delegates.
	/// </summary>
	public class ConsolePadCore : IConsolePadHost, IEditable, IPositionable, IToolsHost
	{
		readonly Func<string> promptProvider;
		readonly Func<string, bool> acceptCommand;
		readonly Action<TextCompositionEventArgs> textEntered;
		readonly Func<ConsoleControl, ToolBar> buildToolBar;

		readonly Grid panel;
		readonly ConsoleControl console;
		readonly ToolBar toolbar;

		bool cleared;
		readonly IList<string> history = new List<string>();
		int historyPointer;

		public ConsolePadCore(Func<string> promptProvider, Func<string, bool> acceptCommand,
			Action<TextCompositionEventArgs> textEntered, Func<ConsoleControl, ToolBar> buildToolBar)
		{
			this.promptProvider = promptProvider;
			this.acceptCommand = acceptCommand;
			this.textEntered = textEntered;
			this.buildToolBar = buildToolBar;

			this.panel = new Grid();
			this.console = new ConsoleControl();

			// creating the toolbar accesses the WordWrap property, so we must do this after creating the console
			// (the console is passed in rather than read back from a host property: the host's own
			// reference to this core is not assigned until the constructor returns)
			this.toolbar = buildToolBar(this.console);
			this.toolbar.SetValue(DockPanel.DockProperty, Dock.Top);

			panel.Children.Add(toolbar);
			panel.Children.Add(console);

			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			Grid.SetRow(console, 1);

			this.console.editor.TextArea.PreviewKeyDown += (sender, e) => {
				e.Handled = HandleInput(e.Key);
			};

			this.console.editor.TextArea.TextEntered += (sender, e) => {
				if (this.textEntered != null)
					this.textEntered(e);
			};

			AppendPrompt();
		}

		public Grid Panel => panel;

		public ConsoleControl Console => console;

		public object Content => panel;

		public ITextEditor TextEditor {
			get {
				return console.TextEditor;
			}
		}

		/// <summary>
		/// Creates a snapshot of the editor content.
		/// This method is thread-safe.
		/// </summary>
		public ITextSource CreateSnapshot()
		{
			return this.TextEditor.Document.CreateSnapshot();
		}

		string GetText()
		{
			return this.TextEditor.Document.Text;
		}

		public string Text {
			get { return GetText(); }
		}

		string IEditable.Text {
			get {
				return GetText();
			}
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

		public void JumpTo(int line, int column)
		{
			this.TextEditor.JumpTo(line, column);
		}

		public int Line {
			get { return this.TextEditor.Caret.Line; }
		}

		public int Column {
			get { return this.TextEditor.Caret.Column; }
		}
		#endregion

		object IToolsHost.ToolsContent {
			// TextEditorSideBar (WinForms) is out of MVP scope - no tools content in this build.
			get { return null; }
		}

		public bool HandleInput(Key key)
		{
			switch (key) {
				case Key.Back:
				case Key.Delete:
					if (console.editor.SelectionStart == 0 &&
					    console.editor.SelectionLength == console.editor.Document.TextLength) {
						ClearConsole();
						return true;
					}
					return false;
				case Key.Down:
					if (console.CommandText.Contains("\n"))
						return false;
					this.historyPointer = Math.Min(this.historyPointer + 1, this.history.Count);
					if (this.historyPointer == this.history.Count)
						console.CommandText = "";
					else
						console.CommandText = this.history[this.historyPointer];
					console.editor.ScrollToEnd();
					return true;
				case Key.Up:
					if (console.CommandText.Contains("\n"))
						return false;
					this.historyPointer = Math.Max(this.historyPointer - 1, 0);
					if (this.historyPointer == this.history.Count)
						console.CommandText = "";
					else
						console.CommandText = this.history[this.historyPointer];
					console.editor.ScrollToEnd();
					return true;
				case Key.Return:
					if (Keyboard.Modifiers == ModifierKeys.Shift)
						return false;
					int caretOffset = this.console.TextEditor.Caret.Offset;
					string commandText = console.CommandText;
					cleared = false;
					if (acceptCommand(commandText)) {
						IDocument document = console.TextEditor.Document;
						if (!cleared) {
							if (document.GetCharAt(document.TextLength - 1) != '\n')
								document.Insert(document.TextLength, Environment.NewLine);
							AppendPrompt();
							console.TextEditor.Select(document.TextLength, 0);
						} else {
							console.CommandText = "";
						}
						cleared = false;
						this.history.Add(commandText);
						this.historyPointer = this.history.Count;
						console.editor.ScrollToEnd();
						return true;
					}
					return false;
				default:
					return false;
			}
		}

		/// <summary>
		/// Deletes the content of the console and prints a new prompt.
		/// </summary>
		public void ClearConsole()
		{
			this.console.editor.Document.Text = "";
			cleared = true;
			AppendPrompt();
		}

		/// <summary>
		/// Deletes the console history.
		/// </summary>
		public void DeleteHistory()
		{
			this.history.Clear();
			this.historyPointer = 0;
		}

		public void SetHighlighting(string language)
		{
			if (this.console != null)
				this.console.SetHighlighting(language);
		}

		public bool WordWrap {
			get { return this.console.editor.WordWrap; }
			set { this.console.editor.WordWrap = value; }
		}

		public void AppendPrompt()
		{
			console.Append(promptProvider());
			console.SetReadonly();
			console.editor.Document.UndoStack.ClearAll();
		}

		public void AppendLine(string text)
		{
			console.Append(text + Environment.NewLine);
		}

		public void Append(string text)
		{
			console.Append(text);
		}

		public void InsertBeforePrompt(string text)
		{
			int endOffset = this.console.readOnlyRegion.EndOffset;
			bool needScrollDown = this.console.editor.CaretOffset >= endOffset;
			this.console.editor.Document.Insert(endOffset - promptProvider().Length, text);
			this.console.editor.ScrollToEnd();
			this.console.SetReadonly(endOffset + text.Length);
		}

		public void Clear()
		{
			this.ClearConsole();
		}
	}
}
