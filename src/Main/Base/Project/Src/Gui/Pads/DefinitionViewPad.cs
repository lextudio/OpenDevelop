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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Roslyn;
using ICSharpCode.SharpDevelop.Workbench;
using Microsoft.CodeAnalysis;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Description of the pad content
	/// </summary>
	public class DefinitionViewPad : AbstractPadContent
	{
		AvalonEdit.TextEditor ctl;
		DispatcherTimer timer;
		
		/// <summary>
		/// The control representing the pad
		/// </summary>
		public override object Control {
			get { return ctl; }
		}
		
		/// <summary>
		/// Creates a new DefinitionViewPad object
		/// </summary>
		public DefinitionViewPad()
		{
			ctl = Editor.AvalonEditTextEditorAdapter.CreateAvalonEditInstance();
			ctl.IsReadOnly = true;
			ctl.MouseDoubleClick += OnDoubleClick;
			SD.ParserService.ParseInformationUpdated += OnParserUpdateStep;
			SD.ParserService.LoadSolutionProjectsThread.Finished += LoadThreadFinished;
			timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
			timer.Tick += delegate { UpdateTick(null); };
			timer.IsEnabled = !SD.ParserService.LoadSolutionProjectsThread.IsRunning;
			ctl.IsVisibleChanged += delegate { UpdateTick(null); };
		}
		
		/// <summary>
		/// Cleans up all used resources
		/// </summary>
		public override void Dispose()
		{
			SD.ParserService.ParseInformationUpdated -= OnParserUpdateStep;
			SD.ParserService.LoadSolutionProjectsThread.Finished -= LoadThreadFinished;
			ctl.Document = null;
			base.Dispose();
		}
		
		void OnDoubleClick(object sender, EventArgs e)
		{
			FileName fileName = currentFileName;
			if (fileName != null) {
				var caret = ctl.TextArea.Caret;
				SD.FileService.JumpToFilePosition(fileName, caret.Line, caret.Column);
				
				// refresh DefinitionView to show the definition of the expression that was double-clicked
				UpdateTick(null);
			}
		}
		
		void LoadThreadFinished(object sender, EventArgs e)
		{
			timer.IsEnabled = true;
			UpdateTick(null);
		}
		
		void OnParserUpdateStep(object sender, ParseInformationEventArgs e)
		{
			UpdateTick(e);
		}
		
		async void UpdateTick(ParseInformationEventArgs e)
		{
			bool isActive = ctl.IsVisible && !SD.ParserService.LoadSolutionProjectsThread.IsRunning;
			timer.IsEnabled = isActive;
			if (!isActive) return;
			LoggingService.Debug("DefinitionViewPad.Update");
			
			ISymbol symbol = await ResolveAtCaretAsync(e);
			if (symbol == null) return;
			var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
			if (location == null) return; // TODO : try to decompile?
			OpenFile(location.GetLineSpan());
		}

		Task<ISymbol> ResolveAtCaretAsync(ParseInformationEventArgs e)
		{
			IWorkbenchWindow window = SD.Workbench.ActiveWorkbenchWindow;
			if (window == null)
				return Task.FromResult<ISymbol>(null);
			IViewContent viewContent = window.ActiveViewContent;
			if (viewContent == null)
				return Task.FromResult<ISymbol>(null);
			ITextEditor editor = viewContent.GetService<ITextEditor>();
			if (editor == null)
				return Task.FromResult<ISymbol>(null);

			// e might be null when this is a manually triggered update
			// don't resolve when an unrelated file was changed
			if (e != null && editor.FileName != e.FileName)
				return Task.FromResult<ISymbol>(null);

			return Task.Run(() => RoslynWorkspaceHelper.GetSymbolAtCaret(editor));
		}

		FileLinePositionSpan oldPosition;
		FileName currentFileName;

		void OpenFile(FileLinePositionSpan pos)
		{
			if (pos.Equals(oldPosition)) return;
			oldPosition = pos;
			var fileName = new FileName(pos.Path);
			if (fileName != currentFileName)
				LoadFile(fileName);
			ctl.TextArea.Caret.Location = new TextLocation(pos.StartLinePosition.Line + 1, pos.StartLinePosition.Character + 1);
			Rect r = ctl.TextArea.Caret.CalculateCaretRectangle();
			if (!r.IsEmpty) {
				ctl.ScrollToVerticalOffset(r.Top - 4);
			}
		}
		
		/// <summary>
		/// Loads the file from the corresponding text editor window if it is
		/// open otherwise the file is loaded from the file system.
		/// </summary>
		void LoadFile(FileName fileName)
		{
			// Load the text into the definition view's text editor.
			ctl.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(SD.FileService.GetFileContent(fileName));
			ctl.Document.FileName = fileName;
			currentFileName = fileName;
			ctl.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
		}
	}
}
