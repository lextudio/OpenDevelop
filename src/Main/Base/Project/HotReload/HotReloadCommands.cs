using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>
	/// Applies the active document to the running application - or, for an adapter whose framework
	/// watches the files itself, saves it, which is the only thing that can trigger a reload there.
	/// </summary>
	public sealed class ApplyHotReloadCommand : AbstractMenuCommand
	{
		public override bool IsEnabled =>
			HotReloadWorkflow.DecideApplyAction(HotReloadService.CurrentSession) != HotReloadApplyAction.None
			&& SD.Workbench.ActiveViewContent?.PrimaryFileName != null;

		public override void Run()
		{
			var session = HotReloadService.CurrentSession;
			var action = HotReloadWorkflow.DecideApplyAction(session);
			if (action == HotReloadApplyAction.None)
				return;

			var viewContent = SD.Workbench.ActiveViewContent;
			var fileName = viewContent?.PrimaryFileName;
			if (fileName == null)
				return;

			var openedFile = SD.FileService.GetOpenedFile(fileName);
			if (action == HotReloadApplyAction.Save || HotReloadWorkflow.MustSaveBeforeApply(session)) {
				if (openedFile != null && openedFile.IsDirty)
					ICSharpCode.SharpDevelop.Commands.SaveFile.Save(openedFile);
				if (action == HotReloadApplyAction.Save) {
					// Nothing else to do: the framework picks the change up from disk itself.
					HotReloadOutput.Write(session.Framework,
						$"Saved {fileName.GetFileName()}; waiting for {session.Framework} to reload it.");
					return;
				}
			}

			var editor = viewContent.GetService<ITextEditor>();
			if (editor == null) {
				HotReloadOutput.Write(session.Framework, $"{fileName.GetFileName()} has no text editor to apply.");
				return;
			}

			var currentText = editor.Document.Text;
			var knownPrevious = HotReloadDocumentTracker.GetAcceptedText(fileName) ?? LaunchedText(fileName);
			var change = new HotReloadDocumentChange(fileName.ToString(), knownPrevious ?? currentText, currentText,
				editor.Document.Version?.GetHashCode() ?? 0,
				HotReloadWorkflow.ClassifyApply(knownPrevious, currentText, GeneratesXamlCode(session, fileName),
					HotReloadWorkflow.HandlerNamesIn(CodeBehindText(fileName))));

			ApplyAsync(session, change, fileName).FireAndForget();
		}

		// The x:Class code-behind beside the XAML (View.xaml.cs), as the editor has it if it is open: the methods it
		// declares tell an event attribute from a property one. Null when there is none.
		static string CodeBehindText(FileName fileName)
		{
			var codeBehind = FileName.Create(fileName + ".cs");
			var editor = SD.FileService.GetOpenedFile(codeBehind)?.RegisteredViewContents
				.Select(view => view.GetService<ITextEditor>()).FirstOrDefault(e => e != null);
			if (editor != null)
				return editor.Document.Text;
			try {
				return System.IO.File.Exists(codeBehind) ? System.IO.File.ReadAllText(codeBehind) : null;
			} catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) {
				return null;
			}
		}

		// Before the first accepted apply, the application runs what the launch build compiled: the
		// file on disk, unless it was saved after the launch (then it is unknown: null). Falling back to
		// the editor buffer instead made previous and current equal, so the first apply of a session was
		// never classified (a {cs:} edit went out as plain text).
		static string LaunchedText(FileName fileName)
		{
			try {
				if (!System.IO.File.Exists(fileName)
					|| System.IO.File.GetLastWriteTimeUtc(fileName) > HotReloadService.CurrentSessionStartedUtc)
					return null;
				return System.IO.File.ReadAllText(fileName);
			} catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) {
				return null;
			}
		}

		// Only a WPF session can be on the LibreWPF generator; the environment switch is process-wide and
		// must not turn the generator's rules on for a WinUI or Uno project.
		static bool GeneratesXamlCode(IHotReloadSession session, FileName fileName)
		{
			if (session.Framework != "WPF")
				return false;
			var project = SD.ProjectService.FindProjectContainingFile(fileName);
			// The evaluated property sees Directory.Build.props and imported targets, as the build does.
			Func<string, string> evaluated = project is MSBuildBasedProject msbuildProject
				? msbuildProject.GetEvaluatedProperty
				: null;
			return HotReloadWorkflow.UsesGeneratedXaml(project?.FileName, evaluated);
		}

		static async Task ApplyAsync(IHotReloadSession session, HotReloadDocumentChange change, FileName fileName)
		{
			HotReloadApplyResult result;
			try {
				result = await session.ApplyAsync(change, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception ex) {
				result = new HotReloadApplyResult(HotReloadOutcome.Failed, ex.Message, ex.ToString());
			}

			HotReloadOutput.Write(session.Framework, $"{fileName.GetFileName()}: {result.Outcome} - {result.Message}");
			// Only a confirmed apply advances the baseline; otherwise the next attempt would diff
			// against text the running application never accepted.
			if (result.IsSuccess)
				HotReloadDocumentTracker.SetAcceptedText(fileName, change.CurrentText);

			SD.MainThread.InvokeAsyncAndForget(() =>
				SD.StatusBar.SetMessage($"Hot Reload ({session.Framework}): {result.Outcome}"));
		}
	}

	/// <summary>Ends the current Hot Reload session without touching the running application.</summary>
	public sealed class StopHotReloadCommand : AbstractMenuCommand
	{
		public override bool IsEnabled => HotReloadService.CurrentSession != null;

		public override void Run() => HotReloadService.DisposeCurrentSession();
	}

	/// <summary>Enables UI that only makes sense while an application is running with Hot Reload.</summary>
	public sealed class HotReloadSessionActiveConditionEvaluator : IConditionEvaluator
	{
		public bool IsValid(object caller, Condition condition)
			=> HotReloadWorkflow.DecideApplyAction(HotReloadService.CurrentSession) != HotReloadApplyAction.None;
	}

	/// <summary>
	/// The single Hot Reload output channel, matching Visual Studio's one Hot Reload pane.
	/// There is one session at a time - the framework follows from the startup project - so
	/// splitting this per framework would only make somebody asking "why did my reload not work"
	/// guess which pane to open first. The framework is named in the message instead.
	/// </summary>
	public static class HotReloadOutput
	{
		public const string CategoryName = "Hot Reload";

		public static void Write(string framework, string message)
		{
			var line = string.IsNullOrEmpty(framework) ? message : $"[{framework}] {message}";
			LoggingService.Info($"{CategoryName}: {line}");
			try {
				SD.GetService<IOutputPad>()?.GetOrCreateCategory(CategoryName)?.AppendLine(line);
			} catch (Exception ex) {
				// Output is diagnostics, never a reason to fail an apply.
				LoggingService.Warn("Hot Reload: could not write to the output pad: " + ex.Message);
			}
		}
	}

	/// <summary>
	/// Remembers the last text each document was successfully reloaded with, so a failed apply
	/// does not poison the next diff.
	/// </summary>
	public static class HotReloadDocumentTracker
	{
		static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> accepted =
			new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static string GetAcceptedText(FileName fileName)
			=> fileName != null && accepted.TryGetValue(fileName.ToString(), out var text) ? text : null;

		public static void SetAcceptedText(FileName fileName, string text)
		{
			if (fileName != null)
				accepted[fileName.ToString()] = text;
		}

		public static void Clear() => accepted.Clear();
	}
}
