using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Lists every folder the user has trusted and lets each be revoked, with or without a solution open.
	/// Built in code: it is a small fixed layout and Base has no XAML dialogs of its own.
	/// </summary>
	public sealed class WorkspaceTrustWindow : Window
	{
		readonly IWorkspaceTrustService trust;
		readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
		readonly ListBox list = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 140 };
		readonly Button revoke = new Button { Content = WorkspaceTrust.Text("WorkspaceTrust.Window.RevokeSelected", "Revoke Selected"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
		readonly Button revokeAll = new Button { Content = WorkspaceTrust.Text("WorkspaceTrust.Window.RevokeAll", "Revoke All"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
		readonly Button trustCurrent = new Button { Content = WorkspaceTrust.Text("WorkspaceTrust.Window.TrustCurrent", "Trust Current Workspace..."), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
		readonly TextBlock note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.DarkRed };

		/// <summary>True when the user trusted the open workspace here, so the caller reloads the solution.</summary>
		public bool TrustedCurrentWorkspace { get; private set; }

		public WorkspaceTrustWindow(IWorkspaceTrustService trust)
		{
			this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
			Title = WorkspaceTrust.Text("WorkspaceTrust.Title", "Workspace Trust");
			Width = 640;
			Height = 440;
			MinWidth = 460;
			MinHeight = 320;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;

			var grid = new Grid { Margin = new Thickness(12) };
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			Grid.SetRow(status, 0);
			var heading = new TextBlock { Text = WorkspaceTrust.Text("WorkspaceTrust.Window.Heading", "Trusted folders (code in these folders may be built, run and analyzed):"), Margin = new Thickness(0, 0, 0, 4) };
			Grid.SetRow(heading, 1);
			Grid.SetRow(list, 2);

			var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
			buttons.Children.Add(revoke);
			buttons.Children.Add(revokeAll);
			buttons.Children.Add(trustCurrent);
			var close = new Button { Content = WorkspaceTrust.Text("Global.Close", "Close"), Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsCancel = true };
			close.Click += (_, _) => Close();
			var right = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
			DockPanel.SetDock(close, Dock.Right);
			right.Children.Add(close);
			right.Children.Add(buttons);
			buttons.Margin = new Thickness(0);
			Grid.SetRow(right, 3);
			Grid.SetRow(note, 4);

			grid.Children.Add(status);
			grid.Children.Add(heading);
			grid.Children.Add(list);
			grid.Children.Add(right);
			grid.Children.Add(note);
			Content = grid;

			list.SelectionChanged += (_, _) => revoke.IsEnabled = list.SelectedItems.Count > 0;
			revoke.Click += (_, _) => RevokeRoots(list.SelectedItems.Cast<string>().ToList());
			revokeAll.Click += (_, _) => RevokeRoots(trust.TrustedRoots.ToList());
			trustCurrent.Click += (_, _) => {
				string root = trust.CurrentWorkspaceRoot;
				if (root != null && WorkspaceTrustPrompt.Ask(trust, root)) {
					TrustedCurrentWorkspace = true;
					Close();
				}
			};
			Refresh();
		}

		void RevokeRoots(System.Collections.Generic.List<string> roots)
		{
			if (roots.Count == 0)
				return;
			bool affectsCurrent = WorkspaceTrustPrompt.Revoke(trust, roots);
			Refresh();
			if (affectsCurrent)
				note.Text = WorkspaceTrust.Text("WorkspaceTrust.Window.RevokedNote",
					"The open workspace is no longer trusted. Analyzers and assemblies already loaded stay in memory until OpenDevelop restarts; "
					+ "builds, runs, debugging, tests, terminals, designers and semantic analysis are refused from now on.");
		}

		void Refresh()
		{
			string root = trust.CurrentWorkspaceRoot;
			if (!trust.IsEnabled)
				status.Text = WorkspaceTrust.Text("WorkspaceTrust.Window.StatusDisabled", "Workspace Trust is switched off (OD_WORKSPACE_TRUST=0): every folder is treated as trusted.");
			else if (root == null)
				status.Text = WorkspaceTrust.Text("WorkspaceTrust.Window.StatusNoSolution", "No solution is open.");
			else
				status.Text = string.Format(WorkspaceTrust.Text("WorkspaceTrust.Window.StatusOpen", "Open workspace: {0}"), root) + "\n"
					+ (trust.CurrentState == TrustState.Trusted
						? WorkspaceTrust.Text("WorkspaceTrust.Window.StateTrusted", "Trusted.")
						: WorkspaceTrust.Text("WorkspaceTrust.Window.StateRestricted", "Restricted Mode."));
			list.Items.Clear();
			foreach (string trusted in trust.TrustedRoots.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
				list.Items.Add(trusted);
			revokeAll.IsEnabled = list.Items.Count > 0;
			trustCurrent.Visibility = trust.IsEnabled && root != null && trust.CurrentState == TrustState.Restricted ? Visibility.Visible : Visibility.Collapsed;
		}
	}
}
