using System;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace SearchAndReplace
{
	public class Find : AbstractMenuCommand
	{
		public static void SetSearchPattern()
		{
			ITextEditor textArea = SearchManager.GetActiveTextEditor();
			if (textArea != null) {
				string selectedText = textArea.SelectedText;
				if (selectedText != null && selectedText.Length > 0 && !IsMultipleLines(selectedText)) {
					SearchOptions.CurrentFindPattern = selectedText;
				}
			}
		}

		public override void Run()
		{
			SetSearchPattern();
			var dialog = new FindInFilesDialog();
			dialog.Show();
		}

		public static bool IsMultipleLines(string text)
		{
			return text.IndexOf('\n') != -1;
		}
	}

	public class FindNext : AbstractMenuCommand
	{
		public override void Run()
		{
			if (SearchOptions.CurrentFindPattern.Length > 0) {
				var location = new SearchLocation(SearchOptions.SearchTarget, SearchOptions.LookIn, SearchOptions.LookInFiletypes, SearchOptions.IncludeSubdirectories, SearchOptions.SearchTarget == SearchTarget.CurrentSelection ? SearchManager.GetActiveSelection(true) : null);
				var strategy = SearchStrategyFactory.Create(SearchOptions.FindPattern, !SearchOptions.MatchCase, SearchOptions.MatchWholeWord, SearchOptions.SearchMode);
				var result = SearchManager.FindNext(strategy, location);
				SearchManager.SelectResult(result);
			} else {
				Find find = new Find();
				find.Run();
			}
		}
	}

	public class Replace : AbstractMenuCommand
	{
		public override void Run()
		{
			Find.SetSearchPattern();
			var dialog = new FindInFilesDialog();
			dialog.Show();
		}
	}
}
