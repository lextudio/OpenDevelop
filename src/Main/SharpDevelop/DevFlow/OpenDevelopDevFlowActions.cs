// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the app without a native
// file-open dialog (which the WPF-embedded DevFlow agent can't see/control - see
// doc/technotes/csharp-roslyn.md session notes). Static methods on a [DevFlowUIThread]-annotated
// class are auto-discovered by LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class OpenDevelopDevFlowActions
	{
		[DevFlowAction("od.open-solution", Description = "Open a solution or project file by path (bypasses the native Open dialog)")]
		public static string OpenSolution(string path)
		{
			try {
				bool ok = SD.ProjectService.OpenSolutionOrProject(FileName.Create(path));
				return JsonSerializer.Serialize(new { success = ok, currentSolution = SD.ProjectService.CurrentSolution?.FileName?.ToString() });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.solution-tree", Description = "Get the current solution's project/file tree, as seen by Solution Explorer")]
		public static string GetSolutionTree()
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null)
				return JsonSerializer.Serialize(new { solutionFile = (string)null, projects = Array.Empty<object>() });

			var projects = solution.Projects.Select(p => new {
				name = p.Name,
				fileName = p.FileName != null ? p.FileName.ToString() : null,
				files = p.Items.OfType<FileProjectItem>()
					.Where(i => i.ItemType == ItemType.Compile)
					.Select(i => i.FileName.ToString())
					.ToArray()
			}).ToArray();

			return JsonSerializer.Serialize(new {
				solutionFile = solution.FileName != null ? solution.FileName.ToString() : null,
				projects
			});
		}

		[DevFlowAction("od.open-file", Description = "Open a file in the editor by path (bypasses the native Open dialog)")]
		public static string OpenFile(string path)
		{
			var viewContent = SD.FileService.OpenFile(FileName.Create(path));
			return JsonSerializer.Serialize(new {
				opened = viewContent != null,
				viewContentType = viewContent != null ? viewContent.GetType().FullName : null
			});
		}

		[DevFlowAction("od.active-view", Description = "Inspect the active view content - used to confirm AvalonEdit rendered an opened file")]
		public static string GetActiveView()
		{
			var viewContent = SD.Workbench.ActiveViewContent;
			if (viewContent == null)
				return JsonSerializer.Serialize(new { active = false });

			string typeName = viewContent.GetType().FullName;
			var editor = viewContent.GetService<ITextEditor>();
			string text = editor != null ? editor.Document.Text : null;

			return JsonSerializer.Serialize(new {
				active = true,
				typeName,
				isAvalonEdit = typeName == "ICSharpCode.AvalonEdit.AddIn.AvalonEditViewContent",
				fileName = viewContent.PrimaryFileName != null ? viewContent.PrimaryFileName.ToString() : null,
				textLength = text?.Length,
				textPreview = text != null ? text.Substring(0, Math.Min(200, text.Length)) : null
			});
		}

		[DevFlowAction("od.build-solution", Description = "Build the current solution (or a single project by name) and return error/warning counts plus the individual diagnostics")]
		public static async Task<string> BuildSolution(string projectName = null)
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null)
				return JsonSerializer.Serialize(new { success = false, error = "No solution is open." });

			var options = new BuildOptions(BuildTarget.Build);

			BuildResults results;
			if (string.IsNullOrEmpty(projectName)) {
				results = await SD.BuildService.BuildAsync(solution, options);
			} else {
				var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
				if (project == null)
					return JsonSerializer.Serialize(new { success = false, error = $"No project named '{projectName}' in the current solution." });
				results = await SD.BuildService.BuildAsync(project, options);
			}

			return JsonSerializer.Serialize(new {
				success = true,
				result = results.Result.ToString(),
				errorCount = results.ErrorCount,
				warningCount = results.WarningCount,
				messageCount = results.MessageCount,
				diagnostics = results.Errors.Select(e => new {
					isWarning = e.IsWarning,
					isMessage = e.IsMessage,
					fileName = e.FileName,
					line = e.Line,
					column = e.Column,
					errorCode = e.ErrorCode,
					errorText = e.ErrorText
				}).ToArray()
			});
		}

		[DevFlowAction("od.output-text", Description = "Get the full accumulated text of an Output pad category (default: 'Build')")]
		public static string GetOutputText(string category = "Build")
		{
			IOutputCategory outputCategory = string.Equals(category, "Build", StringComparison.OrdinalIgnoreCase)
				? SD.OutputPad.BuildCategory
				: SD.OutputPad.GetOrCreateCategory(category);

			string text = (outputCategory as MessageViewCategory)?.Text;
			return JsonSerializer.Serialize(new {
				category,
				text = text ?? string.Empty
			});
		}
		
		[DevFlowAction("od.addins", Description = "List loaded addins for diagnostics")]
		public static string GetLoadedAddIns()
		{
			return JsonSerializer.Serialize(new {
				addins = SD.AddInTree.AddIns.Select(a => new {
					name = a.Name,
					fileName = a.FileName,
					enabled = a.Enabled,
					action = a.Action.ToString()
				}).ToArray()
			});
		}
		
		[DevFlowAction("od.debug.output", Description = "Read the Debug output category text (diagnostics)")]
		public static string GetDebugOutput()
		{
			var category = ICSharpCode.SharpDevelop.Gui.CompilerMessageView.Instance.MessageCategories
				.FirstOrDefault(c => c.Category == "Debug");
			return JsonSerializer.Serialize(new { text = category?.Text ?? string.Empty });
		}

		[DevFlowAction("od.debug.service-info", Description = "Inspect debugger service registration and state")]
		public static string GetDebuggerServiceInfo()
		{
			var debugger = SD.GetService<IDebuggerService>();
			return JsonSerializer.Serialize(new {
				available = debugger != null,
				typeName = debugger?.GetType().FullName,
				isDebugging = debugger?.IsDebugging ?? false,
				isProcessRunning = debugger?.IsProcessRunning ?? false
			});
		}
		
		[DevFlowAction("od.debug.clear-breakpoints", Description = "Clear debugger breakpoints for one file or all files")]
		public static string ClearDebugBreakpoints(string filePath = null)
		{
			if (string.IsNullOrEmpty(filePath)) {
				SD.BookmarkManager.RemoveAll(IsBreakpointBookmark);
			} else {
				var fileName = FileName.Create(filePath);
				SD.BookmarkManager.RemoveAll(b => IsBreakpointBookmark(b) && b.FileName == fileName);
			}
			return JsonSerializer.Serialize(new { success = true, remaining = GetAllBreakpointDtos() });
		}
		
		[DevFlowAction("od.debug.set-breakpoint", Description = "Set a breakpoint at file:line and return breakpoint lines for that file")]
		public static string SetDebugBreakpoint(string filePath, int line)
		{
			var fileName = FileName.Create(filePath);
			var viewContent = SD.FileService.OpenFile(fileName);
			var editor = viewContent?.GetService<ITextEditor>();
			if (editor == null) {
				return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + filePath });
			}
			
			bool exists = SD.BookmarkManager.GetBookmarks(fileName)
				.Where(IsBreakpointBookmark)
				.Any(b => b.LineNumber == line);
			if (!exists) {
				SD.Debugger.ToggleBreakpointAt(editor, line);
			}
			
			return JsonSerializer.Serialize(new {
				success = true,
				file = filePath,
				lines = GetBreakpointLines(fileName)
			});
		}
		
		[DevFlowAction("od.debug.start", Description = "Start debugging a project and optionally wait for a stopped event")]
		public static async Task<string> StartDebug(string projectPath = null, bool waitForStop = true, int timeoutSeconds = 30)
		{
			var debugger = SD.Debugger;
			if (debugger == null) {
				return JsonSerializer.Serialize(new { started = false, error = "Debugger service not available." });
			}
			if (debugger.IsDebugging) {
				return JsonSerializer.Serialize(new { started = false, error = "Already debugging." });
			}
			
			if (!string.IsNullOrEmpty(projectPath)) {
				SD.ProjectService.OpenSolutionOrProject(FileName.Create(projectPath));
			}
			
			int stopSequence = GetIntProperty(debugger, "CurrentStopSequence");
			var stopTask = waitForStop ? WaitForStopAsync(debugger, timeoutSeconds, stopSequence) : Task.FromResult(true);
			Task startTask = InvokeTask(debugger, "StartProjectAsync", projectPath);
			if (startTask != null) {
				await startTask;
			} else {
				var project = ResolveProject(projectPath);
				if (project == null) {
					return JsonSerializer.Serialize(new { started = false, error = "No project to debug." });
				}
				project.Start(true);
			}
			
			bool stopped = waitForStop && await stopTask;
			return JsonSerializer.Serialize(new {
				started = debugger.IsDebugging || stopped,
				stopped,
				isDebugging = debugger.IsDebugging,
				isProcessRunning = debugger.IsProcessRunning,
				threadId = GetIntProperty(debugger, "CurrentThreadId"),
				currentFile = GetStringProperty(debugger, "CurrentFile"),
				currentLine = GetIntProperty(debugger, "CurrentLine")
			});
		}
		
		[DevFlowAction("od.debug.stop", Description = "Stop the current debug session")]
		public static string StopDebug()
		{
			SD.Debugger.Stop();
			return JsonSerializer.Serialize(new {
				success = true,
				isDebugging = SD.Debugger.IsDebugging,
				isProcessRunning = SD.Debugger.IsProcessRunning
			});
		}
		
		[DevFlowAction("od.debug.continue", Description = "Continue debugging and optionally wait for the next stop")]
		public static async Task<string> ContinueDebug(bool waitForStop = true, int timeoutSeconds = 30)
		{
			int stopSequence = GetIntProperty(SD.Debugger, "CurrentStopSequence");
			var wait = waitForStop ? WaitForStopAsync(SD.Debugger, timeoutSeconds, stopSequence) : Task.FromResult(true);
			SD.Debugger.Continue();
			bool stopped = waitForStop && await wait;
			return SerializeDebugLocation(stopped);
		}
		
		[DevFlowAction("od.debug.step-over", Description = "Step over and wait for the next stop")]
		public static async Task<string> StepOverDebug(int timeoutSeconds = 30)
		{
			int stopSequence = GetIntProperty(SD.Debugger, "CurrentStopSequence");
			var wait = WaitForStopAsync(SD.Debugger, timeoutSeconds, stopSequence);
			SD.Debugger.StepOver();
			return SerializeDebugLocation(await wait);
		}
		
		[DevFlowAction("od.debug.step-into", Description = "Step into and wait for the next stop")]
		public static async Task<string> StepIntoDebug(int timeoutSeconds = 30)
		{
			int stopSequence = GetIntProperty(SD.Debugger, "CurrentStopSequence");
			var wait = WaitForStopAsync(SD.Debugger, timeoutSeconds, stopSequence);
			SD.Debugger.StepInto();
			return SerializeDebugLocation(await wait);
		}
		
		[DevFlowAction("od.debug.step-out", Description = "Step out and wait for the next stop")]
		public static async Task<string> StepOutDebug(int timeoutSeconds = 30)
		{
			int stopSequence = GetIntProperty(SD.Debugger, "CurrentStopSequence");
			var wait = WaitForStopAsync(SD.Debugger, timeoutSeconds, stopSequence);
			SD.Debugger.StepOut();
			return SerializeDebugLocation(await wait);
		}
		
		[DevFlowAction("od.debug.call-stack", Description = "Return current call stack")]
		public static async Task<string> GetDebugCallStack()
		{
			var debugger = SD.Debugger;
			int threadId = GetIntProperty(debugger, "CurrentThreadId");
			var frames = await InvokeEnumerableTask(debugger, "GetStackFramesAsync", threadId);
			return JsonSerializer.Serialize(frames.Select(ToPropertyDictionary).ToArray());
		}
		
		[DevFlowAction("od.debug.locals", Description = "Return locals for the top stack frame")]
		public static async Task<string> GetDebugLocals()
		{
			var debugger = SD.Debugger;
			int threadId = GetIntProperty(debugger, "CurrentThreadId");
			var frames = (await InvokeEnumerableTask(debugger, "GetStackFramesAsync", threadId)).ToList();
			int frameId = frames.Count > 0 ? GetIntProperty(frames[0], "Id") : 0;
			var locals = await InvokeEnumerableTask(debugger, "GetLocalsAsync", frameId);
			return JsonSerializer.Serialize(locals.Select(ToPropertyDictionary).ToArray());
		}
		
		[DevFlowAction("od.debug.evaluate", Description = "Evaluate an expression in the top stack frame")]
		public static async Task<string> EvaluateDebugExpression(string expression)
		{
			var debugger = SD.Debugger;
			int threadId = GetIntProperty(debugger, "CurrentThreadId");
			var frames = (await InvokeEnumerableTask(debugger, "GetStackFramesAsync", threadId)).ToList();
			int frameId = frames.Count > 0 ? GetIntProperty(frames[0], "Id") : 0;
			object result = await InvokeObjectTask(debugger, "EvaluateAsync", expression, frameId);
			return JsonSerializer.Serialize(result != null ? ToPropertyDictionary(result) : new Dictionary<string, object>());
		}
		
		[DevFlowAction("od.debug.threads", Description = "Return debugger threads")]
		public static async Task<string> GetDebugThreads()
		{
			var threads = await InvokeEnumerableTask(SD.Debugger, "GetThreadsAsync");
			return JsonSerializer.Serialize(threads.Select(ToPropertyDictionary).ToArray());
		}
		
		[DevFlowAction("od.debug.modules", Description = "Return debugger modules")]
		public static async Task<string> GetDebugModules()
		{
			var modules = await InvokeEnumerableTask(SD.Debugger, "GetModulesAsync");
			return JsonSerializer.Serialize(modules.Select(ToPropertyDictionary).ToArray());
		}

		[DevFlowAction("od.pads", Description = "List registered workbench pads")]
		public static string GetPads()
		{
			return JsonSerializer.Serialize(SD.Workbench.PadContentCollection.Select(p => new {
				title = p.Title,
				category = p.Category,
				className = p.Class,
				defaultPosition = p.DefaultPosition.ToString()
			}).ToArray());
		}
		
		[DevFlowAction("od.debug.pad-snapshot", Description = "Create a debugger pad and return its current snapshot")]
		public static async Task<string> GetDebugPadSnapshot(string padName)
		{
			var pad = FindPad(padName);
			if (pad == null) {
				return JsonSerializer.Serialize(new { found = false, padName, items = Array.Empty<object>() });
			}
			pad.CreatePad();
			var content = pad.PadContent;
			var items = await InvokeEnumerableTask(content, "GetSnapshotAsync");
			return JsonSerializer.Serialize(new {
				found = true,
				title = pad.Title,
				category = pad.Category,
				className = pad.Class,
				items = items.Select(ToPropertyDictionary).ToArray()
			});
		}
		
		static IProject ResolveProject(string projectPath)
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null) {
				return SD.ProjectService.CurrentProject;
			}
			if (!string.IsNullOrEmpty(projectPath)) {
				var project = solution.Projects.FirstOrDefault(p => FileUtility.IsEqualFileName(p.FileName, projectPath));
				if (project != null) {
					return project;
				}
			}
			return solution.StartupProject ?? SD.ProjectService.CurrentProject;
		}
		
		static int[] GetBreakpointLines(FileName fileName)
		{
			return SD.BookmarkManager.GetBookmarks(fileName)
				.Where(IsBreakpointBookmark)
				.Select(b => b.LineNumber)
				.OrderBy(l => l)
				.ToArray();
		}

		static object[] GetAllBreakpointDtos()
		{
			return SD.BookmarkManager.Bookmarks
				.Where(IsBreakpointBookmark)
				.Select(b => new { file = b.FileName?.ToString(), line = b.LineNumber })
				.Cast<object>()
				.ToArray();
		}

		/// <summary>
		/// Matches breakpoints without a compile-time reference to Debugger.AddIn's
		/// BreakpointBookmark (an addin type the main app doesn't/shouldn't reference directly -
		/// it's a sibling of the sealed Bookmark class, not a subtype, so OfType&lt;Bookmark&gt;()
		/// never matches real breakpoints).
		/// </summary>
		static bool IsBreakpointBookmark(SDBookmark bookmark)
		{
			return bookmark != null && bookmark.GetType().Name == "BreakpointBookmark";
		}

		static PadDescriptor FindPad(string padName)
		{
			return SD.Workbench.PadContentCollection.FirstOrDefault(p =>
				string.Equals(p.Class, padName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(p.Title, padName, StringComparison.OrdinalIgnoreCase)
				|| p.Class.EndsWith("." + padName, StringComparison.OrdinalIgnoreCase));
		}
		
		static async Task<bool> WaitForStopAsync(IDebuggerService debugger, int timeoutSeconds, int previousStopSequence)
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
			while (DateTime.UtcNow < deadline) {
				if (debugger.IsDebugging
				    && !debugger.IsProcessRunning
				    && GetIntProperty(debugger, "CurrentLine") > 0
				    && GetIntProperty(debugger, "CurrentStopSequence") > previousStopSequence) {
					return true;
				}
				await Task.Delay(200);
			}
			return false;
		}
		
		static string SerializeDebugLocation(bool stopped)
		{
			var debugger = SD.Debugger;
			return JsonSerializer.Serialize(new {
				stopped,
				isDebugging = debugger.IsDebugging,
				isProcessRunning = debugger.IsProcessRunning,
				threadId = GetIntProperty(debugger, "CurrentThreadId"),
				currentFile = GetStringProperty(debugger, "CurrentFile"),
				currentLine = GetIntProperty(debugger, "CurrentLine")
			});
		}
		
		static Task InvokeTask(object target, string methodName, params object[] args)
		{
			return InvokeObjectTask(target, methodName, args) as Task;
		}
		
		static async Task<object> InvokeObjectTask(object target, string methodName, params object[] args)
		{
			MethodInfo method = target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
			if (method == null) {
				return null;
			}
			object invocation = method.Invoke(target, args);
			if (invocation is Task task) {
				await task;
				Type taskType = task.GetType();
				if (taskType.IsGenericType) {
					return taskType.GetProperty("Result")?.GetValue(task);
				}
				return task;
			}
			return invocation;
		}
		
		static async Task<IEnumerable<object>> InvokeEnumerableTask(object target, string methodName, params object[] args)
		{
			object result = await InvokeObjectTask(target, methodName, args);
			return result is IEnumerable enumerable
				? enumerable.Cast<object>().ToArray()
				: Array.Empty<object>();
		}
		
		static Dictionary<string, object> ToPropertyDictionary(object value)
		{
			return value.GetType()
				.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(p => p.GetIndexParameters().Length == 0)
				.ToDictionary(p => p.Name, p => p.GetValue(value));
		}
		
		static int GetIntProperty(object value, string propertyName)
		{
			object result = value?.GetType().GetProperty(propertyName)?.GetValue(value);
			return result is int i ? i : 0;
		}
		
		static string GetStringProperty(object value, string propertyName)
		{
			return value?.GetType().GetProperty(propertyName)?.GetValue(value) as string;
		}
	}
}
