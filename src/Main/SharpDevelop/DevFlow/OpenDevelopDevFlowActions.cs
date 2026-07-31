// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the app without a native
// file-open dialog (which the WPF-embedded DevFlow agent can't see/control - see
// doc/technotes/csharp-roslyn.md session notes). Static methods on a [DevFlowUIThread]-annotated
// class are auto-discovered by LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.Sdk;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class OpenDevelopDevFlowActions
	{
		[DevFlowAction("od.sdk.list", Description = "List discovered .NET SDKs and which one is currently selected/effective")]
		public static string ListDotNetSdks()
		{
			var discovered = DotNetSdkService.DiscoverSdks();
			var effective = DotNetSdkService.ResolveEffectiveSdk();
			return JsonSerializer.Serialize(new {
				selectedRootPath = DotNetSdkService.SelectedSdkRootPath,
				effective = new { effective.Label, effective.RootPath, effective.HighestSdkVersion, origin = effective.Origin.ToString() },
				sdks = discovered.Select(s => new { s.Label, s.RootPath, s.HighestSdkVersion, origin = s.Origin.ToString() }).ToArray()
			});
		}

		[DevFlowAction("od.sdk.select", Description = "Select a .NET SDK by root path (empty string = use system default)")]
		public static string SelectDotNetSdk(string rootPath)
		{
			DotNetSdkService.SelectedSdkRootPath = rootPath ?? string.Empty;
			var effective = DotNetSdkService.ResolveEffectiveSdk();
			return JsonSerializer.Serialize(new { success = true, effective = new { effective.Label, effective.RootPath, effective.HighestSdkVersion } });
		}

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
				// ProjectDisplayItems (not the raw p.Items snapshot) since SDK-style projects don't
				// list their (implicitly-globbed) Compile items in p.Items at all - only MSBuild
				// evaluation sees them. This is the same source Solution Explorer's own tree uses
				// (SharpDevelopProjectTreeProvider.AddProjectContentNodes), so this action reflects
				// what the UI actually shows.
				files = ICSharpCode.SharpDevelop.Services.ProjectDisplayItems.GetProjectDisplayItems(p)
					.Select(i => i.PhysicalPath)
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

			string syntaxHighlighting = null;
			if (viewContent.PrimaryFileName != null)
			{
				var def = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(viewContent.PrimaryFileName.ToString()));
				syntaxHighlighting = def?.Name;
			}

			return JsonSerializer.Serialize(new {
				active = true,
				typeName,
				isAvalonEdit = typeName == "ICSharpCode.AvalonEdit.AddIn.AvalonEditViewContent",
				fileName = viewContent.PrimaryFileName != null ? viewContent.PrimaryFileName.ToString() : null,
				caretLine = editor != null ? editor.Caret.Line : (int?)null,
				caretColumn = editor != null ? editor.Caret.Column : (int?)null,
				textLength = text?.Length,
				textPreview = text != null ? text.Substring(0, Math.Min(200, text.Length)) : null,
				syntaxHighlighting
			});
		}

		[DevFlowAction("od.parser.status", Description = "Check whether RoslynWorkspaceHelper has a real Roslyn Document for a file - the actual integration point GoToDefinition/completion/etc. use (see doc/technotes/roslyn.md). Note: SD.ParserService.GetCompilationForFile is NOT checked here - for project-owned files it still routes through the old IProjectContent mock, not RoslynParser, regardless of language.")]
		public static string GetParserStatus(string fileName)
		{
			try {
				var document = ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.FindDocument(fileName);
				return JsonSerializer.Serialize(new {
					hasDocument = document != null,
					language = document?.Project.Language
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { hasDocument = false, error = ex.Message });
			}
		}

		[DevFlowAction("od.find-references", Description = "Find all references to the symbol at file:line/column across the whole solution, via Roslyn's SymbolFinder (the modern replacement for the deleted NRefactory find-references engine)")]
		public static async Task<string> FindReferences(string fileName, int line, int column)
		{
			try {
				var location = new ICSharpCode.AvalonEdit.Document.TextLocation(line, column);
				var references = await ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.FindReferencesAt(fileName, location);
				return JsonSerializer.Serialize(new {
					count = references.Count,
					references = references.Select(r => new {
						filePath = r.Document.FilePath,
						line = r.Location.GetLineSpan().StartLinePosition.Line + 1,
						column = r.Location.GetLineSpan().StartLinePosition.Character + 1
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { count = 0, error = ex.Message });
			}
		}

		[DevFlowAction("od.rename-symbol", Description = "Renames the symbol at file:line/column across the whole solution via RoslynWorkspaceHelper.RenameSymbolAsync (bypasses RenameSymbolDialog, which the WPF-embedded DevFlow agent can't drive since it's modal - same reasoning as od.open-solution bypassing the native Open dialog)")]
		public static async Task<string> RenameSymbol(string fileName, int line, int column, string newName, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false)
		{
			try {
				var location = new ICSharpCode.AvalonEdit.Document.TextLocation(line, column);
				var document = ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.FindDocument(fileName);
				var symbol = document != null ? ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.GetSymbolAt(document, location) : null;
				if (symbol == null)
					return JsonSerializer.Serialize(new { success = false, error = "no symbol at location" });
				await ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.RenameSymbolAsync(symbol, newName, renameOverloads, renameInStrings, renameInComments);
				return JsonSerializer.Serialize(new { success = true, oldName = symbol.Name });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.extract-interface", Description = "Extracts an interface from the class at file:line/column via RoslynWorkspaceHelper.ExtractInterfaceAsync (bypasses ExtractInterfaceDialog, which is modal). memberNames is a comma-separated allowlist of member names to include; pass an empty string to include every eligible public instance member")]
		public static async Task<string> ExtractInterface(string fileName, int line, int column, string interfaceName, string newFilePath, bool addInterfaceToClass, string memberNames = "", bool includeComments = false)
		{
			try {
				var location = new ICSharpCode.AvalonEdit.Document.TextLocation(line, column);
				var document = ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.FindDocument(fileName);
				var symbol = document != null ? ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.GetSymbolAt(document, location) : null;
				var classSymbol = symbol as Microsoft.CodeAnalysis.INamedTypeSymbol;
				if (classSymbol == null || classSymbol.TypeKind != Microsoft.CodeAnalysis.TypeKind.Class)
					return JsonSerializer.Serialize(new { success = false, error = "no class symbol at location" });

				var candidates = ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.GetExtractInterfaceCandidateMembers(classSymbol);
				var allowlist = memberNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				var chosen = allowlist.Length == 0 ? candidates : candidates.Where(m => allowlist.Contains(m.Name)).ToArray();
				if (chosen.Count == 0)
					return JsonSerializer.Serialize(new { success = false, error = "no members matched", available = candidates.Select(m => m.Name).ToArray() });

				var path = await ICSharpCode.SharpDevelop.Roslyn.RoslynWorkspaceHelper.ExtractInterfaceAsync(
					classSymbol, interfaceName, chosen, addInterfaceToClass, newFilePath, includeComments);
				return JsonSerializer.Serialize(new { success = true, filePath = path, members = chosen.Select(m => m.Name).ToArray() });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.file.revert-all-dirty", Description = "Reverts every open dirty file to its on-disk content, discarding unsaved changes without prompting - lets a test that intentionally leaves files dirty (e.g. od.rename-symbol) clean up afterwards so a later od.open-solution in the same app session doesn't hit a blocking 'save changes?' dialog")]
		public static string RevertAllDirtyFiles()
		{
			try {
				var reverted = new System.Collections.Generic.List<string>();
				foreach (var openedFile in SD.FileService.OpenedFiles.ToArray()) {
					if (!openedFile.IsDirty)
						continue;
					openedFile.ReloadFromDisk();
					reverted.Add(openedFile.FileName.ToString());
				}
				return JsonSerializer.Serialize(new { success = true, reverted = reverted.ToArray() });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
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

		[DevFlowAction("od.error-list", Description = "Get the Error List pad's actual task collection (TaskService.Tasks) - a separate code path from od.build-solution's own BuildResults: errors reach here asynchronously via UIBuildFeedbackSink.ReportError -> TaskService.Add, dispatched with InvokeAsyncAndForget, so this can lag slightly behind od.build-solution's return and (since od.build-solution bypasses the Build menu command's TaskService.ClearExceptCommentTasks() call) accumulates across multiple builds unless od.error-list.clear is called first")]
		public static string GetErrorList()
		{
			var tasks = TaskService.Tasks.Select(t => new {
				type = t.TaskType.ToString(),
				description = t.Description,
				file = t.FileName?.ToString(),
				line = t.Line,
				column = t.Column
			}).ToArray();
			return JsonSerializer.Serialize(new {
				count = tasks.Length,
				errorCount = tasks.Count(t => t.type == "Error"),
				warningCount = tasks.Count(t => t.type == "Warning"),
				tasks
			});
		}

		[DevFlowAction("od.error-list.clear", Description = "Clear the Error List pad's task collection (TaskService.ClearExceptCommentTasks) - the same reset the Build menu command does before a build, which od.build-solution's direct SD.BuildService.BuildAsync call does not do on its own")]
		public static string ClearErrorList()
		{
			TaskService.ClearExceptCommentTasks();
			return JsonSerializer.Serialize(new { success = true });
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
			return JsonSerializer.Serialize(new { text = GetOutputCategoryText("Debug") });
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

		[DevFlowAction("od.show-pad", Description = "Activate/bring-to-front a workbench pad by title or class name so AvalonDock actually creates and renders its content (needed before inspecting some pads via od.ui.tree, since an un-activated pad's content is never realized)")]
		public static string ShowPad(string padName)
		{
			var pad = FindPad(padName);
			if (pad == null)
				return JsonSerializer.Serialize(new { found = false, padName });
			SD.Workbench.ActivatePad(pad);
			return JsonSerializer.Serialize(new { found = true, title = pad.Title, className = pad.Class });
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

		// --- File Save / Dirty-State Actions ---

		[DevFlowAction("od.file.is-dirty", Description = "Check whether an open file has unsaved changes (OpenedFile.IsDirty)")]
		public static string IsFileDirty(string path)
		{
			var file = SD.FileService.GetOpenedFile(FileName.Create(path));
			return JsonSerializer.Serialize(new { isOpen = file != null, isDirty = file?.IsDirty ?? false });
		}

		[DevFlowAction("od.file.edit-text", Description = "Insert text at the end of an open file's editor document, the same AvalonEdit.Document.Insert call a real keystroke makes, so it dirties the file naturally rather than setting a flag directly")]
		public static string EditFileText(string path, string text)
		{
			var fileName = FileName.Create(path);
			var viewContent = SD.FileService.GetOpenFile(fileName) ?? SD.FileService.OpenFile(fileName);
			var editor = viewContent?.GetService<ITextEditor>();
			if (editor == null)
				return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + path });

			editor.Document.Insert(editor.Document.TextLength, text);
			var file = SD.FileService.GetOpenedFile(fileName);
			return JsonSerializer.Serialize(new { success = true, isDirty = file?.IsDirty ?? false });
		}

		[DevFlowAction("od.file.save", Description = "Save one open file to disk (same as Ctrl+S / File > Save)")]
		public static string SaveOpenFile(string path)
		{
			var file = SD.FileService.GetOpenedFile(FileName.Create(path));
			if (file == null)
				return JsonSerializer.Serialize(new { success = false, error = "File is not open: " + path });

			ICSharpCode.SharpDevelop.Commands.SaveFile.Save(file);
			return JsonSerializer.Serialize(new { success = true, isDirty = file.IsDirty });
		}

		[DevFlowAction("od.file.save-all", Description = "Save all open dirty files to disk (same as File > Save All)")]
		public static string SaveAllOpenFiles()
		{
			ICSharpCode.SharpDevelop.Commands.SaveAllFiles.SaveAll();
			var stillDirty = SD.FileService.OpenedFiles.Where(f => f.IsDirty).Select(f => f.FileName.ToString()).ToArray();
			return JsonSerializer.Serialize(new { success = true, stillDirtyFiles = stillDirty });
		}

		// --- Solution Explorer CRUD Actions ---

		[DevFlowAction("od.solution.add-file", Description = "Create a new file on disk (if it doesn't already exist) and add it to a project as a FileProjectItem, then save the project file - the same effect as Solution Explorer's Add > New/Existing Item")]
		public static string AddFileToProject(string projectName, string filePath, string itemType = "Compile")
		{
			var project = FindProjectByName(projectName);
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No project named '" + projectName + "' in the current solution." });

			if (!File.Exists(filePath))
				File.WriteAllText(filePath, string.Empty);

			WithChangeWatchersDisabled(() => {
				var item = new FileProjectItem(project, new ItemType(itemType)) { FileName = FileName.Create(filePath) };
				project.Items.Add(item);
				project.Save();
			});

			return JsonSerializer.Serialize(new { success = true, files = GetProjectFilesForTesting(project) });
		}

		[DevFlowAction("od.solution.remove-file", Description = "Remove a file's ProjectItem from a project (does not delete the file from disk) and save the project file - the same effect as Solution Explorer's Remove")]
		public static string RemoveFileFromProject(string projectName, string filePath)
		{
			var project = FindProjectByName(projectName);
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No project named '" + projectName + "' in the current solution." });

			var item = project.FindFile(FileName.Create(filePath));
			if (item == null)
				return JsonSerializer.Serialize(new { success = false, error = "File is not part of project '" + projectName + "': " + filePath });

			WithChangeWatchersDisabled(() => {
				project.Items.Remove(item);
				project.Save();
			});

			var files = GetProjectFilesForTesting(project);

			// Removing the ProjectItem element only stops the file from appearing in Solution
			// Explorer if that was the *only* thing including it. For an SDK-style project's
			// default implicit item globs (e.g. Microsoft.NET.Sdk.DefaultItems.props'
			// `<Compile Include="**/*.cs">` / `<None Include="**">`), the glob independently
			// re-matches the same physical file regardless of any explicit item add/remove - so
			// Items.Remove() can succeed (no exception, item gone from the XML) while the file
			// still shows up, because only a `<Compile Remove="...">` / `<None Remove="...">`
			// exclude directive (not implemented here) actually overrides a default glob. Verify
			// against the file's continued presence rather than assuming Remove()'s return value
			// reflects what Solution Explorer will actually display.
			bool stillPresent = files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
			if (stillPresent) {
				return JsonSerializer.Serialize(new {
					success = false,
					error = "'" + filePath + "' is still part of the project after removal - it's likely covered by the SDK's implicit item glob, and excluding glob-covered items (writing a <Remove> directive) isn't supported yet.",
					files
				});
			}

			return JsonSerializer.Serialize(new { success = true, files });
		}

		[DevFlowAction("od.solution.rename-file", Description = "Rename a file on disk and update the matching ProjectItem's Include so Solution Explorer's node follows it - the same effect as Solution Explorer's Rename")]
		public static string RenameProjectFile(string projectName, string oldFilePath, string newFilePath)
		{
			var project = FindProjectByName(projectName);
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No project named '" + projectName + "' in the current solution." });

			var item = project.FindFile(FileName.Create(oldFilePath));
			if (item == null)
				return JsonSerializer.Serialize(new { success = false, error = "File is not part of project '" + projectName + "': " + oldFilePath });

			if (!File.Exists(oldFilePath))
				return JsonSerializer.Serialize(new { success = false, error = "File does not exist: " + oldFilePath });

			WithChangeWatchersDisabled(() => {
				// Doesn't go through SD.FileService.RenameFile - that raises a FileRenamed event
				// whose Solution.FileServiceFileRenamed handler unconditionally tries to update the
				// matching ProjectItem's Include, which throws InvalidOperationException
				// ("Cannot modify an evaluated object originating in an imported file ...") for any
				// file that's only part of the project via the SDK's own implicit item glob (e.g.
				// Microsoft.NET.Sdk.DefaultItems.props' `<None Include="**">`) rather than an explicit
				// <Compile>/<None> entry actually written in the .csproj. A plain File.Move sidesteps
				// that: the glob picks up the new filename on its own, no Include update needed.
				File.Move(oldFilePath, newFilePath);
				try {
					item.FileName = FileName.Create(newFilePath);
					project.Save();
				} catch (InvalidOperationException) {
					// item.FileName's Include write hit the same glob-derived-item restriction -
					// harmless here since the glob already covers the new physical path.
				}
			});

			return JsonSerializer.Serialize(new { success = true, files = GetProjectFilesForTesting(project) });
		}

		[DevFlowAction("od.solution.add-reference", Description = "Add an assembly reference (e.g. 'System.Xml') to a project as a ReferenceProjectItem and save the project file - the same effect as Solution Explorer's Add Reference")]
		public static string AddProjectReference(string projectName, string referenceName)
		{
			var project = FindProjectByName(projectName);
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No project named '" + projectName + "' in the current solution." });

			WithChangeWatchersDisabled(() => {
				var item = new ReferenceProjectItem(project, referenceName);
				project.Items.Add(item);
				project.Save();
			});

			return JsonSerializer.Serialize(new {
				success = true,
				references = project.GetItemsOfType(ItemType.Reference).Select(i => i.Include).ToArray()
			});
		}

		// Our own project.Save()/RenameFile writes make ProjectChangeWatcher think the project file
		// was changed by an external tool, popping a blocking "Solution was altered externally?"
		// MessageBox on the (single, shared) UI thread - which then hangs every later DevFlow action
		// dispatched to that same thread. Suppress the watcher around our own writes so the
		// in-memory project model (already updated via project.Items.Add/Remove) doesn't get
		// second-guessed by a reload prompt for a change we made ourselves.
		static void WithChangeWatchersDisabled(Action action)
		{
			ICSharpCode.SharpDevelop.Workbench.FileChangeWatcher.DisableAllChangeWatchers();
			try {
				action();
			} finally {
				ICSharpCode.SharpDevelop.Workbench.FileChangeWatcher.EnableAllChangeWatchers();
			}
		}

		static IProject FindProjectByName(string projectName)
		{
			var solution = SD.ProjectService.CurrentSolution;
			return solution?.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
		}

		static string[] GetProjectFilesForTesting(IProject project)
		{
			return ICSharpCode.SharpDevelop.Services.ProjectDisplayItems.GetProjectDisplayItems(project)
				.Select(i => i.PhysicalPath)
				.ToArray();
		}

		[DevFlowAction("od.nuget.set-local-feed", Description = "Add (and activate) a local folder as the only NuGet package source, so package search/install doesn't need network access")]
		public static string SetNuGetLocalFeed(string path)
		{
			return InvokePackageManagementDevFlowAction("SetLocalFeed", path);
		}

		[DevFlowAction("od.nuget.open-dialog", Description = "Open the real Manage NuGet Packages dialog")]
		public static string OpenNuGetDialog()
		{
			return InvokePackageManagementDevFlowAction("OpenDialog");
		}

		[DevFlowAction("od.nuget.close-dialog", Description = "Close the Manage NuGet Packages dialog opened by od.nuget.open-dialog")]
		public static string CloseNuGetDialog()
		{
			return InvokePackageManagementDevFlowAction("CloseDialog");
		}

		[DevFlowAction("od.nuget.set-search-text", Description = "Set the search box text in the currently open Manage NuGet Packages dialog's Available tab")]
		public static string SetNuGetSearchText(string text)
		{
			return InvokePackageManagementDevFlowAction("SetSearchText", text);
		}

		[DevFlowAction("od.nuget.search", Description = "Execute the real SearchCommand on the Available tab")]
		public static string SearchNuGetPackages()
		{
			return InvokePackageManagementDevFlowAction("Search");
		}

		[DevFlowAction("od.nuget.status", Description = "Inspect the Available tab's current NuGet package state")]
		public static string GetNuGetStatus()
		{
			return InvokePackageManagementDevFlowAction("Status");
		}

		[DevFlowAction("od.nuget.install", Description = "Execute the real per-row AddPackageCommand for the package with the given Id")]
		public static string InstallNuGetPackage(string packageId)
		{
			return InvokePackageManagementDevFlowAction("Install", packageId);
		}
		
		// --- Unit Testing Actions ---
		
		static Type serviceType;
		static Type itestInterfaceType;
		
		static object GetTestService()
		{
			if (serviceType == null)
				serviceType = Type.GetType("ICSharpCode.UnitTesting.ITestService, UnitTesting", throwOnError: false);
			if (serviceType == null)
				return null;
			return SD.Services.GetService(serviceType);
		}
		
		[DevFlowAction("od.unit-test.status", Description = "Check unit testing service availability and state")]
		public static string GetUnitTestStatus()
		{
			var s = GetTestService();
			if (s == null)
				return JsonSerializer.Serialize(new { available = false });
			var st = s.GetType();
			var os = st.GetProperty("OpenSolution")?.GetValue(s);
			return JsonSerializer.Serialize(new {
				available = true,
				isRunningTests = (bool)(st.GetProperty("IsRunningTests")?.GetValue(s) ?? false),
				solutionAvailable = os != null,
				solutionDisplayName = os?.GetType().GetProperty("DisplayName")?.GetValue(os) as string
			});
		}
		
		[DevFlowAction("od.unit-test.tree", Description = "Get the unit test tree from the current solution")]
		public static string GetUnitTestTree()
		{
			var s = GetTestService();
			if (s == null)
				return JsonSerializer.Serialize(new { available = false, tests = Array.Empty<object>() });
			var os = s.GetType().GetProperty("OpenSolution")?.GetValue(s);
			if (os == null)
				return JsonSerializer.Serialize(new { available = true, tests = Array.Empty<object>() });
			return JsonSerializer.Serialize(new { available = true, tests = new[] { WalkTestNode(os) } });
		}

		[DevFlowAction("od.unit-test.goto", Description = "Execute GoToDefinition for the first discovered unit test whose display name matches or ends with the given name")]
		public static string GoToUnitTestDefinition(string displayName)
		{
			var s = GetTestService();
			if (s == null)
				return JsonSerializer.Serialize(new { success = false, error = "ITestService not available." });
			var os = s.GetType().GetProperty("OpenSolution")?.GetValue(s);
			if (os == null)
				return JsonSerializer.Serialize(new { success = false, error = "No test solution open." });

			var test = FindTestNode(os, displayName);
			if (test == null)
				return JsonSerializer.Serialize(new { success = false, error = "Test not found." });

			var command = test.GetType().GetProperty("GoToDefinition")?.GetValue(test) as System.Windows.Input.ICommand;
			if (command == null || !command.CanExecute(null))
				return JsonSerializer.Serialize(new { success = false, error = "GoToDefinition is not available for this test.", rawJson = GetRawMtpJson(test) });

			command.Execute(null);

			var viewContent = SD.Workbench.ActiveViewContent;
			var editor = viewContent?.GetService<ITextEditor>();
			return JsonSerializer.Serialize(new {
				success = true,
				fileName = viewContent?.PrimaryFileName != null ? viewContent.PrimaryFileName.ToString() : null,
				caretLine = editor != null ? editor.Caret.Line : (int?)null,
				caretColumn = editor != null ? editor.Caret.Column : (int?)null
			});
		}
		
		[DevFlowAction("od.unit-test.run", Description = "Run all tests in the open solution and wait for completion")]
		public static async Task<string> RunUnitTests(int timeoutSeconds = 120)
		{
			var task = TryStartRunAllUnitTests(out var error);
			if (task == null)
				return JsonSerializer.Serialize(new { started = false, error });
			var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
			bool completed = done == task;
			return JsonSerializer.Serialize(new {
				started = true,
				completed,
				timedOut = !completed,
				faulted = task.IsFaulted,
				error = task.IsFaulted ? task.Exception?.InnerException?.Message : null
			});
		}
		
		[DevFlowAction("od.unit-test.run-start", Description = "Start running all tests in the open solution without waiting for completion")]
		public static string StartRunUnitTests()
		{
			var task = TryStartRunAllUnitTests(out var error);
			if (task == null)
				return JsonSerializer.Serialize(new { started = false, error });
			task.ContinueWith(t => LoggingService.Warn("DevFlow unit test run failed", t.Exception),
				TaskContinuationOptions.OnlyOnFaulted);
			return JsonSerializer.Serialize(new { started = true });
		}
		
		static Task TryStartRunAllUnitTests(out string error)
		{
			error = null;
			var s = GetTestService();
			if (s == null) {
				error = "ITestService not available.";
				return null;
			}
			var st = s.GetType();
			var os = st.GetProperty("OpenSolution")?.GetValue(s);
			if (os == null) {
				error = "No test solution open.";
				return null;
			}
			
			if (itestInterfaceType == null)
				itestInterfaceType = Type.GetType("ICSharpCode.UnitTesting.ITest, UnitTesting", throwOnError: false);
			var optType = Type.GetType("ICSharpCode.UnitTesting.TestExecutionOptions, UnitTesting", throwOnError: false);
			var opts = optType != null ? Activator.CreateInstance(optType) : null;
			
			var arr = Array.CreateInstance(itestInterfaceType ?? typeof(object), 1);
			arr.SetValue(os, 0);
			
			var run = st.GetMethods(BindingFlags.Instance | BindingFlags.Public)
				.FirstOrDefault(m => m.Name == "RunTestsAsync" && m.GetParameters().Length == 2);
			if (run == null) {
				error = "RunTestsAsync not found.";
				return null;
			}
			
			return (Task)run.Invoke(s, new object[] { arr, opts });
		}
		
		[DevFlowAction("od.unit-test.debug", Description = "Debug all tests in the open solution (UseDebugger=true) and wait for completion or timeout")]
		public static async Task<string> DebugUnitTests(int timeoutSeconds = 60)
		{
			var s = GetTestService();
			if (s == null)
				return JsonSerializer.Serialize(new { started = false, error = "ITestService not available." });
			var st = s.GetType();
			var os = st.GetProperty("OpenSolution")?.GetValue(s);
			if (os == null)
				return JsonSerializer.Serialize(new { started = false, error = "No test solution open." });

			if (itestInterfaceType == null)
				itestInterfaceType = Type.GetType("ICSharpCode.UnitTesting.ITest, UnitTesting", throwOnError: false);
			var optType = Type.GetType("ICSharpCode.UnitTesting.TestExecutionOptions, UnitTesting", throwOnError: false);
			var opts = optType != null ? Activator.CreateInstance(optType) : null;
			optType?.GetProperty("UseDebugger", BindingFlags.Instance | BindingFlags.Public)?.SetValue(opts, true);

			var arr = Array.CreateInstance(itestInterfaceType ?? typeof(object), 1);
			arr.SetValue(os, 0);

			var run = st.GetMethods(BindingFlags.Instance | BindingFlags.Public)
				.FirstOrDefault(m => m.Name == "RunTestsAsync" && m.GetParameters().Length == 2);
			if (run == null)
				return JsonSerializer.Serialize(new { started = false, error = "RunTestsAsync not found." });

			// Bounded by Task.WhenAny so this action itself can never hang the DevFlow agent even
			// if the underlying debugger session does (see the known "debugger can hang the whole
			// app" issue) -- if it times out, the run task is left running in the background and
			// the caller should expect to need od.debug.stop / an app restart to recover.
			var task = (Task)run.Invoke(s, new object[] { arr, opts });
			var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
			bool completed = done == task;
			return JsonSerializer.Serialize(new {
				started = true,
				completed,
				timedOut = !completed,
				faulted = completed && task.IsFaulted,
				error = completed && task.IsFaulted ? task.Exception?.InnerException?.Message : null,
				isDebugging = SD.Debugger?.IsDebugging ?? false
				});
			}

			[DevFlowAction("od.unit-test.debug-one", Description = "Debug the first discovered unit test whose display name matches or ends with the given name")]
			public static async Task<string> DebugOneUnitTest(string displayName, int timeoutSeconds = 60)
			{
				var s = GetTestService();
				if (s == null)
					return JsonSerializer.Serialize(new { started = false, error = "ITestService not available." });
				var st = s.GetType();
				var os = st.GetProperty("OpenSolution")?.GetValue(s);
				if (os == null)
					return JsonSerializer.Serialize(new { started = false, error = "No test solution open." });

				var test = FindTestNode(os, displayName);
				if (test == null)
					return JsonSerializer.Serialize(new { started = false, error = "Test not found." });

				if (itestInterfaceType == null)
					itestInterfaceType = Type.GetType("ICSharpCode.UnitTesting.ITest, UnitTesting", throwOnError: false);
				var optType = Type.GetType("ICSharpCode.UnitTesting.TestExecutionOptions, UnitTesting", throwOnError: false);
				var opts = optType != null ? Activator.CreateInstance(optType) : null;
				optType?.GetProperty("UseDebugger", BindingFlags.Instance | BindingFlags.Public)?.SetValue(opts, true);

				var arr = Array.CreateInstance(itestInterfaceType ?? typeof(object), 1);
				arr.SetValue(test, 0);

				var run = st.GetMethods(BindingFlags.Instance | BindingFlags.Public)
					.FirstOrDefault(m => m.Name == "RunTestsAsync" && m.GetParameters().Length == 2);
				if (run == null)
					return JsonSerializer.Serialize(new { started = false, error = "RunTestsAsync not found." });

				var padNode = FindUnitTestPadNode(displayName);
				var changedProperties = new List<string>();
				PropertyChangedEventHandler propertyChanged = (sender, e) => changedProperties.Add(e.PropertyName);
				(padNode as INotifyPropertyChanged)?.PropertyChanged += propertyChanged;

				var task = (Task)run.Invoke(s, new object[] { arr, opts });
				var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
				bool completed = done == task;
				(padNode as INotifyPropertyChanged)?.PropertyChanged -= propertyChanged;
				var finalPadNode = FindUnitTestPadNode(displayName);
				var finalTest = FindTestNode(os, displayName);
				return JsonSerializer.Serialize(new {
					started = true,
					displayName = test.GetType().GetProperty("DisplayName")?.GetValue(test) as string,
					completed,
					timedOut = !completed,
					faulted = completed && task.IsFaulted,
					error = completed && task.IsFaulted ? task.Exception?.InnerException?.Message : null,
					isDebugging = SD.Debugger?.IsDebugging ?? false,
					debugOutput = GetOutputCategoryText("Debug"),
					padNodeReplaced = !ReferenceEquals(padNode, finalPadNode),
					padNode = GetUnitTestPadNodeSnapshot(finalPadNode, finalTest, changedProperties)
				});
			}

			[DevFlowAction("od.unit-test.pad-node", Description = "Inspect the actual UnitTestNode rendered by the Unit Tests pad")]
			public static string GetUnitTestPadNode(string displayName)
			{
				var s = GetTestService();
				var os = s?.GetType().GetProperty("OpenSolution")?.GetValue(s);
				var test = os != null ? FindTestNode(os, displayName) : null;
				var padNode = FindUnitTestPadNode(displayName);
				return JsonSerializer.Serialize(GetUnitTestPadNodeSnapshot(padNode, test, Array.Empty<string>()));
			}

			static object FindUnitTestPadNode(string displayName)
			{
				var pad = FindPad("ICSharpCode.UnitTesting.UnitTestsPad");
				if (pad == null)
					return null;
				pad.CreatePad();
				var treeView = pad.PadContent?.GetType().GetProperty("TreeView")?.GetValue(pad.PadContent);
				var root = treeView?.GetType().GetProperty("Root")?.GetValue(treeView);
				return FindSharpTreeNode(root, displayName);
			}

			static object FindSharpTreeNode(object node, string displayName)
			{
				if (node == null)
					return null;
				var model = GetDeclaredProperty(node, "Model");
				var nodeDisplayName = model?.GetType().GetProperty("DisplayName")?.GetValue(model) as string;
				if (string.Equals(nodeDisplayName, displayName, StringComparison.Ordinal)
				    || (nodeDisplayName?.EndsWith("." + displayName, StringComparison.Ordinal) ?? false))
					return node;

				node.GetType().GetMethod("EnsureLazyChildren", BindingFlags.Instance | BindingFlags.Public)?.Invoke(node, null);
				var children = node.GetType().GetProperty("Children")?.GetValue(node) as IEnumerable;
				if (children == null)
					return null;
				foreach (var child in children) {
					var found = FindSharpTreeNode(child, displayName);
					if (found != null)
						return found;
				}
				return null;
			}

			static object GetUnitTestPadNodeSnapshot(object node, object serviceModel, IEnumerable<string> changedProperties)
			{
				if (node == null)
					return new { found = false };
				var model = GetDeclaredProperty(node, "Model");
				var icon = node.GetType().GetProperty("Icon")?.GetValue(node);
				return new {
					found = true,
					nodeType = node.GetType().FullName,
					sameModelInstance = ReferenceEquals(model, serviceModel),
					modelResult = model?.GetType().GetProperty("Result")?.GetValue(model)?.ToString(),
					iconType = icon?.GetType().FullName,
					iconUri = icon?.GetType().GetProperty("UriSource")?.GetValue(icon)?.ToString(),
					changedProperties = changedProperties.ToArray()
				};
			}

			static object GetDeclaredProperty(object instance, string propertyName)
			{
				return instance?.GetType()
					.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
					?.GetValue(instance);
			}

			[DevFlowAction("od.unit-test.output", Description = "Get the UnitTesting output pad text")]
			public static string GetUnitTestOutput()
		{
			var s = GetTestService();
			if (s == null)
				return JsonSerializer.Serialize(new { category = "UnitTesting", text = string.Empty });
			var mv = s.GetType().GetProperty("UnitTestMessageView")?.GetValue(s);
			var text = mv?.GetType().GetProperty("Text")?.GetValue(mv) as string;
			return JsonSerializer.Serialize(new { category = "UnitTesting", text = text ?? string.Empty });
		}

		// --- Code Coverage Actions ---
		// Reflection-based against the CodeCoverage addin assembly, same pattern as the Unit
		// Testing actions above - SharpDevelop.csproj doesn't (and shouldn't) hard-reference an
		// addin assembly just to expose test-automation hooks for it.

		static Type codeCoverageServiceType;
		static Type runAllTestsWithCodeCoverageCommandType;

		static Type GetCodeCoverageServiceType()
		{
			if (codeCoverageServiceType == null)
				codeCoverageServiceType = Type.GetType("ICSharpCode.CodeCoverage.CodeCoverageService, CodeCoverage", throwOnError: false);
			return codeCoverageServiceType;
		}

		static Array GetCodeCoverageResults()
		{
			var serviceType = GetCodeCoverageServiceType();
			if (serviceType == null)
				return null;
			return serviceType.GetProperty("Results", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as Array;
		}

		[DevFlowAction("od.code-coverage.status", Description = "Check whether the CodeCoverage addin is loaded")]
		public static string GetCodeCoverageStatus()
		{
			bool available = GetCodeCoverageServiceType() != null;
			return JsonSerializer.Serialize(new { available });
		}

		[DevFlowAction("od.code-coverage.run", Description = "Run all tests in the open solution with code coverage (AltCover) and wait for results to appear")]
		public static async Task<string> RunCodeCoverage(int timeoutSeconds = 180)
		{
			if (GetCodeCoverageServiceType() == null)
				return JsonSerializer.Serialize(new { started = false, error = "CodeCoverage addin not available." });

			var testService = GetTestService();
			if (testService == null)
				return JsonSerializer.Serialize(new { started = false, error = "ITestService not available." });
			var openSolution = testService.GetType().GetProperty("OpenSolution")?.GetValue(testService);
			if (openSolution == null)
				return JsonSerializer.Serialize(new { started = false, error = "No test solution open." });

			if (runAllTestsWithCodeCoverageCommandType == null)
				runAllTestsWithCodeCoverageCommandType = Type.GetType("ICSharpCode.CodeCoverage.RunAllTestsWithCodeCoverageCommand, CodeCoverage", throwOnError: false);
			if (runAllTestsWithCodeCoverageCommandType == null)
				return JsonSerializer.Serialize(new { started = false, error = "RunAllTestsWithCodeCoverageCommand not found." });

			int resultCountBefore = GetCodeCoverageResults()?.Length ?? 0;

			var command = Activator.CreateInstance(runAllTestsWithCodeCoverageCommandType);
			var run = runAllTestsWithCodeCoverageCommandType.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public);
			// Run() kicks off the Prepare/test-run/Collect sequence and returns immediately (it
			// fire-and-forgets the actual test run) - poll CodeCoverageService.Results for a new
			// entry rather than awaiting a Task, since there's nothing here to await.
			await SD.MainThread.InvokeAsync(() => { run.Invoke(command, null); });

			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
			bool completed = false;
			while (DateTime.UtcNow < deadline) {
				if ((GetCodeCoverageResults()?.Length ?? 0) > resultCountBefore) {
					completed = true;
					break;
				}
				await Task.Delay(200);
			}

			return JsonSerializer.Serialize(new {
				started = true,
				completed,
				timedOut = !completed,
				resultCount = GetCodeCoverageResults()?.Length ?? 0
			});
		}

		[DevFlowAction("od.code-coverage.results", Description = "Get a summary of the current code coverage results (one entry per instrumented module)")]
		public static string GetCodeCoverageResults_()
		{
			var results = GetCodeCoverageResults();
			if (results == null)
				return JsonSerializer.Serialize(new { available = false, modules = Array.Empty<object>() });

			var modules = new List<object>();
			foreach (var result in results) {
				var modulesList = result.GetType().GetProperty("Modules")?.GetValue(result) as System.Collections.IEnumerable;
				if (modulesList == null)
					continue;
				foreach (var module in modulesList) {
					var mt = module.GetType();
					int visited = (int)(mt.GetMethod("GetVisitedCodeLength")?.Invoke(module, null) ?? 0);
					int unvisited = (int)(mt.GetMethod("GetUnvisitedCodeLength")?.Invoke(module, null) ?? 0);
					decimal branchCoverage = (decimal)(mt.GetMethod("GetVisitedBranchCoverage")?.Invoke(module, null) ?? 0m);
					var methods = mt.GetProperty("Methods")?.GetValue(module) as System.Collections.ICollection;
					modules.Add(new {
						name = mt.GetProperty("Name")?.GetValue(module) as string,
						methodCount = methods?.Count ?? 0,
						visitedCodeLength = visited,
						unvisitedCodeLength = unvisited,
						branchCoveragePercent = branchCoverage
					});
				}
			}
			return JsonSerializer.Serialize(new { available = true, modules = modules.ToArray() });
		}

		[DevFlowAction("od.code-coverage.clear", Description = "Clear the current code coverage results")]
		public static string ClearCodeCoverageResults()
		{
			var serviceType = GetCodeCoverageServiceType();
			if (serviceType == null)
				return JsonSerializer.Serialize(new { success = false, error = "CodeCoverage addin not available." });
			serviceType.GetMethod("ClearResults", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
			return JsonSerializer.Serialize(new { success = true });
		}

		static object WalkTestNode(object test)
		{
			if (test == null) return null;
			var t = test.GetType();
			var name = t.GetProperty("DisplayName")?.GetValue(test) as string ?? "";
			var res = t.GetProperty("Result")?.GetValue(test);
			string typeName = "test";
			var ifaces = t.GetInterfaces();
			if (ifaces.Any(i => i.Name == "ITestSolution")) typeName = "solution";
			else if (ifaces.Any(i => i.Name == "ITestProject")) typeName = "project";
			else if (t.Name.Contains("Namespace")) typeName = "namespace";
			else if (t.Name == "TestCollection" || t.Name.Contains("Class")) typeName = "class";
			else if (t.Name.Contains("Method")) typeName = "method";
			var nested = GetMostDerivedProperty(t, "NestedTests")?.GetValue(test) as System.Collections.IEnumerable;
			var kids = new List<object>();
			if (nested != null) { foreach (var c in nested) { var n = WalkTestNode(c); if (n != null) kids.Add(n); } }
			return new { displayName = name, result = res?.ToString() ?? "None", type = typeName, nestedTests = kids };
		}

		static string GetOutputCategoryText(string categoryName)
		{
			var category = ICSharpCode.SharpDevelop.Gui.CompilerMessageView.Instance.MessageCategories
				.FirstOrDefault(c => c.Category == categoryName);
			return category?.Text ?? string.Empty;
		}

		static object FindTestNode(object test, string displayName)
		{
			if (test == null || string.IsNullOrEmpty(displayName))
				return null;
			var t = test.GetType();
			var name = t.GetProperty("DisplayName")?.GetValue(test) as string;
			if (name == displayName || (name != null && name.EndsWith("." + displayName, StringComparison.Ordinal)))
				return test;
			var nested = GetMostDerivedProperty(t, "NestedTests")?.GetValue(test) as System.Collections.IEnumerable;
			if (nested == null)
				return null;
			foreach (var child in nested) {
				var found = FindTestNode(child, displayName);
				if (found != null)
					return found;
			}
			return null;
		}

		static string GetRawMtpJson(object test)
		{
			var node = test.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(test);
			return node?.GetType().GetProperty("RawJson", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(node) as string;
		}

		// Plain Type.GetProperty(name) throws AmbiguousMatchException when a subclass re-declares
		// a property with a covariant return type (e.g. TestNamespace's "NestedTests" narrows
		// TestCollection's) -- walk from the most-derived type down, taking the first
		// DeclaredOnly match, instead of asking the whole hierarchy for one ambiguous "NestedTests".
		static PropertyInfo GetMostDerivedProperty(Type type, string name)
		{
			for (var cur = type; cur != null; cur = cur.BaseType) {
				var p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
				if (p != null) return p;
			}
			return null;
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

		static string InvokePackageManagementDevFlowAction(string methodName, params object[] args)
		{
			try {
				LoadPackageManagementAddInRuntime();
				Type type = Type.GetType("ICSharpCode.PackageManagement.PackageManagementDevFlowActions, PackageManagement");
				if (type == null) {
					return JsonSerializer.Serialize(new {
						success = false,
						error = "PackageManagementDevFlowActions type was not found after loading PackageManagement.addin"
					});
				}
				
				MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
				if (method == null) {
					return JsonSerializer.Serialize(new {
						success = false,
						error = "PackageManagementDevFlowActions." + methodName + " was not found"
					});
				}
				
				return (string)method.Invoke(null, args);
			} catch (TargetInvocationException ex) {
				return JsonSerializer.Serialize(new {
					success = false,
					error = ex.InnerException?.Message ?? ex.Message
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new {
					success = false,
					error = ex.Message
				});
			}
		}

		static void LoadPackageManagementAddInRuntime()
		{
			var addIn = SD.AddInTree.AddIns.FirstOrDefault(a =>
				a.FileName != null &&
				a.FileName.EndsWith("PackageManagement.addin", StringComparison.OrdinalIgnoreCase));
			addIn?.LoadRuntimeAssemblies();
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
			// Calling an async method always returns a non-null Task immediately, even when the
			// method-not-found case inside it resolves to a null *result* -- so callers checking
			// "startTask != null" to decide whether the reflected method existed always saw a
			// non-null Task and took the wrong branch (e.g. od.debug.start's StartDebug never
			// fell through to project.Start(true), because reflection could never find
			// "StartProjectAsync" on the debugger service, yet this always returned non-null).
			// Do the existence check synchronously, before entering the async wrapper.
			MethodInfo method = target?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
			if (method == null)
				return null;
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

		[DevFlowAction("od.xml-tree-status", Description = "Check the XML Tree View secondary view for the active workbench window. Returns {found, title, viewType} JSON.")]
		public static string GetXmlTreeStatus()
		{
			var window = SD.Workbench.ActiveWorkbenchWindow;
			var treeView = window?.ViewContents
				.FirstOrDefault(vc => vc.GetType().FullName == "ICSharpCode.XmlEditor.XmlTreeView");

			if (treeView is null)
			{
				return JsonSerializer.Serialize(new
				{
					found = false,
					activeFile = window?.ActiveViewContent?.PrimaryFileName?.ToString(),
					views = window?.ViewContents.Select(vc => vc.GetType().FullName).ToArray() ?? Array.Empty<string>()
				});
			}

			return JsonSerializer.Serialize(new
			{
				found = true,
				title = treeView.TabPageText,
				viewType = treeView.GetType().FullName,
				primaryFile = treeView.PrimaryFileName?.ToString(),
				isDirty = treeView.IsDirty
			});
		}

		// od.xaml-outline.status lives in XamlBinding's own XamlOutlineDevFlowActions.cs
		// (ICSharpCode.XamlBinding.XamlOutlineDevFlowActions), next to the real
		// XamlOutlineContentHost/XamlOutlineLspProvider it inspects - a stale duplicate
		// registration here (targeting a DockPanel+SharpTreeView shape that was never actually
		// built) used to win the action-name collision since this assembly loads before
		// XamlBinding.dll, silently shadowing the real implementation.
	}
}
