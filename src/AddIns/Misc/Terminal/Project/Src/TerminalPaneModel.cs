using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.ILSpy.ViewModels;

namespace OpenDevelop.Terminal
{
	/// <summary>One shell: its session and the view that draws it.</summary>
	sealed class TerminalPane
	{
		public TerminalSession Session { get; init; }
		public TerminalView View { get; init; }
		public TerminalGroup Group { get; init; }
	}

	/// <summary>Panes shown side by side; a plain terminal is a group of one, Split adds a pane.</summary>
	sealed class TerminalGroup
	{
		public List<TerminalPane> Panes { get; } = new List<TerminalPane>();
		public Grid Host { get; } = new Grid();
	}

	/// <summary>
	/// The Terminal pad: a classic toolbar over the active <see cref="TerminalGroup"/>. Constructed once by
	/// the <see cref="TerminalPad"/> shim (same shape as Debugger.AddIn's ConsolePadViewModel).
	/// </summary>
	sealed class TerminalPaneModel : ToolPaneModel
	{
		readonly List<TerminalGroup> groups = new List<TerminalGroup>();
		readonly Border host = new Border();
		TerminalPane activePane;
		string lastLabels = "";

		public static TerminalPaneModel Instance { get; private set; }

		/// <summary>Raised when panes are added/removed, the active pane changes, or a pane's label changes.</summary>
		public event Action Changed;

		public TerminalPaneModel()
		{
			Instance = this; // The toolbar's custom items look the model up while the toolbar is being created.
			Title = "Terminal";
			ContentId = "TerminalPad";
			IsVisible = false;
			IsCloseable = true;
			LegacyPadClass = typeof(TerminalPad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			var panel = new DockPanel();
			var toolBar = ToolBarService.CreateToolBar(host, this, "/SharpDevelop/Pads/TerminalPad/ToolBar");
			DockPanel.SetDock(toolBar, Dock.Top);
			panel.Children.Add(toolBar);
			panel.Children.Add(host);
			Content = panel;
		}

		public IEnumerable<TerminalPane> Panes => groups.SelectMany(g => g.Panes);
		public List<TerminalSession> Sessions => Panes.Select(p => p.Session).ToList();
		public TerminalPane ActivePane => activePane;
		public TerminalSession Active => activePane?.Session;
		public TerminalView View => activePane?.View;
		public int ActiveIndex => activePane == null ? -1 : Panes.ToList().IndexOf(activePane);
		public int GroupIndexOf(TerminalPane pane) => groups.IndexOf(pane.Group);

		public IReadOnlyList<string> Labels()
			=> Panes.Select((p, i) => $"{i + 1}: {(string.IsNullOrEmpty(p.Session.Title) ? p.Session.ShellName : p.Session.Title)}{(p.Group.Panes.Count > 1 ? "  (split)" : "")}").ToList();

		public Task<TerminalPane> NewSessionAsync(string shell = null, string workingDirectory = null)
		{
			var group = new TerminalGroup();
			groups.Add(group);
			return StartPaneAsync(group, shell, EstimateColumns(1), EstimateRows(), workingDirectory);
		}

		public Task<TerminalPane> SplitActiveAsync()
		{
			var group = activePane?.Group;
			if (group == null)
				return NewSessionAsync();
			return StartPaneAsync(group, activePane.Session.ShellPath, EstimateColumns(group.Panes.Count + 1), EstimateRows());
		}

		// A PTY must start near its final size: before its first layout a view cannot say, so use the
		// active pane (shared width / full height) or 80x24.
		int EstimateColumns(int panesInGroup) => activePane?.View is { IsLaidOut: true } v ? Math.Max(20, v.ColumnsFit * Math.Max(1, activePane.Group.Panes.Count) / panesInGroup) : 80;
		int EstimateRows() => activePane?.View is { IsLaidOut: true } v ? v.RowsFit : 24;

		async Task<TerminalPane> StartPaneAsync(TerminalGroup group, string shell, int columns, int rows, string workingDirectory = null)
		{
			string cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : SD.ProjectService?.CurrentSolution?.Directory?.ToString();
			if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
				cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			TerminalSession session;
			try {
				session = await TerminalSession.StartAsync(shell ?? TerminalSession.DefaultShell(), cwd, columns, rows).ConfigureAwait(true);
			} catch {
				if (group.Panes.Count == 0)
					groups.Remove(group);
				throw;
			}
			var view = new TerminalView();
			var pane = new TerminalPane { Session = session, View = view, Group = group };
			view.Session = session;
			view.GotKeyboardFocus += (_, _) => SetActive(pane);
			view.Updated += RaiseChangedIfLabelsChanged;
			group.Panes.Add(pane);
			LayoutGroup(group);
			host.Child = group.Host;
			SetActive(pane);
			view.Focus();
			return pane;
		}

		static void LayoutGroup(TerminalGroup group)
		{
			var grid = group.Host;
			grid.Children.Clear();
			grid.ColumnDefinitions.Clear();
			for (int i = 0; i < group.Panes.Count; i++) {
				if (i > 0) {
					var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Gray, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
					grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
					Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
					grid.Children.Add(splitter);
				}
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				Grid.SetColumn(group.Panes[i].View, grid.ColumnDefinitions.Count - 1);
				grid.Children.Add(group.Panes[i].View);
			}
		}

		void SetActive(TerminalPane pane)
		{
			if (activePane == pane)
				return;
			activePane = pane;
			if (pane != null)
				host.Child = pane.Group.Host;
			RaiseChanged();
		}

		public void ActivateIndex(int index)
		{
			var pane = Panes.ElementAtOrDefault(index);
			if (pane == null)
				return;
			SetActive(pane);
			pane.View.Focus();
		}

		public void KillActive()
		{
			var pane = activePane;
			if (pane == null)
				return;
			var group = pane.Group;
			group.Panes.Remove(pane);
			pane.View.Dispose();
			pane.Session.Dispose();
			activePane = null;
			if (group.Panes.Count > 0) {
				LayoutGroup(group);
				SetActive(group.Panes.Last());
				activePane.View.Focus();
			} else {
				groups.Remove(group);
				var next = groups.LastOrDefault();
				host.Child = next?.Host;
				if (next != null) {
					SetActive(next.Panes.Last());
					activePane.View.Focus();
				} else {
					RaiseChanged();
					NewSessionAsync().FireAndForget();
				}
			}
		}

		public void EnsureSession()
		{
			if (activePane == null && groups.Count == 0)
				NewSessionAsync().FireAndForget();
		}

		public void ClearActive() => Active?.Clear();

		public string ScreenText() => Active?.GetSnapshot()?.GetText() ?? "";

		public void CopySelection() => View?.CopySelection();

		public void Paste() => View?.PasteFromClipboard();

		void RaiseChangedIfLabelsChanged()
		{
			var labels = string.Join("\n", Labels());
			if (labels != lastLabels)
				RaiseChanged();
		}

		void RaiseChanged()
		{
			lastLabels = string.Join("\n", Labels());
			Changed?.Invoke();
		}
	}
}
