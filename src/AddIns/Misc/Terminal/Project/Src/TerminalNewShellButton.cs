using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop;

namespace OpenDevelop.Terminal
{
	/// <summary>The arrow beside "New Terminal": a menu of installed shells and the default-shell choice.</summary>
	public sealed class TerminalNewShellButton : Button
	{
		public TerminalNewShellButton()
		{
			Content = "▾";
			ToolTip = "Select the shell for a new terminal";
			Padding = new Thickness(4, 0, 4, 0);
			Click += (_, _) => ShowMenu();
			Loaded += (_, _) => Visible = this;
		}

		/// <summary>The arrow that was loaded last (what a pointer test needs to find), and the menu it opened while that is open.</summary>
		internal static TerminalNewShellButton Visible { get; private set; }

		ContextMenu openMenu;

		/// <summary>Screen centres of the open menu's top-level entries (a popup is not in the UI tree); empty when it is closed.</summary>
		internal IReadOnlyList<(string Header, Point Screen)> OpenMenuEntries()
		{
			var entries = new List<(string, Point)>();
			if (openMenu is { IsOpen: true })
				foreach (var item in openMenu.Items)
					if (item is MenuItem entry && entry.IsVisible)
						entries.Add((entry.Header as string, entry.PointToScreen(new Point(entry.ActualWidth / 2, entry.ActualHeight / 2))));
			return entries;
		}

		internal void CloseMenu()
		{
			if (openMenu != null)
				openMenu.IsOpen = false;
		}

		void ShowMenu()
		{
			var model = TerminalPaneModel.Instance;
			var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
			var defaultShell = TerminalShells.Default;
			var defaults = new MenuItem { Header = "Default Shell" };
			foreach (var shell in TerminalShells.Detect()) {
				var path = shell;
				var open = new MenuItem { Header = TerminalShells.DisplayName(path) + (string.Equals(path, defaultShell, StringComparison.OrdinalIgnoreCase) ? "  (default)" : "") };
				open.Click += (_, _) => model?.NewSessionAsync(path).FireAndForget();
				menu.Items.Add(open);
				var choose = new MenuItem { Header = TerminalShells.DisplayName(path), IsCheckable = true, IsChecked = string.Equals(path, defaultShell, StringComparison.OrdinalIgnoreCase) };
				choose.Click += (_, _) => TerminalShells.Default = path;
				defaults.Items.Add(choose);
			}
			menu.Items.Add(new Separator());
			menu.Items.Add(defaults);
			menu.Closed += (_, _) => { if (ReferenceEquals(openMenu, menu)) openMenu = null; };
			openMenu = menu;
			menu.IsOpen = true;
		}
	}
}
