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
using System.Diagnostics;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using Microsoft.Win32;
using Debugger.AddIn.Breakpoints;
using ICSharpCode.SharpDevelop.Services;

namespace Debugger.AddIn
{
	// RunToCursorCommand was removed: "run to cursor" has no standard DAP request and the bundled
	// SharpDbg adapter does not implement a custom extension for it. Known capability gap.

	public class SetCurrentStatementCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor textEditor = SD.GetActiveViewContentService<ITextEditor>();
			
			if (textEditor == null || SD.Debugger == null)
				return;
			
			SD.Debugger.SetInstructionPointer(textEditor.FileName, textEditor.Caret.Line, textEditor.Caret.Column, false);
		}
	}

	/// <summary>Restart the current debug session (relaunch or re-attach), like VS's Ctrl+Shift+F5.</summary>
	public class RestartDebuggerCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			if (SD.Debugger is ICSharpCode.SharpDevelop.Services.WindowsDebugger windowsDebugger)
				windowsDebugger.RestartAsync().FireAndForget();
		}
	}

	/// <summary>Run to Cursor: continue until the caret line is reached, using a temporary
	/// breakpoint that is removed once the debugger next stops (like VS's Ctrl+F10).</summary>
	public class RunToCursorCommand : AbstractMenuCommand
	{
		static ITextEditor runToCursorEditor;
		static int runToCursorLine;
		static bool runToCursorAdded;

		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || SD.Debugger == null || !SD.Debugger.IsDebugging)
				return;

			ClearRunToCursor();
			runToCursorEditor = editor;
			runToCursorLine = editor.Caret.Line;
			bool alreadySet = SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>()
				.Any(b => b.FileName == editor.FileName && b.LineNumber == runToCursorLine);
			if (!alreadySet) {
				SD.Debugger.ToggleBreakpointAt(editor, runToCursorLine); // adds and syncs with the adapter
				runToCursorAdded = true;
			}
			SD.Debugger.IsProcessRunningChanged += OnRunToCursorProcessRunningChanged;
			SD.Debugger.Continue();
		}

		static void OnRunToCursorProcessRunningChanged(object sender, EventArgs e)
		{
			if (SD.Debugger != null && !SD.Debugger.IsProcessRunning)
				ClearRunToCursor();
		}

		static void ClearRunToCursor()
		{
			if (SD.Debugger != null)
				SD.Debugger.IsProcessRunningChanged -= OnRunToCursorProcessRunningChanged;
			if (runToCursorAdded && runToCursorEditor != null && SD.Debugger != null) {
				SD.Debugger.ToggleBreakpointAt(runToCursorEditor, runToCursorLine); // removes and syncs
				runToCursorAdded = false;
			}
			runToCursorEditor = null;
		}
	}
	
	/// <summary>Insert a logpoint at the caret: a breakpoint that logs a message and continues.</summary>
	public class InsertLogpointCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null)
				return;

			var bookmark = BreakpointUtil.BreakpointsOnCaret.FirstOrDefault();
			if (bookmark == null) {
				bookmark = new BreakpointBookmark();
				SD.BookmarkManager.AddMark(bookmark, editor.Document, editor.Caret.Line);
			}
			if (string.IsNullOrEmpty(bookmark.LogMessage))
				bookmark.LogMessage = "logpoint hit";
			if (SD.Debugger is ICSharpCode.SharpDevelop.Services.WindowsDebugger windowsDebugger && windowsDebugger.IsDebugging)
				windowsDebugger.SyncBreakpointsForFileAsync(editor.FileName.ToString()).FireAndForget();
		}
	}

	public static class BreakpointUtil
	{
		public static IEnumerable<BreakpointBookmark> BreakpointsOnCaret {
			get {
				ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
				if (editor == null)
					return new BreakpointBookmark[0];
				
				return SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>().Where(bp => bp.FileName == editor.FileName && bp.LineNumber == editor.Caret.Line);
			}
		}
	}
	
	/// <summary>Enables every breakpoint.</summary>
	public class EnableAllBreakpointsCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			foreach (BreakpointBookmark bp in SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>())
				bp.IsEnabled = true;
		}
	}

	/// <summary>Disables every breakpoint.</summary>
	public class DisableAllBreakpointsCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			foreach (BreakpointBookmark bp in SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>())
				bp.IsEnabled = false;
		}
	}

	/// <summary>Deletes every breakpoint.</summary>
	public class DeleteAllBreakpointsCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			foreach (BreakpointBookmark bp in SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>().ToList())
				SD.BookmarkManager.RemoveMark(bp);
		}
	}

	public class EnableBreakpointMenuCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			foreach (BreakpointBookmark bp in BreakpointUtil.BreakpointsOnCaret) {
				bp.IsEnabled = true;
			}
		}
	}
	
	public class DisableBreakpointMenuCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			foreach (BreakpointBookmark bp in BreakpointUtil.BreakpointsOnCaret) {
				bp.IsEnabled = false;
			}
		}
	}
	
	public class IsActiveBreakpointCondition : IConditionEvaluator
	{
		public bool IsValid(object caller, Condition condition)
		{
			return BreakpointUtil.BreakpointsOnCaret.Any(bp => bp.IsEnabled);
		}
	}
	
	public class IsBreakpointCondition : IConditionEvaluator
	{
		public bool IsValid(object caller, Condition condition)
		{
			return BreakpointUtil.BreakpointsOnCaret.Any();
		}
	}
	
	public class DebugExecutableMenuCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			if (DebuggingOptions.Instance.AskForArguments) {
				var window = new ExecuteProcessWindow { Owner = SD.Workbench.MainWindow };
				if (window.ShowDialog() == true) {
					string fileName = window.SelectedExecutable;
					
					// execute the process
					StartExecutable(fileName, window.WorkingDirectory, window.Arguments);
				}
			} else {
				OpenFileDialog dialog = new OpenFileDialog() {
					Filter = ".NET executable|*.exe",
					RestoreDirectory = true,
					DefaultExt = "exe"
				};
				if (dialog.ShowDialog() == true) {
					string fileName = dialog.FileName;
					// execute the process
					StartExecutable(fileName);
				}
			}
		}
		
		void StartExecutable(string fileName, string workingDirectory = null, string arguments = null)
		{
			SD.Debugger.BreakAtBeginning = DebuggingOptions.Instance.BreakAtBeginning;
			SD.Debugger.Start(new ProcessStartInfo {
			                      	FileName = fileName,
			                      	WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName),
			                      	Arguments = arguments
			                      });
		}
	}
}
