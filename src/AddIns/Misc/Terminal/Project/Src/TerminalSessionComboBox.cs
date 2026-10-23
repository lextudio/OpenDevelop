using System.Windows;
using System.Windows.Controls;

namespace OpenDevelop.Terminal
{
	/// <summary>Toolbar item listing every terminal; picking one makes it the active pane.</summary>
	public sealed class TerminalSessionComboBox : ComboBox
	{
		readonly TerminalPaneModel model = TerminalPaneModel.Instance;
		bool refreshing;

		public TerminalSessionComboBox()
		{
			MinWidth = 180;
			// LibreWPF's implicit-style lookup does not walk BaseType, so a ComboBox subclass would
			// miss the themed ComboBox style (same workaround as the Task List scope combo).
			Loaded += (_, _) => {
				if (Style == null && Application.Current != null)
					Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
			};
			if (model != null) {
				// The model (and so this toolbar item) outlives the pad's visual tree: closing and reopening the pad
				// unloads and loads the combo again, so the subscription follows Loaded/Unloaded and each Loaded refreshes.
				Loaded += (_, _) => {
					model.Changed -= Refresh;
					model.Changed += Refresh;
					Refresh();
				};
				Unloaded += (_, _) => model.Changed -= Refresh;
				model.Changed += Refresh;
				Refresh();
			}
		}

		void Refresh()
		{
			refreshing = true;
			try {
				ItemsSource = model.Labels();
				SelectedIndex = model.ActiveIndex;
			} finally {
				refreshing = false;
			}
		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
			base.OnSelectionChanged(e);
			if (!refreshing && model != null && SelectedIndex >= 0 && SelectedIndex != model.ActiveIndex)
				model.ActivateIndex(SelectedIndex);
		}
	}
}
