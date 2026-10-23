using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenDevelop.Terminal
{
	/// <summary>The find bar above the terminal: query, match case, previous/next, a count, close. It drives the active <see cref="TerminalView"/>.</summary>
	sealed class TerminalFindBar : Border
	{
		readonly TextBox query = new TextBox { MinWidth = 220, Margin = new Thickness(0, 0, 4, 0) };
		readonly CheckBox matchCase = new CheckBox { Content = "Aa", ToolTip = "Match case", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
		readonly TextBlock status = new TextBlock { MinWidth = 70, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
		readonly Func<TerminalView> activeView;
		readonly Action close;
		// A search pages through the whole scrollback (about 0.2 s for 12 000 lines), so typing waits for a pause instead of searching per key.
		readonly DispatcherTimer debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
		bool programmatic;

		public TerminalFindBar(Func<TerminalView> activeView, Action close)
		{
			this.activeView = activeView;
			this.close = close;
			Visibility = Visibility.Collapsed;
			Padding = new Thickness(4);

			var previous = Button("▲", "Previous match (Shift+Enter)", () => Step(-1));
			var next = Button("▼", "Next match (Enter)", () => Step(1));
			var closeButton = Button("✕", "Close (Esc)", () => this.close());
			var row = new StackPanel { Orientation = Orientation.Horizontal };
			foreach (UIElement element in new UIElement[] { query, matchCase, previous, next, status, closeButton })
				row.Children.Add(element);
			Child = row;

			debounce.Tick += (_, _) => {
				debounce.Stop();
				Search();
			};
			query.TextChanged += (_, _) => {
				if (programmatic)
					return;
				debounce.Stop();
				debounce.Start();
			};
			matchCase.Click += (_, _) => Search();
			query.PreviewKeyDown += (_, e) => {
				if (e.Key == Key.Enter) {
					Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
					e.Handled = true;
				} else if (e.Key == Key.Escape) {
					this.close();
					e.Handled = true;
				}
			};
		}

		static Button Button(string content, string toolTip, Action click)
		{
			var button = new Button { Content = content, ToolTip = toolTip, MinWidth = 24, Margin = new Thickness(0, 0, 2, 0) };
			button.Click += (_, _) => click();
			return button;
		}

		/// <summary>The query text, for tests and for refreshing the status.</summary>
		public string Query => query.Text;

		/// <summary>How many searches the bar has run (typing is debounced, so fewer than keystrokes).</summary>
		public int SearchCount { get; private set; }

		public void Show(string prefill)
		{
			Visibility = Visibility.Visible;
			debounce.Stop();
			if (!string.IsNullOrEmpty(prefill)) {
				programmatic = true; // one search below, not one per TextChanged
				query.Text = prefill;
				programmatic = false;
			}
			query.Focus();
			query.SelectAll();
			Search();
		}

		/// <summary>Types the text one character at a time, as the keyboard would (each step raises TextChanged).</summary>
		public void TypeQuery(string text)
		{
			for (int i = 1; i <= text.Length; i++)
				query.Text = text.Substring(0, i);
		}

		public void Hide()
		{
			debounce.Stop();
			Visibility = Visibility.Collapsed;
		}

		public bool IsOpen => Visibility == Visibility.Visible;

		/// <summary>What the bar shows next to the buttons: "2 of 5", "No results" or nothing.</summary>
		public string StatusText => status.Text;

		void Search()
		{
			var view = activeView();
			if (view == null)
				return;
			SearchCount++;
			int count = view.RunFind(query.Text, matchCase.IsChecked == true);
			if (count > 0)
				view.MoveToMatch(0);
			UpdateStatus(view);
		}

		void Step(int delta)
		{
			var view = activeView();
			if (view == null)
				return;
			if (debounce.IsEnabled) { // a typed query is still waiting for its search: run it now, then step from the start
				debounce.Stop();
				SearchCount++;
				view.RunFind(query.Text, matchCase.IsChecked == true);
			} else if (view.MatchCount == 0) {
				SearchCount++;
				view.RunFind(query.Text, matchCase.IsChecked == true);
			}
			view.MoveToMatch(delta);
			UpdateStatus(view);
		}

		void UpdateStatus(TerminalView view)
			=> status.Text = string.IsNullOrEmpty(query.Text) ? "" : view.MatchCount == 0 ? "No results" : $"{view.CurrentMatchIndex + 1} of {view.MatchCount}";
	}
}
