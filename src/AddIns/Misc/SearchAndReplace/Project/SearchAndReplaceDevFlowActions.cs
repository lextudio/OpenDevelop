// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the real plain-text
// Find/Replace engine (SearchManager) end-to-end, distinct from the Roslyn-based symbol
// find-references/rename actions in OpenDevelopDevFlowActions.cs. SearchManager is fully headless
// already (no modal dialog in its execution path - only the optional Find/Replace *settings* UI
// is a dialog), so these call it directly rather than needing a non-modal Show() workaround.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;

using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Core;
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
				var results = RunFind(pattern, scope, matchCase, wholeWord, useRegex, filter);
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
				var results = RunFind(pattern, scope, matchCase, wholeWord, useRegex, filter);
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
				// NOTE: deliberately uses the sequential SearchManager.FindAll path (like the real
				// Find-in-Files dialog) instead of FindAllParallel: SearchRun's parallel path pushes
				// results through ObserveOnUIThread, whose SD.MainThread.SynchronizationContext is
				// null in this host, so OnNext/OnError NRE on the posted delegate and the results
				// never reach the pad (pre-existing engine bug; nothing in the app uses
				// FindAllParallel). The IObservable overload of SearchResultsPad.ShowSearchResults
				// subscribes through the same broken path, so feed the plain match-list overload -
				// the exact call FindReferencesCommand uses.
				var results = RunFind(pattern, scope, matchCase, wholeWord, useRegex, filter);
				var matches = results.SelectMany(f => f.Matches).ToList();
				string title = StringParser.Parse("${res:MainWindow.Windows.SearchResultPanel.OccurrencesOf}",
				                                  new StringTagPair("Pattern", pattern));
				SearchResultsPad.Instance.ShowSearchResults(title, matches);
				SearchResultsPad.Instance.BringToFront();
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static System.Collections.Generic.List<SearchedFile> RunFind(string pattern, string scope, bool matchCase, bool wholeWord, bool useRegex, string filter)
		{
			var strategy = SearchStrategyFactory.Create(pattern, !matchCase, wholeWord, useRegex ? SearchMode.RegEx : SearchMode.Normal);
			var target = ResolveTarget(scope);
			var location = new SearchLocation(target, null, filter, true, null);
			// FindAll's underlying SearchRun takes ownership of the monitor and disposes it once
			// enumeration completes - don't also wrap this in `using` (double-dispose).
			var monitor = SD.StatusBar.CreateProgressMonitor();
			return SearchManager.FindAll(strategy, location, monitor).ToList();
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
