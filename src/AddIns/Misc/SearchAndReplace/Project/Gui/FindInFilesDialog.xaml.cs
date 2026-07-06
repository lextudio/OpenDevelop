using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

namespace SearchAndReplace
{
	public partial class FindInFilesDialog : Window
	{
		public FindInFilesDialog()
		{
			InitializeComponent();

			searchPatternCombo.ItemsSource = SearchOptions.FindPatterns;
			if (SearchOptions.CurrentFindPattern.Length > 0)
				searchPatternCombo.Text = SearchOptions.CurrentFindPattern;
			else
				searchPatternCombo.Text = GetSelectedText();

			matchCaseCheckBox.IsChecked = SearchOptions.MatchCase;
			matchWholeWordCheckBox.IsChecked = SearchOptions.MatchWholeWord;
			lookInCombo.SelectedIndex = (int)SearchOptions.SearchTarget;
			fileTypesTextBox.Text = SearchOptions.LookInFiletypes;
		}

		string GetSelectedText()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor != null)
			{
				string text = editor.SelectedText;
				if (!string.IsNullOrEmpty(text) && text.IndexOf('\n') == -1)
					return text;
			}
			return "";
		}

		void SaveOptions()
		{
			SearchOptions.CurrentFindPattern = searchPatternCombo.Text;
			SearchOptions.FindPattern = searchPatternCombo.Text;
			SearchOptions.MatchCase = matchCaseCheckBox.IsChecked == true;
			SearchOptions.MatchWholeWord = matchWholeWordCheckBox.IsChecked == true;
			SearchOptions.SearchTarget = (SearchTarget)lookInCombo.SelectedIndex;
			SearchOptions.LookInFiletypes = fileTypesTextBox.Text;
		}

		void FindAllClick(object sender, RoutedEventArgs e)
		{
			SaveOptions();
			SearchOptions.FindPattern = searchPatternCombo.Text;

			var location = new SearchLocation(
				SearchOptions.SearchTarget,
				SearchOptions.LookIn,
				SearchOptions.LookInFiletypes,
				SearchOptions.IncludeSubdirectories,
				SearchOptions.SearchTarget == SearchTarget.CurrentSelection
					? SearchManager.GetActiveSelection(true) : null);

			var strategy = SearchStrategyFactory.Create(
				SearchOptions.FindPattern,
				!SearchOptions.MatchCase,
				SearchOptions.MatchWholeWord,
				SearchOptions.SearchMode);

			SearchManager.FindAll(strategy, location, SD.StatusBar.CreateProgressMonitor());
		}

		void FindNextClick(object sender, RoutedEventArgs e)
		{
			SaveOptions();
			if (SearchOptions.CurrentFindPattern.Length == 0)
				return;

			var location = new SearchLocation(
				SearchOptions.SearchTarget,
				SearchOptions.LookIn,
				SearchOptions.LookInFiletypes,
				SearchOptions.IncludeSubdirectories,
				SearchOptions.SearchTarget == SearchTarget.CurrentSelection
					? SearchManager.GetActiveSelection(true) : null);

			var strategy = SearchStrategyFactory.Create(
				SearchOptions.FindPattern,
				!SearchOptions.MatchCase,
				SearchOptions.MatchWholeWord,
				SearchOptions.SearchMode);

			var result = SearchManager.FindNext(strategy, location);
			SearchManager.SelectResult(result);
		}

		void CloseClick(object sender, RoutedEventArgs e)
		{
			Close();
		}
	}
}
