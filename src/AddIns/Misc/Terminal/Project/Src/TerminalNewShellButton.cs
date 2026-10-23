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
			menu.IsOpen = true;
		}
	}
}
