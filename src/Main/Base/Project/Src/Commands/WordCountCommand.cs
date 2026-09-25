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
using System.Text.RegularExpressions;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace ICSharpCode.SharpDevelop.Commands
{
	/// <summary>
	/// Edit > Word Count: words, characters and lines of the selection, or of the whole document when
	/// nothing is selected. Replaces the WinForms WordCountDialog (Src\Commands\EditCommands.cs).
	/// </summary>
	public class WordCount : AbstractMenuCommand
	{
		static readonly Regex Word = new Regex(@"\w+", RegexOptions.Compiled);
		
		public override void Run()
		{
			ITextEditor textEditor = SD.GetActiveViewContentService<ITextEditor>();
			if (textEditor == null) {
				MessageService.ShowMessage("Open a text document to count its words.", "Word Count");
				return;
			}
			
			bool selection = textEditor.SelectionLength > 0;
			string text = selection ? textEditor.SelectedText : textEditor.Document.Text;
			// A trailing newline ends the last line rather than starting another one.
			int lines = text.Length == 0 ? 0 : text.Split('\n').Length - (text.EndsWith("\n", StringComparison.Ordinal) ? 1 : 0);
			string scope = selection ? "Selection" : System.IO.Path.GetFileName(textEditor.FileName);
			MessageService.ShowMessage(string.Format(
				"{0}\n\nWords: {1:N0}\nCharacters: {2:N0}\nCharacters (no whitespace): {3:N0}\nLines: {4:N0}",
				scope, Word.Matches(text).Count, text.Length, Regex.Replace(text, @"\s", "").Length, lines), "Word Count");
		}
	}
}
