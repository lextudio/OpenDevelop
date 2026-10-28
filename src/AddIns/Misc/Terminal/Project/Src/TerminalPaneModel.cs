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
		/// <summary>A name the user gave this terminal; null shows the shell's own title.</summary>
		public string CustomName { get; set; }
		public string SessionKey { get; init; }
	}

	enum TerminalSplitOrientation
	{
		Horizontal,
		Vertical,
	}

	abstract class TerminalLayoutNode
	{
		public TerminalLayoutNode Parent { get; set; }
	}

	sealed class TerminalLayoutLeaf : TerminalLayoutNode
	{
		public TerminalPane Pane { get; init; }
	}

	sealed class TerminalLayoutSplit : TerminalLayoutNode
	{
		public TerminalSplitOrientation Orientation { get; init; }
		public TerminalLayoutNode First { get; set; }
		public TerminalLayoutNode Second { get; set; }
	}

	/// <summary>Panes in one terminal group. Its split tree permits horizontal and vertical splits to nest.</summary>
	sealed class TerminalGroup
	{
		public List<TerminalPane> Panes { get; } = new List<TerminalPane>();
		public Grid Host { get; } = new Grid();
		public TerminalLayoutNode Root { get; private set; }

		public void AddRoot(TerminalPane pane) => Root = new TerminalLayoutLeaf { Pane = pane };

		public void Split(TerminalPane existing, TerminalPane added, TerminalSplitOrientation orientation)
		{
			var leaf = FindLeaf(Root, existing) ?? throw new InvalidOperationException("The terminal pane is not in its group.");
			var split = new TerminalLayoutSplit {
				Orientation = orientation,
				First = leaf,
				Second = new TerminalLayoutLeaf { Pane = added },
				Parent = leaf.Parent,
			};
			leaf.Parent = split;
			split.Second.Parent = split;
			if (split.Parent == null)
				Root = split;
			else if (ReferenceEquals(((TerminalLayoutSplit)split.Parent).First, leaf))
				((TerminalLayoutSplit)split.Parent).First = split;
			else
				((TerminalLayoutSplit)split.Parent).Second = split;
		}

		public void Remove(TerminalPane pane)
		{
			var leaf = FindLeaf(Root, pane);
			if (leaf == null)
				return;
			if (leaf.Parent == null) {
				Root = null;
				return;
			}
			var split = (TerminalLayoutSplit)leaf.Parent;
			Replace(split, ReferenceEquals(split.First, leaf) ? split.Second : split.First);
		}

		void Replace(TerminalLayoutNode oldNode, TerminalLayoutNode newNode)
		{
			var parent = oldNode.Parent;
			newNode.Parent = parent;
			if (parent == null)
				Root = newNode;
			else if (ReferenceEquals(((TerminalLayoutSplit)parent).First, oldNode))
				((TerminalLayoutSplit)parent).First = newNode;
			else
				((TerminalLayoutSplit)parent).Second = newNode;
		}

		static TerminalLayoutLeaf FindLeaf(TerminalLayoutNode node, TerminalPane pane)
			=> node switch {
				TerminalLayoutLeaf leaf when ReferenceEquals(leaf.Pane, pane) => leaf,
				TerminalLayoutSplit split => FindLeaf(split.First, pane) ?? FindLeaf(split.Second, pane),
				_ => null,
			};
	}

	/// <summary>
	/// The Terminal pad: a classic toolbar over the active <see cref="TerminalGroup"/>. Constructed once by
	/// the <see cref="TerminalPad"/> shim (same shape as Debugger.AddIn's ConsolePadViewModel).
	/// </summary>
	sealed class TerminalPaneModel : ToolPaneModel
	{
		readonly List<TerminalGroup> groups = new List<TerminalGroup>();
		readonly Border host = new Border();
		readonly TerminalFindBar findBar;
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
			findBar = new TerminalFindBar(() => View, HideFind);
			DockPanel.SetDock(findBar, Dock.Top);
			panel.Children.Add(findBar);
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
			=> Panes.Select((p, i) => $"{i + 1}: {Name(p)}{(p.Group.Panes.Count > 1 ? "  (split)" : "")}").ToList();

		public static string Name(TerminalPane pane)
			=> !string.IsNullOrEmpty(pane.CustomName) ? pane.CustomName
				: string.IsNullOrEmpty(pane.Session.Title) ? pane.Session.ShellName : pane.Session.Title;

		/// <summary>Names the active terminal; an empty name goes back to the shell's own title.</summary>
		public void RenameActive(string name)
		{
			if (activePane == null)
				return;
			name = name?.Trim();
			activePane.CustomName = string.IsNullOrEmpty(name) ? null : name;
			RaiseChanged();
		}

		/// <summary>Moves to the next (+1) or previous (-1) terminal in the session list, wrapping around.</summary>
		public void ActivateRelative(int delta)
		{
			var panes = Panes.ToList();
			if (panes.Count == 0)
				return;
			int current = Math.Max(0, panes.IndexOf(activePane));
			ActivateIndex(((current + delta) % panes.Count + panes.Count) % panes.Count);
		}

		public Task<TerminalPane> NewSessionAsync(string shell = null, string workingDirectory = null, IReadOnlyList<string> arguments = null, IReadOnlyDictionary<string, string> environment = null, string title = null, string sessionKey = null)
		{
			var group = new TerminalGroup();
			groups.Add(group);
			return StartPaneAsync(group, shell, EstimateColumns(1), EstimateRows(), workingDirectory, null, TerminalSplitOrientation.Horizontal, arguments, environment, title, sessionKey);
		}

		public Task<TerminalPane> SplitActiveAsync(TerminalSplitOrientation orientation = TerminalSplitOrientation.Horizontal)
		{
			var group = activePane?.Group;
			if (group == null)
				return NewSessionAsync();
			// A split starts where the terminal beside it is now, not where that one started.
			return StartPaneAsync(group, activePane.Session.ShellPath, EstimateSplitColumns(orientation), EstimateSplitRows(orientation), activePane.Session.CurrentDirectory, activePane, orientation);
		}

		// A PTY must start near its final size: before its first layout a view cannot say, so use the
		// active pane (shared width / full height) or 80x24.
		int EstimateColumns(int panesInGroup) => activePane?.View is { IsLaidOut: true } v ? Math.Max(20, v.ColumnsFit * Math.Max(1, activePane.Group.Panes.Count) / panesInGroup) : 80;
		int EstimateRows() => activePane?.View is { IsLaidOut: true } v ? v.RowsFit : 24;
		int EstimateSplitColumns(TerminalSplitOrientation orientation)
			=> activePane?.View is { IsLaidOut: true } v ? orientation == TerminalSplitOrientation.Horizontal ? Math.Max(20, v.ColumnsFit / 2) : v.ColumnsFit : 80;
		int EstimateSplitRows(TerminalSplitOrientation orientation)
			=> activePane?.View is { IsLaidOut: true } v ? orientation == TerminalSplitOrientation.Vertical ? Math.Max(1, v.RowsFit / 2) : v.RowsFit : 24;

		async Task<TerminalPane> StartPaneAsync(TerminalGroup group, string shell, int columns, int rows, string workingDirectory = null, TerminalPane splitFrom = null, TerminalSplitOrientation orientation = TerminalSplitOrientation.Horizontal, IReadOnlyList<string> arguments = null, IReadOnlyDictionary<string, string> environment = null, string title = null, string sessionKey = null)
		{
			string cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : SD.ProjectService?.CurrentSolution?.Directory?.ToString();
			if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
				cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			TerminalSession session;
			try {
				session = await TerminalSession.StartAsync(shell ?? TerminalSession.DefaultShell(), cwd, columns, rows, TerminalTheme.Current(), arguments, environment).ConfigureAwait(true);
			} catch {
				if (group.Panes.Count == 0)
					groups.Remove(group);
				throw;
			}
			var view = new TerminalView();
			var pane = new TerminalPane { Session = session, View = view, Group = group, CustomName = title, SessionKey = sessionKey };
			view.Session = session;
			view.GotKeyboardFocus += (_, _) => SetActive(pane);
			view.Updated += RaiseChangedIfLabelsChanged;
			view.CloseRequested += () => KillPane(pane);
			view.FindRequested += ShowFind;
			group.Panes.Add(pane);
			if (splitFrom == null)
				group.AddRoot(pane);
			else
				group.Split(splitFrom, pane, orientation);
			LayoutGroup(group);
			host.Child = group.Host;
			SetActive(pane);
			view.Focus();
			return pane;
		}

		static void LayoutGroup(TerminalGroup group)
		{
			foreach (var child in group.Host.Children.OfType<UIElement>().ToList())
				Detach(child);
			group.Host.Children.Clear();
			if (group.Root != null)
				group.Host.Children.Add(BuildLayout(group.Root));
		}

		static void Detach(UIElement element)
		{
			if (element is not Panel panel)
				return;
			foreach (var child in panel.Children.OfType<UIElement>().ToList())
				Detach(child);
			panel.Children.Clear();
		}

		static UIElement BuildLayout(TerminalLayoutNode node)
		{
			if (node is TerminalLayoutLeaf leaf)
				return leaf.Pane.View;
			var split = (TerminalLayoutSplit)node;
			var grid = new Grid();
			if (split.Orientation == TerminalSplitOrientation.Horizontal) {
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				var divider = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Gray, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
				var first = BuildLayout(split.First);
				Grid.SetColumn(first, 0);
				Grid.SetColumn(divider, 1);
				var second = BuildLayout(split.Second);
				Grid.SetColumn(second, 2);
				grid.Children.Add(first); grid.Children.Add(divider); grid.Children.Add(second);
			} else {
				grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				var divider = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Gray, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
				var first = BuildLayout(split.First);
				Grid.SetRow(first, 0);
				Grid.SetRow(divider, 1);
				var second = BuildLayout(split.Second);
				Grid.SetRow(second, 2);
				grid.Children.Add(first); grid.Children.Add(divider); grid.Children.Add(second);
			}
			return grid;
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

		/// <summary>Activates an existing IDE-owned session by its stable, non-display identity.</summary>
		public bool ActivateSession(string key)
		{
			var pane = Panes.FirstOrDefault(p => !p.Session.HasExited && string.Equals(p.SessionKey, key, StringComparison.Ordinal));
			if (pane == null)
				return false;
			SetActive(pane);
			pane.View.Focus();
			return true;
		}

		public void KillActive() => KillPane(activePane);

		/// <summary>Ends a pane's shell and removes the pane; the active pane moves to a neighbour when it was the active one.</summary>
		void KillPane(TerminalPane pane)
		{
			if (pane == null || !pane.Group.Panes.Contains(pane))
				return; // already gone (key repeat on an exited pane, for instance)
			var group = pane.Group;
			bool wasActive = pane == activePane;
			group.Panes.Remove(pane);
			group.Remove(pane);
			pane.View.Dispose();
			pane.Session.Dispose();
			if (!wasActive) {
				if (group.Panes.Count > 0) {
					LayoutGroup(group);
				} else {
					groups.Remove(group);
				}
				RaiseChanged();
				return;
			}
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

		/// <summary>Shows the find bar (starting with the selected text, if any) and searches the active terminal.</summary>
		public void ShowFind()
		{
			var selected = View?.SelectedText ?? "";
			findBar.Show(selected.Length > 0 && !selected.Contains('\n') ? selected : null);
		}

		public bool FindBarOpen => findBar.IsOpen;
		public string FindBarStatus => findBar.StatusText;
		public int FindBarSearchCount => findBar.SearchCount;

		/// <summary>Types into the find bar like the keyboard: each character raises TextChanged and the search follows after a pause.</summary>
		public void TypeInFindBar(string text) => findBar.TypeQuery(text);

		/// <summary>Opens the find bar with this text and searches for it.</summary>
		public void ShowFind(string query) => findBar.Show(query);

		public void HideFind()
		{
			findBar.Hide();
			View?.ClearFind();
			View?.Focus();
		}

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
