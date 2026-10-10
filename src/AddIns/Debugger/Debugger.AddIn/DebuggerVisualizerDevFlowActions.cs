using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Debugger.AddIn.Breakpoints;
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
