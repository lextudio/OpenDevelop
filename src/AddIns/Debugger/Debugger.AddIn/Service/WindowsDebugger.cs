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
using System.Threading.Tasks;
using Debugger.AddIn.Breakpoints;
using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Services
{
	/// <summary>
	/// Debugger service backed by the Debug Adapter Protocol (SharpDbg.Cli), replacing the former
	/// ICorDebug-based engine in Debugger.Core. The class name/namespace and most of the static
	/// surface (Instance, CurrentXxx, RefreshingPads) are kept so the existing Pads/TreeModel/
	/// Visualizers code only has to change how it reads data, not who it talks to.
	/// </summary>
	public class WindowsDebugger : BaseDebuggerService
	{
		public static WindowsDebugger Instance { get; private set; }

		public static DapSession CurrentSession { get; private set; }
		public static DapThreadInfo CurrentThread { get; set; }
		public static DapStackFrameInfo CurrentStackFrame { get; set; }
		static int currentStopSequence;

		public static Action RefreshingPads;

		// Instance-level inspection surface used by DevFlow test actions (tests/OpenDevelop.IntegrationTests,
		// src/Main/SharpDevelop/DevFlow/OpenDevelopDevFlowActions.cs), which drive/inspect the debugger
		// through reflection over whatever IDebuggerService is currently registered, without depending on
		// a concrete engine type. These simply expose the static Current* state as instance members.
		public int CurrentThreadId {
			get { return CurrentThread != null ? CurrentThread.Id : 0; }
		}

		public string CurrentFile {
			get { return CurrentStackFrame != null ? CurrentStackFrame.FilePath : null; }
		}

		public int CurrentLine {
			get { return CurrentStackFrame != null ? CurrentStackFrame.Line : 0; }
		}

		public int CurrentStopSequence {
			get { return currentStopSequence; }
		}

		public Task<IReadOnlyList<DapStackFrameInfo>> GetStackFramesAsync(int threadId)
		{
			if (CurrentSession == null) {
				return Task.FromResult<IReadOnlyList<DapStackFrameInfo>>(Array.Empty<DapStackFrameInfo>());
			}
			return CurrentSession.GetStackFramesAsync(threadId);
		}

		public async Task<IReadOnlyList<DapVariableInfo>> GetLocalsAsync(int frameId)
		{
			if (CurrentSession == null) {
				return Array.Empty<DapVariableInfo>();
			}
			var scopes = await CurrentSession.GetScopesAsync(frameId).ConfigureAwait(false);
			var result = new List<DapVariableInfo>();
			foreach (var scope in scopes) {
				if (scope.Expensive) {
					continue;
				}
				result.AddRange(await CurrentSession.GetVariablesAsync(scope.VariablesReference).ConfigureAwait(false));
			}
			return result;
		}

		public async Task<object> EvaluateAsync(string expression, int frameId)
		{
			if (CurrentSession == null) {
				throw new InvalidOperationException("Debugger is not running");
			}
			var result = await CurrentSession.EvaluateAsync(expression, frameId, "watch").ConfigureAwait(false);
			return new { Name = expression, Value = result.Value, Type = result.Type, result.VariablesReference };
		}

		public Task<IReadOnlyList<DapThreadInfo>> GetThreadsAsync()
		{
			if (CurrentSession == null) {
				return Task.FromResult<IReadOnlyList<DapThreadInfo>>(Array.Empty<DapThreadInfo>());
			}
			return CurrentSession.GetThreadsAsync();
		}

		public Task<IReadOnlyList<DapModuleInfo>> GetModulesAsync()
		{
			if (CurrentSession == null) {
				return Task.FromResult<IReadOnlyList<DapModuleInfo>>(Array.Empty<DapModuleInfo>());
			}
			return CurrentSession.GetModulesAsync();
		}

		public static void RefreshPads()
		{
			RefreshingPads?.Invoke();
		}

		public override bool BreakAtBeginning { get; set; }

		public WindowsDebugger()
		{
			Instance = this;
		}

		string errorDebugging = "${res:XML.MainMenu.DebugMenu.Error.Debugging}";
		string errorNotDebugging = "${res:XML.MainMenu.DebugMenu.Error.NotDebugging}";

		public override bool IsDebugging {
			get { return CurrentSession != null && CurrentSession.IsRunning; }
		}

		public override bool IsAttached {
			get { return false; }
		}

		public override bool IsProcessRunning {
			get { return IsDebugging && !CurrentSession.IsPaused; }
		}

		public override IDebuggerOptions Options {
			get { return DebuggingOptions.Instance; }
		}

		public override bool CanDebug(IProject project)
		{
			return true;
		}

		public override bool Supports(DebuggerFeatures feature)
		{
			switch (feature) {
				case DebuggerFeatures.Attaching:
				case DebuggerFeatures.Detaching:
					return false;
				default:
					return true;
			}
		}

		public override void Start(ProcessStartInfo processStartInfo)
		{
			if (IsDebugging) {
				MessageService.ShowMessage(errorDebugging);
				return;
			}

			OnDebugStarting(EventArgs.Empty);
			StartAsync(processStartInfo).FireAndForget();
		}

		async Task StartAsync(ProcessStartInfo processStartInfo)
		{
			try {
				PrintDebugMessage("> Starting debug adapter...\n");

				CurrentSession = new DapSession();
				CurrentSession.Started += SessionStarted;
				CurrentSession.Stopped += SessionStopped;
				CurrentSession.Continued += SessionContinued;
				CurrentSession.Exited += SessionExited;
				CurrentSession.OutputReceived += PrintDebugMessage;

				string targetPath = processStartInfo.FileName;
				if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) {
					throw new FileNotFoundException("Debug target not found.", targetPath);
				}

				await SyncAllBreakpointsBeforeLaunch();

				bool breakAtBeginning = BreakAtBeginning;
				BreakAtBeginning = false;
				await CurrentSession.StartAsync(targetPath, processStartInfo.WorkingDirectory, breakAtBeginning).ConfigureAwait(false);

				PrintDebugMessage("> Debugging: " + Path.GetFileName(targetPath) + "\n");
			} catch (Exception ex) {
				PrintDebugMessage("ERROR: " + ex.Message + "\n");
				SD.MainThread.InvokeAsyncAndForget(() => OnDebugStopped(EventArgs.Empty));
				Stop();
			}
		}

		void SessionStarted()
		{
			SD.MainThread.InvokeAsyncAndForget(() => {
				OnDebugStarted(EventArgs.Empty);
				OnIsProcessRunningChanged(EventArgs.Empty);
			});
		}

		void SessionStopped(DapStoppedEventArgs e)
		{
			HandleStoppedAsync(e).FireAndForget();
		}

		async Task HandleStoppedAsync(DapStoppedEventArgs e)
		{
			var threads = await CurrentSession.GetThreadsAsync().ConfigureAwait(false);
			CurrentThread = threads.FirstOrDefault(t => t.Id == e.ThreadId) ?? threads.FirstOrDefault();

			var frames = CurrentThread != null
				? await CurrentSession.GetStackFramesAsync(CurrentThread.Id, 0, 1).ConfigureAwait(false)
				: Array.Empty<DapStackFrameInfo>();
			CurrentStackFrame = frames.FirstOrDefault();
			if (CurrentStackFrame != null) {
				CurrentSession.ActiveFrameId = CurrentStackFrame.Id;
			}

			DapExceptionInfo exceptionInfo = e.Reason == "exception"
				? await CurrentSession.GetExceptionInfoAsync(e.ThreadId).ConfigureAwait(false)
				: null;

			System.Threading.Interlocked.Increment(ref currentStopSequence);

			SD.MainThread.InvokeAsyncAndForget(() => {
				OnIsProcessRunningChanged(EventArgs.Empty);

				bool shouldBreak = true;
				if (exceptionInfo != null) {
					shouldBreak = ShowExceptionPrompt(exceptionInfo);
				}

				if (shouldBreak) {
					JumpToCurrentLine();
					RefreshPads();
				} else {
					CurrentSession?.Continue();
				}
			});
		}

		void SessionContinued()
		{
			SD.MainThread.InvokeAsyncAndForget(() => {
				OnIsProcessRunningChanged(EventArgs.Empty);
				RemoveCurrentLineMarker();
				CurrentThread = null;
				CurrentStackFrame = null;
				RefreshPads();
			});
		}

		void SessionExited()
		{
			SD.MainThread.InvokeAsyncAndForget(() => {
				OnDebugStopped(EventArgs.Empty);
				CurrentThread = null;
				CurrentStackFrame = null;
				RefreshPads();
			});
			CurrentSession = null;
		}

		public override void ShowAttachDialog()
		{
			MessageService.ShowMessage("Attach to process is not supported by the DAP debugger.");
		}

		public override void Attach(Process existingProcess)
		{
			throw new NotSupportedException("Attach to process is not supported by the DAP debugger.");
		}

		public override void Detach()
		{
			throw new NotSupportedException("Attach/detach is not supported by the DAP debugger.");
		}

		public override void StartWithoutDebugging(ProcessStartInfo processStartInfo)
		{
			Process.Start(processStartInfo);
		}

		public override void Stop()
		{
			if (!IsDebugging) {
				MessageService.ShowMessage(errorNotDebugging, "${res:XML.MainMenu.DebugMenu.Stop}");
				return;
			}
			CurrentSession.Stop();
		}

		public override void Break()
		{
			CurrentSession?.Break();
		}

		public override void Continue()
		{
			CurrentSession?.Continue();
		}

		public override void StepInto()
		{
			CurrentSession?.StepInto();
		}

		public override void StepOver()
		{
			CurrentSession?.StepOver();
		}

		public override void StepOut()
		{
			CurrentSession?.StepOut();
		}

		public override bool SetInstructionPointer(string filename, int line, int column, bool dryRun)
		{
			// Not supported by the standard DAP surface (would require a SharpDbg-specific extension).
			return false;
		}

		public override void ToggleBreakpointAt(ITextEditor editor, int lineNumber)
		{
			if (editor == null)
				throw new ArgumentNullException("editor");

			if (!SD.BookmarkManager.RemoveBookmarkAt(editor.FileName, lineNumber, b => b is BreakpointBookmark)) {
				SD.BookmarkManager.AddMark(new BreakpointBookmark(), editor.Document, lineNumber);
			}

			if (IsDebugging) {
				SyncBreakpointsForFileAsync(editor.FileName.ToString()).FireAndForget();
			}
		}

		async Task SyncAllBreakpointsBeforeLaunch()
		{
			var byFile = SD.BookmarkManager.Bookmarks
				.OfType<BreakpointBookmark>()
				.Where(b => b.FileName != null)
				.GroupBy(b => b.FileName.ToString(), StringComparer.OrdinalIgnoreCase);

			foreach (var group in byFile) {
				await SyncBreakpointsForFileAsync(group.Key).ConfigureAwait(false);
			}
		}

		async Task SyncBreakpointsForFileAsync(string fileName)
		{
			if (CurrentSession == null) {
				return;
			}

			var bookmarks = SD.MainThread.InvokeIfRequired(() =>
				SD.BookmarkManager.Bookmarks
					.OfType<BreakpointBookmark>()
					.Where(b => b.FileName != null && string.Equals(b.FileName.ToString(), fileName, StringComparison.OrdinalIgnoreCase) && b.IsEnabled)
					.ToList());

			var requested = bookmarks.Select(b => (Line: b.LineNumber, Condition: b.Condition)).ToList();
			var verified = await CurrentSession.SetBreakpointsAsync(fileName, requested).ConfigureAwait(false);

			SD.MainThread.InvokeAsyncAndForget(() => {
				foreach (var bookmark in bookmarks) {
					var match = verified.FirstOrDefault(v => v.Line == bookmark.LineNumber);
					bookmark.IsHealthy = match == null || match.Verified;
				}
			});
		}

		public event EventHandler Initialize;

		public void InitializeService()
		{
			if (Initialize != null) {
				Initialize(this, null);
			}
		}

		public static async Task<DapEvaluateResult> EvaluateAsync(string expression, string context = "watch")
		{
			if (CurrentSession == null) {
				throw new InvalidOperationException("Debugger is not running");
			}
			int? frameId = CurrentStackFrame != null ? CurrentStackFrame.Id : (int?)null;
			return await CurrentSession.EvaluateAsync(expression, frameId, context).ConfigureAwait(false);
		}

		public void JumpToCurrentLine()
		{
			if (CurrentStackFrame == null || string.IsNullOrEmpty(CurrentStackFrame.FilePath))
				return;

			SD.Workbench.MainWindow.Activate();
			RemoveCurrentLineMarker();
			JumpToCurrentLine(CurrentStackFrame.FilePath, CurrentStackFrame.Line, CurrentStackFrame.Column,
				CurrentStackFrame.EndLine > 0 ? CurrentStackFrame.EndLine : CurrentStackFrame.Line,
				CurrentStackFrame.EndColumn > 0 ? CurrentStackFrame.EndColumn : CurrentStackFrame.Column);
		}

		/// <summary>
		/// WinForms hosting was already removed from this repo's SharpDevelop shell ("out of MVP
		/// scope"), so the old WinForms DebuggeeExceptionForm (scrollable stack trace, double-click
		/// to jump to a frame, "stop breaking on this exception type" checkbox) can no longer be
		/// shown. This is a plain three-button prompt instead - known capability gap.
		/// </summary>
		static bool ShowExceptionPrompt(DapExceptionInfo exceptionInfo)
		{
			string caption = exceptionInfo.IsUnhandled
				? StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Title.Unhandled}")
				: StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Title.Handled}");
			string message = string.Format(StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Message}"), exceptionInfo.ExceptionId ?? "Exception")
				+ Environment.NewLine + Environment.NewLine + exceptionInfo.Description
				+ Environment.NewLine + Environment.NewLine + exceptionInfo.StackTrace;

			if (!exceptionInfo.IsUnhandled) {
				string[] handledLabels = {
					StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Break}"),
					StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Continue}"),
					StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Terminate}")
				};
				int handledResult = MessageService.ShowCustomDialog(caption, message, 0, 1, handledLabels);
				if (handledResult == 2) {
					CurrentSession?.Stop();
					return false;
				}
				return handledResult == 0;
			}

			string[] unhandledLabels = {
				StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Break}"),
				StringParser.Parse("${res:MainWindow.Windows.Debug.ExceptionForm.Terminate}")
			};
			int unhandledResult = MessageService.ShowCustomDialog(caption, message, 0, 1, unhandledLabels);
			if (unhandledResult == 1) {
				CurrentSession?.Stop();
				return false;
			}
			return true;
		}

		public override void HandleToolTipRequest(ToolTipRequestEventArgs e)
		{
			if (CurrentSession == null || !CurrentSession.IsPaused)
				return;
			if (CurrentStackFrame == null || !e.InDocument)
				return;

			int offset = e.Editor.Document.PositionToOffset(e.LogicalPosition.Line, e.LogicalPosition.Column);
			// No NRefactory/Roslyn semantic resolution here (batch 3 territory) - just evaluate the
			// identifier text under the cursor as a raw DAP expression. Works for simple locals/
			// fields/properties; member-chain expressions ("a.b.c") are not resolved this way.
			string word = e.Editor.Document.GetWordAt(offset);
			if (string.IsNullOrEmpty(word))
				return;

			try {
				var node = new Debugger.AddIn.TreeModel.ValueNode(null, word, word);
				if (node.ErrorMessage != null)
					return;
				e.SetToolTip(new Debugger.AddIn.Tooltips.DebuggerTooltipControl(node));
			} catch (Exception ex) {
				LoggingService.Warn("HandleToolTipRequest failed", ex);
			}
		}

		public override void RemoveCurrentLineMarker()
		{
			CurrentLineBookmark.Remove();
		}

		public override void JumpToCurrentLine(string sourceFullFilename, int startLine, int startColumn, int endLine, int endColumn)
		{
			if (string.IsNullOrEmpty(sourceFullFilename))
				return;
			IViewContent viewContent = FileService.OpenFile(sourceFullFilename);
			if (viewContent != null) {
				IPositionable positionable = viewContent.GetService<IPositionable>();
				if (positionable != null) {
					positionable.JumpTo(startLine, startColumn);
				}
			}
			CurrentLineBookmark.SetPosition(viewContent, startLine, startColumn, endLine, endColumn);
		}
	}
}
