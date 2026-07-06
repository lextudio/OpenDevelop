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
using System.Windows.Controls;
using System.Windows.Input;

using Debugger.AddIn.Pads.Controls;
using Debugger.AddIn.Service.Dap;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	public class ConsolePad : AbstractConsolePad
	{
		const string debuggerConsoleToolBarTreePath = "/SharpDevelop/Pads/ConsolePad/ToolBar";

		protected override bool AcceptCommand(string command)
		{
			if (!string.IsNullOrEmpty(command)) {
				EvaluateAsync(command).FireAndForget();
			}
			return true;
		}

		async System.Threading.Tasks.Task EvaluateAsync(string code)
		{
			var session = WindowsDebugger.CurrentSession;
			if (session == null) {
				Append(Environment.NewLine + "No process is being debugged");
				return;
			}
			if (!session.IsPaused) {
				Append(Environment.NewLine + "The process is running");
				return;
			}

			try {
				var result = await WindowsDebugger.EvaluateAsync(code, "repl").ConfigureAwait(true);
				if (!string.IsNullOrEmpty(result.Value)) {
					Append(Environment.NewLine + result.Value);
				}
			} catch (DapEvaluationException ex) {
				Append(Environment.NewLine + ex.Message);
			}
		}

		protected override string Prompt {
			get {
				return "> ";
			}
		}

		public ConsolePad()
		{
			WindowsDebugger debugger = (WindowsDebugger)SD.Debugger;
		}

		protected override void AbstractConsolePadTextEntered(object sender, TextCompositionEventArgs e)
		{
			if (e.Text != ".")
				return;
			var frame = WindowsDebugger.CurrentStackFrame;
			if (frame == null || string.IsNullOrEmpty(frame.FilePath))
				return;
			var fileName = new FileName(frame.FilePath);
			var textLocation = new TextLocation(frame.Line, frame.Column);
			var binding = DebuggerDotCompletion.PrepareDotCompletion(console.CommandText, SD.ParserService.ResolveContext(fileName, textLocation));
			if (binding == null) return;
			binding.HandleKeyPressed(console.TextEditor, '.');
		}

		protected override ToolBar BuildToolBar()
		{
			return ToolBarService.CreateToolBar(console, this, debuggerConsoleToolBarTreePath);
		}
	}
}
