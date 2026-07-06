using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace Plugins.RegExpTk
{
	public partial class RegExpTkDialog : Window
	{
		readonly ObservableCollection<MatchViewModel> matches = new ObservableCollection<MatchViewModel>();

		public RegExpTkDialog()
		{
			InitializeComponent();
			matchListView.ItemsSource = matches;
			matchListView.MouseDoubleClick += MatchListView_MouseDoubleClick;
			regexTextBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { FindButton_Click(null, null); e.Handled = true; } };
		}

		void QuickInsertButton_Click(object sender, RoutedEventArgs e)
		{
			var menu = new ContextMenu();
			menu.Items.Add(CreateMenuItem("Ungreedy *?", "*?"));
			menu.Items.Add(new Separator());
			menu.Items.Add(CreateMenuItem(@"Word \w", @"\w"));
			menu.Items.Add(CreateMenuItem(@"Non-word \W", @"\W"));
			menu.Items.Add(CreateMenuItem(@"Whitespace \s", @"\s"));
			menu.Items.Add(CreateMenuItem(@"Non-whitespace \S", @"\S"));
			menu.Items.Add(CreateMenuItem(@"Digit \d", @"\d"));
			menu.Items.Add(CreateMenuItem(@"Non-digit \D", @"\D"));
			menu.Items.Add(CreateMenuItem(@"Word border \b", @"\b"));
			menu.PlacementTarget = quickInsertButton;
			menu.IsOpen = true;
		}

		static MenuItem CreateMenuItem(string header, string text)
		{
			var item = new MenuItem { Header = header, Tag = text };
			item.Click += (s, e) => {
				if (s is MenuItem mi && mi.Tag is string t)
				{
					var active = Keyboard.FocusedElement as System.Windows.Controls.TextBox;
					if (active != null)
						active.SelectedText = t;
				}
			};
			return item;
		}

		void ReplaceCheckBox_Changed(object sender, RoutedEventArgs e)
		{
			replacementTextBox.IsEnabled = replaceCheckBox.IsChecked == true;
			replaceResultTextBox.IsEnabled = replaceCheckBox.IsChecked == true;
		}

		void FindButton_Click(object sender, RoutedEventArgs e)
		{
			matches.Clear();
			string pattern = regexTextBox.Text;
			if (string.IsNullOrEmpty(pattern))
				return;

			RegexOptions options = RegexOptions.None;
			if (ignoreCaseCheckBox.IsChecked == true)
				options |= RegexOptions.IgnoreCase;
			if (multilineCheckBox.IsChecked == true)
				options |= RegexOptions.Multiline;

			string input = inputTextBox.Text;

			try
			{
				if (replaceCheckBox.IsChecked == true)
				{
					string replacement = replacementTextBox.Text;
					replaceResultTextBox.Text = Regex.Replace(input, pattern, replacement, options);
				}

				var found = Regex.Matches(input, pattern, options);
				foreach (Match m in found)
					matches.Add(new MatchViewModel(m));

				statusBarItem.Content = matches.Count + " " + (matches.Count == 1 ? "match" : "matches");
			}
			catch (Exception ex)
			{
				statusBarItem.Content = ex.Message;
				return;
			}
		}

		void MatchListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			if (matchListView.SelectedItem is MatchViewModel mvm)
			{
				var dlg = new GroupForm(mvm.Match);
				dlg.Owner = this;
				dlg.ShowDialog();
			}
		}

		void ShowGroupsButton_Click(object sender, RoutedEventArgs e)
		{
			if (matchListView.SelectedItem is MatchViewModel mvm)
			{
				var dlg = new GroupForm(mvm.Match);
				dlg.Owner = this;
				dlg.ShowDialog();
			}
		}

		void CloseButton_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}
	}
}
