using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Search;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// The "reusable OpenLens details view" doc/technotes/openlens.md §15.2 calls for - a lightweight
	/// popup anchored to the clicked lens item, rather than jumping straight into
	/// <see cref="SearchResultsPad"/>. Offers a "Show in Search Results" command to promote the same
	/// result set into that pad (§15.2: "Do not create separate result models when an existing
	/// OpenDevelop pad already represents the same information" - this popup is a view over the same
	/// <see cref="SearchResultMatch"/> list the pad would show, not a second copy of the data).
	///
	/// Keyboard (doc §15.5): Up/Down moves the selection, Enter navigates to it and closes, Escape
	/// closes without navigating. Double-click navigates the same as Enter.
	///
	/// doc §15.3 ("for one reference, still open the list by default") is satisfied by construction -
	/// this class doesn't special-case a single-result list into direct navigation.
	/// </summary>
	sealed class OpenLensResultsPopup : Popup
	{
		readonly ListBox listBox;

		public OpenLensResultsPopup(UIElement placementTarget, string title, IReadOnlyList<SearchResultMatch> matches, Action promoteToSearchResultsPad)
		{
			PlacementTarget = placementTarget;
			Placement = PlacementMode.Bottom;
			StaysOpen = false;
			AllowsTransparency = true;
			PopupAnimation = PopupAnimation.Fade;

			listBox = new ListBox {
				MaxHeight = 300,
				MinWidth = 320,
				BorderThickness = new Thickness(0),
			};
			foreach (var match in matches)
				listBox.Items.Add(new ResultRow(match));
			if (listBox.Items.Count > 0)
				listBox.SelectedIndex = 0;
			listBox.MouseDoubleClick += (sender, e) => NavigateToSelectionAndClose();
			listBox.PreviewKeyDown += ListBox_PreviewKeyDown;

			var header = new TextBlock {
				Text = title,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(8, 6, 8, 4),
			};

			var promoteButton = new Button {
				Content = "Show in Search Results",
				Margin = new Thickness(8, 4, 8, 6),
				HorizontalAlignment = HorizontalAlignment.Left,
			};
			promoteButton.Click += (sender, e) => {
				promoteToSearchResultsPad();
				IsOpen = false;
			};

			var panel = new DockPanel {
				Background = SystemColors.ControlBrush,
			};
			DockPanel.SetDock(header, Dock.Top);
			DockPanel.SetDock(promoteButton, Dock.Bottom);
			panel.Children.Add(header);
			panel.Children.Add(promoteButton);
			panel.Children.Add(listBox);

			Child = new Border {
				BorderBrush = SystemColors.ActiveBorderBrush,
				BorderThickness = new Thickness(1),
				Child = panel,
			};

			Opened += (sender, e) => listBox.Focus();
			KeyDown += (sender, e) => {
				if (e.Key == Key.Escape) {
					IsOpen = false;
					e.Handled = true;
				}
			};
		}

		void ListBox_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter) {
				NavigateToSelectionAndClose();
				e.Handled = true;
			} else if (e.Key == Key.Escape) {
				IsOpen = false;
				e.Handled = true;
			}
		}

		void NavigateToSelectionAndClose()
		{
			if (listBox.SelectedItem is ResultRow row) {
				try {
					SD.FileService.JumpToFilePosition(row.Match.FileName, row.Match.StartLocation.Line, row.Match.StartLocation.Column);
				} catch (Exception ex) {
					LoggingService.Warn("OpenLens: couldn't navigate to result. " + ex.Message);
				}
			}
			IsOpen = false;
		}

		sealed class ResultRow
		{
			public ResultRow(SearchResultMatch match)
			{
				Match = match;
				Text = System.IO.Path.GetFileName(match.FileName) + ":" + match.StartLocation.Line;
			}

			public SearchResultMatch Match { get; }
			public string Text { get; }

			public override string ToString() => Text;
		}
	}
}
