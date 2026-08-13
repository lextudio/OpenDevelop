// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the real plain-text
// Find/Replace engine (SearchManager) end-to-end, distinct from the Roslyn-based symbol
// find-references/rename actions in OpenDevelopDevFlowActions.cs. SearchManager is fully headless
// already (no modal dialog in its execution path - only the optional Find/Replace *settings* UI
// is a dialog), so these call it directly rather than needing a non-modal Show() workaround.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Search;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace SearchAndReplace
{
	[DevFlowUIThread]
	public static class SearchAndReplaceDevFlowActions
	{
		[DevFlowAction("od.search.find", Description = "Run a real plain-text search via SearchManager (not Roslyn symbol search - see od.find-references for that) across a scope: 'solution' (WholeSolution), 'open-files' (AllOpenFiles), or 'current-document' (the active editor's file), returning per-file line/column matches")]
		public static string Find(string pattern, string scope = "solution", bool matchCase = false, bool wholeWord = false, bool useRegex = false, string filter = "*.*")
		{
			try {
				var results = RunFindParallel(pattern, scope, matchCase, wholeWord, useRegex, filter);
				return JsonSerializer.Serialize(new {
					success = true,
					matchCount = results.Sum(f => f.Matches.Count),
					fileCount = results.Count,
					files = results.Select(f => new {
						file = f.FileName.ToString(),
						matches = f.Matches.Select(m => new {
							line = m.StartLocation.Line,
							column = m.StartLocation.Column,
							length = m.Length,
							text = m.DisplayText?.Text
						}).ToArray()
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}
		}

		[DevFlowAction("od.search.replace", Description = "Run a real plain-text find-and-replace via SearchManager.ReplaceAll across the same scopes as od.search.find. Replaces text in each match's already-open (or newly opened) editor buffer - it does NOT save to disk; call od.file.save-all afterwards to persist")]
		public static string Replace(string pattern, string replacement, string scope = "solution", bool matchCase = false, bool wholeWord = false, bool useRegex = false, string filter = "*.*")
		{
			try {
				var results = RunFindParallel(pattern, scope, matchCase, wholeWord, useRegex, filter);
				var affectedFiles = results.Select(f => f.FileName.ToString()).ToArray();
				int replacedCount = SearchManager.ReplaceAll(results, replacement, CancellationToken.None);
				return JsonSerializer.Serialize(new {
					success = true,
					replacedCount,
					affectedFiles
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}
		}

		[DevFlowAction("od.search.show-results", Description = "Run the same real plain-text search as od.search.find, but display the results in the Search Results pad (SearchResultsPad.ShowSearchResults), mirroring the FindReferences flow - unlike od.search.find, which never populates the pad")]
		public static string ShowResults(string pattern, string scope = "solution", bool matchCase = false, bool wholeWord = false, bool useRegex = false, string filter = "*.*")
		{
			try {
				// Uses the parallel SearchManager.FindAllParallel + IObservable overload of
				// ShowSearchResults: this returns immediately and streams results into the pad as
				// they're found, instead of blocking this DevFlow-dispatched call (which runs on the
				// UI thread - see [DevFlowUIThread] below) for the entire duration of a whole-solution
				// scan. Safe now that SD.MainThread.SynchronizationContext is set from the real
				// Dispatcher at startup (WorkbenchStartup.InitializeWorkbench) - ObserveOnUIThread,
				// which both the parallel search's progress reporting and DefaultSearchResult's
				// subscription go through, no longer NREs on a null context.
				var strategy = SearchStrategyFactory.Create(pattern, !matchCase, wholeWord, useRegex ? SearchMode.RegEx : SearchMode.Normal);
				var location = new SearchLocation(ResolveTarget(scope), null, filter, true, null);
				var monitor = SD.StatusBar.CreateProgressMonitor();
				var results = SearchManager.FindAllParallel(strategy, location, monitor);
				SearchManager.ShowSearchResults(pattern, results);
				SearchResultsHost.Current.BringToFront();
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		// Runs a search with SearchManager.FindAllParallel (multi-threaded across files) and blocks
		// until it completes, for actions that need the full match list back synchronously
		// (od.search.find's JSON response, od.search.replace's edits). Blocking here is safe: the
		// parallel search runs on the thread pool via Task.Run, not the Dispatcher, so waiting for
		// it from this [DevFlowUIThread]-dispatched call can't deadlock against it.
		static List<SearchedFile> RunFindParallel(string pattern, string scope, bool matchCase, bool wholeWord, bool useRegex, string filter)
		{
			var strategy = SearchStrategyFactory.Create(pattern, !matchCase, wholeWord, useRegex ? SearchMode.RegEx : SearchMode.Normal);
			var location = new SearchLocation(ResolveTarget(scope), null, filter, true, null);
			var fileList = location.GenerateFileList().ToList();
			var activeEditor = SD.GetActiveViewContentService<ICSharpCode.SharpDevelop.Editor.ITextEditor>();
			ICSharpCode.Core.LoggingService.Debug($"[SearchDiag] scope={scope} fileListCount={fileList.Count} files=[{string.Join(",", fileList)}] activeEditorFileName={activeEditor?.FileName} activeViewContent={SD.Workbench.ActiveViewContent?.GetType().FullName} activeViewContentFile={SD.Workbench.ActiveViewContent?.PrimaryFileName}");
			var monitor = SD.StatusBar.CreateProgressMonitor();
			var found = new List<SearchedFile>();
			using var done = new ManualResetEventSlim(false);
			Exception error = null;
			SearchManager.FindAllParallel(strategy, location, monitor).Subscribe(
				found.Add,
				ex => { error = ex; done.Set(); },
				() => done.Set());
			done.Wait();
			if (error != null)
				throw error;
			return found;
		}

		static SearchTarget ResolveTarget(string scope)
		{
			switch ((scope ?? "solution").ToLowerInvariant()) {
				case "solution": return SearchTarget.WholeSolution;
				case "open-files": return SearchTarget.AllOpenFiles;
				case "current-document": return SearchTarget.CurrentDocument;
				default: throw new ArgumentException("Unknown scope '" + scope + "' - expected 'solution', 'open-files', or 'current-document'");
			}
		}
	}
}
