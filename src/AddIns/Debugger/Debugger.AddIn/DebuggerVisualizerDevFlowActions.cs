using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Debugger.AddIn.Breakpoints;
using Debugger.AddIn.Service.Dap;
using Debugger.AddIn.TreeModel;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Services;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace Debugger.AddIn
{
	/// <summary>
	/// Narrow integration-test probes for the debugger visualizer production path. These remain in
	/// the loaded add-in so they can construct the real <see cref="ValueNode"/> and execute the
	/// same <see cref="IVisualizerCommand"/> selected by <c>VisualizerPicker</c>.
	/// </summary>
	[DevFlowUIThread]
	public static class DebuggerVisualizerDevFlowActions
	{
		[DevFlowAction("od.debug.set-all-breakpoints-enabled", Description = "Enable or disable every breakpoint, resyncing with the adapter")]
		public static string SetAllBreakpointsEnabled(bool enabled)
		{
			var all = SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>().ToList();
			foreach (var bp in all)
				bp.IsEnabled = enabled;
			ResyncBreakpoints(all);
			return JsonSerializer.Serialize(new { success = true, count = all.Count });
		}

		[DevFlowAction("od.debug.delete-all-breakpoints", Description = "Delete every breakpoint")]
		public static string DeleteAllBreakpoints()
		{
			var all = SD.BookmarkManager.Bookmarks.OfType<BreakpointBookmark>().ToList();
			foreach (var bp in all)
				SD.BookmarkManager.RemoveMark(bp);
			ResyncBreakpoints(all);
			return JsonSerializer.Serialize(new { success = true, count = all.Count });
		}

		static void ResyncBreakpoints(IEnumerable<BreakpointBookmark> bookmarks)
		{
			if (SD.Debugger is WindowsDebugger windowsDebugger && windowsDebugger.IsDebugging) {
				foreach (var file in bookmarks.Where(b => b.FileName != null).Select(b => b.FileName.ToString()).Distinct(StringComparer.OrdinalIgnoreCase))
					windowsDebugger.SyncBreakpointsForFileAsync(file).FireAndForget();
			}
		}

		[DevFlowAction("od.debug.show-external-code", Description = "Enable or disable the Call Stack pad's Show External Code filter")]
		public static string ShowExternalCode(bool enabled)
		{
			ICSharpCode.SharpDevelop.Gui.Pads.CallStackPadViewModel.ShowExternalCode = enabled;
			WindowsDebugger.RefreshPads();
			return JsonSerializer.Serialize(new { success = true, showExternalCode = ICSharpCode.SharpDevelop.Gui.Pads.CallStackPadViewModel.ShowExternalCode });
		}

		[DevFlowAction("od.debug.open-frame", Description = "Open a call-stack frame exactly as double-clicking it does (source file, or the decompiled type for a sourceless external frame). frame: 'external' for the first frame without source, or a zero-based index")]
		public static async System.Threading.Tasks.Task<string> OpenFrame(string frame = "external")
		{
			var frames = await ICSharpCode.SharpDevelop.Gui.Pads.CallStackPadViewModel.GetAllFramesAsync();
			DapStackFrameInfo target = string.Equals(frame, "external", StringComparison.OrdinalIgnoreCase)
				? frames.FirstOrDefault(f => string.IsNullOrEmpty(f.FilePath) && f.IsExternalCode)
				: (int.TryParse(frame, out int index) && index >= 0 && index < frames.Count ? frames[index] : null);
			if (target == null)
				return JsonSerializer.Serialize(new { success = false, error = "No matching frame", frames = frames.Select(f => f.Name).ToArray() });

			string refBefore = target.InstructionPointerReference;
			bool resolvedBefore = target.IsResolved;
			var result = await WindowsDebugger.OpenFrameAsync(target);
			var editor = SD.Workbench.ActiveViewContent?.GetService<ITextEditor>();
			int caretLine = editor != null ? editor.Caret.Line : 0;
			return JsonSerializer.Serialize(new {
				success = true,
				frame = result.Frame.Name,
				resolved = result.Resolved,
				instructionPointerBefore = refBefore,
				resolvedBefore,
				instructionPointer = result.Frame.InstructionPointerReference,
				resolvedFilePath = result.Frame.FilePath,
				resolvedLine = result.Frame.Line,
				decompiledType = result.Frame.DecompiledSource?.TypeFullName,
				decompiledAssembly = result.Frame.DecompiledSource?.AssemblyPath,
				methodToken = result.Frame.MethodToken,
				ilOffset = result.Frame.ILOffset,
				decompiledDocument = result.DecompiledDocument,
				activeDocument = SD.Workbench.ActiveViewContent?.PrimaryFileName?.ToString(),
				caretLine,
				lineCount = editor != null ? editor.Document.LineCount : 0,
				caretLineText = editor != null && caretLine > 0 ? editor.Document.GetText(editor.Document.GetLineByNumber(caretLine)) : null
			});
		}

		[DevFlowAction("od.debug.console-state", Description = "Report the Debug Console (Eval) pad: attached highlighting name and its text")]
		public static string ConsoleState()
		{
			var vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			if (vm == null) {
				new ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad();
				vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			}
			return JsonSerializer.Serialize(new {
				hasHighlighting = vm?.HighlightingName != null,
				highlightingName = vm?.HighlightingName,
				text = vm?.ConsoleText
			});
		}

		[DevFlowAction("od.debug.console-eval", Description = "Run a command in the Debug Console (Eval) pad as if entered, and return the console text")]
		public static async System.Threading.Tasks.Task<string> ConsoleEval(string code)
		{
			var vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			if (vm == null) {
				new ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad();
				vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			}
			await vm.EvaluateForTestAsync(code);
			return JsonSerializer.Serialize(new { success = true, text = vm.ConsoleText });
		}

		[DevFlowAction("od.debug.console-completions", Description = "Debug Console dot-completion for an expression (include the trailing '.'): which stack frame (file, line, method) it resolves in and the completion labels - the same path typing '.' takes")]
		public static async System.Threading.Tasks.Task<string> ConsoleCompletions(string expression = "")
		{
			var vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			if (vm == null) {
				new ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad();
				vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			}
			var (frame, result) = await vm.GetCompletionsAsync(expression);
			return JsonSerializer.Serialize(new {
				hasContext = frame != null,
				file = frame?.FilePath,
				line = frame?.Line ?? 0,
				frame = frame?.Name,
				count = result?.Items.Count ?? 0,
				labels = result?.Items.Select(i => i.InsertionText).Take(400).ToArray() ?? new string[0]
			});
		}

		[DevFlowAction("od.debug.console-type-dot", Description = "Type an expression ending in '.' into the Debug Console and run the path typing '.' takes; report whether the console editor now shows a completion popup and its selected item (the popup is closed again)")]
		public static async System.Threading.Tasks.Task<string> ConsoleTypeDot(string expression)
		{
			var vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			if (vm == null) {
				new ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad();
				vm = ICSharpCode.SharpDevelop.Gui.Pads.ConsolePad.ViewModel;
			}
			var (shown, selected) = await vm.TypeDotForTestAsync(expression);
			return JsonSerializer.Serialize(new { popupShown = shown, selected });
		}

		[DevFlowAction("od.debug.add-logpoint", Description = "Add a logpoint (a breakpoint that logs a message with {expr} interpolation and continues) at file:line")]
		public static string AddLogpoint(string filePath, int line, string message)
		{
			var fileName = FileName.Create(filePath);
			var viewContent = SD.FileService.OpenFile(fileName);
			var editor = viewContent?.GetService<ITextEditor>();
			if (editor == null)
				return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + filePath });

			var bookmark = SD.BookmarkManager.GetBookmarks(fileName).OfType<BreakpointBookmark>().FirstOrDefault(b => b.LineNumber == line);
			if (bookmark == null) {
				bookmark = new BreakpointBookmark();
				SD.BookmarkManager.AddMark(bookmark, editor.Document, line);
			}
			bookmark.LogMessage = message;
			if (SD.Debugger is WindowsDebugger windowsDebugger && windowsDebugger.IsDebugging)
				windowsDebugger.SyncBreakpointsForFileAsync(fileName.ToString()).FireAndForget();
			return JsonSerializer.Serialize(new { success = true, file = filePath, line });
		}

		[DevFlowAction("od.debug.visualizer.inspect", Description = "Execute a real paused-local visualizer, inspect its modal WPF window while displayed, and close it for SharpDbg integration testing")]
		public static string InspectVisualizer(string variableName, string visualizerName)
		{
			var node = ValueNode.GetLocalVariables().OfType<ValueNode>()
				.FirstOrDefault(candidate => string.Equals(candidate.Name, variableName, StringComparison.Ordinal));
			if (node == null)
				return JsonSerializer.Serialize(new { success = false, error = "Local variable was not found: " + variableName });

			var commands = node.VisualizerCommands?.ToArray() ?? Array.Empty<IVisualizerCommand>();
			var command = commands.FirstOrDefault(candidate => string.Equals(candidate.ToString(), visualizerName, StringComparison.Ordinal));
			if (command == null)
				return JsonSerializer.Serialize(new {
					success = false,
					error = "Visualizer was not available: " + visualizerName,
					availableVisualizers = commands.Select(candidate => candidate.ToString()).ToArray()
				});

			var preexistingWindows = Application.Current.Windows.OfType<Window>().ToHashSet();
			VisualizerWindowSnapshot snapshot = null;
			var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) {
				Interval = TimeSpan.FromMilliseconds(20)
			};
			timer.Tick += (_, _) => {
				var window = Application.Current.Windows.OfType<Window>()
					.FirstOrDefault(candidate => !preexistingWindows.Contains(candidate) && candidate.IsVisible);
				if (window == null)
					return;
				snapshot = Capture(window);
				timer.Stop();
				window.Close();
			};

			timer.Start();
			try {
				// Must be the real command: each currently uses ShowDialog(), whose nested dispatcher
				// loop lets the timer observe the rendered window before closing it.
				command.Execute();
			} finally {
				timer.Stop();
			}

			object result = snapshot == null
				? new { success = false, error = "Visualizer command returned without showing a window", availableVisualizers = commands.Select(candidate => candidate.ToString()).ToArray() }
				: new {
					success = true,
					availableVisualizers = commands.Select(candidate => candidate.ToString()).ToArray(),
					title = snapshot.Title,
					text = snapshot.Text,
					highlighting = snapshot.Highlighting,
					rows = snapshot.Rows
				};
			return JsonSerializer.Serialize(result);
		}

		static VisualizerWindowSnapshot Capture(Window window)
		{
			var editor = window.FindName("textEditor") as ICSharpCode.AvalonEdit.TextEditor;
			if (editor != null)
				return new VisualizerWindowSnapshot {
					Title = window.Title,
					Text = editor.Text,
					Highlighting = editor.SyntaxHighlighting?.Name ?? string.Empty,
					Rows = Array.Empty<object>()
				};

			var list = window.FindName("listView") as ListView;
			return new VisualizerWindowSnapshot {
				Title = window.Title,
				Text = string.Empty,
				Highlighting = string.Empty,
				Rows = list?.Items.OfType<Service.Dap.DapVariableInfo>()
					.Select(row => (object)new { row.Name, row.Value, row.Type }).ToArray() ?? Array.Empty<object>()
			};
		}

		sealed class VisualizerWindowSnapshot
		{
			public string Title { get; init; }
			public string Text { get; init; }
			public string Highlighting { get; init; }
			public object[] Rows { get; init; }
		}
	}
}
