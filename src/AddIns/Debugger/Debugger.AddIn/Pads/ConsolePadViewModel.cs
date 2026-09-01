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

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.ILSpy.ViewModels;
using Debugger.AddIn.Pads.Controls;
using Debugger.AddIn.Service.Dap;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="ConsolePad"/> (AddInTree pad id "ConsolePad").
	/// Not a MEF part - Debugger.AddIn's assembly is never scanned by <c>OpenDevelopMefHost</c>
	/// - so it is constructed with a plain <c>new</c> by the <see cref="ConsolePad"/> shim on
	/// first real use and registered with the real docking host via <c>IPaneModelHost.Add</c>.
	/// Hosts the shared <see cref="ConsolePadCore"/> (the same console body the legacy
	/// <see cref="AbstractConsolePad"/> wraps); the CommonConsole toolbar commands drive it
	/// through <see cref="IConsolePadHost"/>.
	/// </summary>
	sealed class ConsolePadViewModel : ToolPaneModel, IConsolePadHost, IEditable, IPositionable, IToolsHost
	{
		readonly ConsolePadCore core;

		public ConsolePadViewModel()
		{
			Title = "Console";
			ContentId = "ConsolePad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(ConsolePad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			core = new ConsolePadCore(() => "> ", AcceptCommand, AbstractConsolePadTextEntered, BuildToolBar);
			Content = core.Content;
		}

		ToolBar BuildToolBar(ConsoleControl console)
		{
			return ToolBarService.CreateToolBar(console, this, "/SharpDevelop/Pads/ConsolePad/ToolBar");
		}

		bool AcceptCommand(string command)
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
				core.Append(Environment.NewLine + "No process is being debugged");
				return;
			}
			if (!session.IsPaused) {
				core.Append(Environment.NewLine + "The process is running");
				return;
			}

			try {
				var result = await WindowsDebugger.EvaluateAsync(code, "repl").ConfigureAwait(true);
				if (!string.IsNullOrEmpty(result.Value)) {
					core.Append(Environment.NewLine + result.Value);
				}
			} catch (DapEvaluationException ex) {
				core.Append(Environment.NewLine + ex.Message);
			}
		}

		void AbstractConsolePadTextEntered(TextCompositionEventArgs e)
		{
			if (e.Text != ".")
				return;
			var frame = WindowsDebugger.CurrentStackFrame;
			if (frame == null || string.IsNullOrEmpty(frame.FilePath))
				return;
			var fileName = new FileName(frame.FilePath);
			var textLocation = new TextLocation(frame.Line, frame.Column);
			var binding = DebuggerDotCompletion.PrepareDotCompletion(core.Console.CommandText, SD.ParserService.ResolveContext(fileName, textLocation));
			if (binding == null) return;
			binding.HandleKeyPressed(core.Console.TextEditor, '.');
		}

		public ITextEditor TextEditor => core.TextEditor;

		public ITextSource CreateSnapshot() => core.CreateSnapshot();

		string IEditable.Text => core.Text;

		void IPositionable.JumpTo(int line, int column) => core.JumpTo(line, column);

		int IPositionable.Line => core.Line;

		int IPositionable.Column => core.Column;

		object IToolsHost.ToolsContent => null;

		void IConsolePadHost.ClearConsole() => core.ClearConsole();

		void IConsolePadHost.DeleteHistory() => core.DeleteHistory();

		bool IConsolePadHost.WordWrap {
			get { return core.WordWrap; }
			set { core.WordWrap = value; }
		}
	}
}
