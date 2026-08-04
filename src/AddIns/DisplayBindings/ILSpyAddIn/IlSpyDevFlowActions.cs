// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the hosted ILSpy addin
// without a native file-open dialog (which the WPF-embedded DevFlow agent can't see/control -
// same reasoning as OpenDevelopDevFlowActions.cs/WpfDesignDevFlowActions.cs). Static methods on a
// [DevFlowUIThread]-annotated class are auto-discovered by LeXtudio.DevFlow.Agent.Core and
// dispatched to the UI thread.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using AvalonDock.Layout;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.ILSpyAddIn
{
	[DevFlowUIThread]
	public static class IlSpyDevFlowActions
	{
		[DevFlowAction("od.ilspy.open-assembly", Description = "Open an assembly (.dll/.exe) into the hosted ILSpy AssemblyTreeModel, bypassing the native file dialog")]
		public static async Task<string> OpenAssemblyAsync(string path)
		{
			await IlSpyWorkspaceHost.OpenAssemblyAsync(path);
			return JsonSerializer.Serialize(new { opened = true, path });
		}

		[DevFlowAction("od.ilspy.show-pane", Description = "Activate a hosted ILSpy pane by title, re-registering it with the dock so AvalonDock deterministically materializes its anchorable content (needed before inspecting panes via od.ui.tree, since runtime-added panes can fail to materialize depending on dock state)")]
		public static string ShowPane(string title)
		{
			var pane = IlSpyWorkspaceHost.Panes.FirstOrDefault(p =>
				string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
			if (pane == null)
				return JsonSerializer.Serialize(new { found = false, title });

			// Remove + re-add to DockWorkspace.ToolPanes: the AnchorablesSource CollectionChanged
			// handler removes the old LayoutAnchorable from the layout and docks a fresh one, so the
			// pane's content is actually realized regardless of prior dock state.
			DockWorkspaceExtensibility.RemoveToolPane(pane);
			DockWorkspaceExtensibility.AddToolPane(pane);
			// The re-created anchorable only starts syncing the model's IsActive on change events
			// (AvalonDock subscribes to the content's INotifyPropertyChanged), and the model is
			// already active from the original Show() - so force a false->true transition, which
			// selects the anchorable in its pane and makes the content area actually render.
			pane.IsActive = false;
			pane.Show();

			// Belt and braces: activate via the DockingManager through reflection as well
			// (DockWorkspace is internal to the OpenDevelop assembly).
			var activation = TryActivateDockPane(pane);

			return JsonSerializer.Serialize(new { found = true, title = pane.Title, activation });
		}

		static string TryActivateDockPane(object pane)
		{
			try {
				var manager = GetDockingManager();
				if (manager == null)
					return "dockingManager not found";
				manager.GetType().GetProperty("ActiveContent")?.SetValue(manager, pane);
				return "activated";
			} catch (Exception ex) {
				return "failed: " + ex.Message;
			}
		}

		// DockWorkspace is `internal sealed` (src/Main/SharpDevelop/Workbench/DockWorkspace.cs), so
		// reaching it from this addin assembly needs reflection - but its `dockingManager` field is
		// a real, public AvalonDock.DockingManager (AvalonDock.dll IS referenced by this addin, for
		// the theme/tree-node-image work elsewhere), so once reflection gets the instance, everything
		// past that point - Layout, Descendents(), LayoutAnchorable, GetSide() - is ordinary typed
		// AvalonDock API, not reflection.
		static AvalonDock.DockingManager GetDockingManager()
		{
			var dockType = Type.GetType("ICSharpCode.SharpDevelop.Workbench.DockWorkspace, OpenDevelop", throwOnError: false);
			var current = dockType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
			return current?.GetType().GetField("dockingManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(current) as AvalonDock.DockingManager;
		}

		/// <summary>
		/// Where a pad's anchorable actually sits in the live AvalonDock layout right now - side
		/// (Left/Top/Right/Bottom), the named <c>LayoutAnchorablePane</c> it's docked into (matches
		/// the `Name="LeftPane"/"TopPane"/"BottomPane"` attributes in Layouts/ILSpy.xml), its index
		/// within that pane (tab order), and whether it's floating/auto-hidden/hidden instead of
		/// docked at all. Added to catch the "layout lost" failure mode the existing pane
		/// title/IsVisible/content assertions can't: a pad can be visible, correctly titled, and
		/// have real rendered content while floating in the wrong place, docked on the wrong side, or
		/// auto-hidden - none of which those assertions would notice (doc/technotes/ilspy.md).
		/// </summary>
		static object ReadPanePosition(string contentId)
		{
			var manager = GetDockingManager();
			if (manager?.Layout == null)
				return new { found = false };

			var anchorable = manager.Layout.Descendents()
				.OfType<AvalonDock.Layout.LayoutAnchorable>()
				.FirstOrDefault(a => string.Equals(a.ContentId, contentId, StringComparison.Ordinal));
			if (anchorable == null)
				return new { found = false };

			bool isFloating = anchorable.IsFloating;
			bool isAutoHidden = anchorable.IsAutoHidden;
			bool isHidden = anchorable.IsHidden;
			var pane = anchorable.Parent as AvalonDock.Layout.LayoutAnchorablePane;

			return new {
				found = true,
				paneName = pane?.Name,
				side = isFloating || pane == null ? (string)null : pane.GetSide().ToString(),
				tabIndex = pane?.Children.IndexOf(anchorable) ?? -1,
				siblingCount = pane?.Children.Count ?? 0,
				isFloating,
				isAutoHidden,
				isHidden
			};
		}

		[DevFlowAction("od.ilspy.pane-position", Description = "Report where a hosted ILSpy pad's anchorable actually sits in the live AvalonDock layout (side, named LayoutAnchorablePane, tab index, floating/auto-hidden/hidden) - used to catch the \"layout lost\" failure mode (pad visible/titled/content-correct but docked in the wrong place, or floating/auto-hidden) that plain visibility/title checks can't")]
		public static string GetPanePosition(string title)
		{
			var pane = IlSpyWorkspaceHost.Panes.FirstOrDefault(p =>
				string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
			if (pane == null)
				return JsonSerializer.Serialize(new { found = false, error = "No ILSpy pane titled '" + title + "'." });
			return JsonSerializer.Serialize(ReadPanePosition(pane.ContentId));
		}

		[DevFlowAction("od.ilspy.activate-pane", Description = "Activate a hosted ILSpy pane by title WITHOUT re-registering it (unlike od.ilspy.show-pane, which removes and re-adds the anchorable). Re-registration is destructive: after the first show-pane, switching to a different pane fails to materialize it at all, so this is the path to use when a pane needs activating more than once in a session")]
		public static string ActivatePane(string title)
		{
			var pane = IlSpyWorkspaceHost.Panes.FirstOrDefault(p =>
				string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
			if (pane == null)
				return JsonSerializer.Serialize(new { found = false, title });

			pane.Show();
			pane.IsActive = true;
			var activation = TryActivateDockPane(pane);
			return JsonSerializer.Serialize(new { found = true, title = pane.Title, isVisible = pane.IsVisible, activation });
		}

		[DevFlowAction("od.ilspy.activate-decompiled-document", Description = "Re-activate the decompiled-code document tab. Activating a tool pane (od.ilspy.show-pane) makes that pane the dock's ActiveContent and leaves the workbench with no active document, so a test that inspected a pad uses this to restore state for whatever runs next against the same shared app instance")]
		public static string ActivateDecompiledDocument()
		{
			IlSpyWorkspaceHost.ActivateDecompiledDocument();
			return JsonSerializer.Serialize(new {
				activeViewTypeName = SD.Workbench.ActiveViewContent?.GetType().FullName
			});
		}

		[DevFlowAction("od.ilspy.highlighting-status", Description = "Report whether ILAsm/Asm syntax highlighting is actually registered and applied (HighlightingManager.Instance.GetDefinition + the hosted DecompilerTextView's live textEditor.SyntaxHighlighting), used to verify the ILAsm-Mode.xshd/Asm-Mode.xshd resource-embedding fix - DecompilerTextView.RegisterHighlighting()'s own ExtensionMethods.RegisterHighlighting silently no-ops (no exception) when the embedded resource it looks up isn't found, so 'nothing crashed' is not evidence it worked")]
		public static string HighlightingStatus()
		{
			var manager = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance;
			return JsonSerializer.Serialize(new {
				ilAsmRegistered = manager.GetDefinition("ILAsm") != null,
				asmRegistered = manager.GetDefinition("Asm") != null,
				liveSyntaxHighlighting = IlSpyWorkspaceHost.DecompilerTextView.textEditor.SyntaxHighlighting?.Name
			});
		}

		[DevFlowAction("od.ilspy.analyze-selected", Description = "Analyze the Assemblies tree's currently selected member(s) into the Analyze pad - exactly what ILSpy's AnalyzeCommand does (SelectedNodes.OfType<IMemberTreeNode>() -> AnalyzerTreeViewModel.Analyze(node.Member)). Returns the Analyze pad's root children, i.e. whether the Assemblies -> Analyze pad linkage actually produced anything")]
		public static async Task<string> AnalyzeSelectedAsync()
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var analyzer = IlSpyWorkspaceHost.AnalyzerPane;

				var members = model.SelectedNodes
					.OfType<ICSharpCode.ILSpy.TreeNodes.IMemberTreeNode>()
					.ToArray();
				if (members.Length == 0) {
					return JsonSerializer.Serialize(new {
						success = false,
						error = "No IMemberTreeNode selected in the Assemblies tree - AnalyzeCommand would be disabled.",
						selectedNodeDetails = GetSelectedNodeDetails()
					});
				}

				foreach (var member in members)
					analyzer.Analyze(member.Member);

				// The root child is added synchronously; its own children (the actual analysis, e.g.
				// "Used By") load lazily on a background thread, so give them a chance to arrive.
				var roots = analyzer.Root.Children.ToArray();
				foreach (var root in roots)
					root.EnsureLazyChildren();
				for (int i = 0; i < 60 && roots.All(r => r.Children.Count == 0); i++)
					await Task.Delay(50);

				return JsonSerializer.Serialize(new {
					success = true,
					analyzed = members.Select(m => m.Member?.Name).ToArray(),
					rootChildren = analyzer.Root.Children.Select(c => new {
						text = c.Text?.ToString(),
						nodeType = c.GetType().Name,
						childCount = c.Children.Count,
						children = c.Children.Take(8).Select(g => g.Text?.ToString()).ToArray()
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.navigate-history", Description = "Walk the hosted ILSpy navigation history - what the Back/Forward toolbar buttons do (AssemblyTreeModel.NavigateHistory). Pass \"back\" or \"forward\". Returns CanNavigateBack/Forward and the resulting Assemblies tree selection, so a test can prove a jump is undoable")]
		public static async Task<string> NavigateHistoryAsync(string direction)
		{
			try {
				bool forward = string.Equals(direction, "forward", StringComparison.OrdinalIgnoreCase);
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				bool can = forward ? model.CanNavigateForward : model.CanNavigateBack;
				if (!can) {
					return JsonSerializer.Serialize(new {
						success = false,
						error = "Cannot navigate " + (forward ? "forward" : "back") + " - history is empty in that direction.",
						canNavigateBack = model.CanNavigateBack,
						canNavigateForward = model.CanNavigateForward,
						selectedNodeDetails = GetSelectedNodeDetails()
					});
				}

				string before = SelectionSignature();
				model.NavigateHistory(forward);
				for (int i = 0; i < 60 && SelectionSignature() == before; i++)
					await Task.Delay(50);

				return JsonSerializer.Serialize(new {
					success = true,
					direction = forward ? "forward" : "back",
					selectionChanged = SelectionSignature() != before,
					canNavigateBack = model.CanNavigateBack,
					canNavigateForward = model.CanNavigateForward,
					selectedNodeDetails = GetSelectedNodeDetails()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.toolbar-combos", Description = "Report the ILSpy toolbar dropdowns' real state (item count, selected item, visibility) - the UI tree exposes neither Items nor SelectedItem for a ComboBox, so rendering size alone cannot tell a populated dropdown from an empty one. Optionally select a value in one of them: pass e.g. \"Language\" and \"IL\" to drive the language dropdown exactly as the user would")]
		public static string ToolbarCombos(string comboType, string select)
		{
			try {
				var combos = new System.Collections.Generic.List<Commands.IlSpyToolBarComboBoxBase>();
				var app = System.Windows.Application.Current;
				if (app != null) {
					foreach (System.Windows.Window window in app.Windows)
						CollectCombos(window, combos);
				}

				string selected = null;
				if (!string.IsNullOrEmpty(comboType) && !string.IsNullOrEmpty(select)) {
					var target = combos.FirstOrDefault(c => c.GetType().Name.Contains(comboType, StringComparison.OrdinalIgnoreCase));
					if (target == null)
						return JsonSerializer.Serialize(new { success = false, error = "No toolbar dropdown matching '" + comboType + "'." });
					var match = target.Items.Cast<object>()
						.FirstOrDefault(i => string.Equals(ItemLabel(i), select, StringComparison.OrdinalIgnoreCase));
					if (match == null)
						return JsonSerializer.Serialize(new {
							success = false,
							error = "No item '" + select + "' in " + target.GetType().Name + ".",
							available = target.Items.Cast<object>().Select(ItemLabel).ToArray()
						});
					// Assign SelectedItem, i.e. exactly what picking it from the dropdown does.
					target.SelectedItem = match;
					selected = ItemLabel(match);
				}

				return JsonSerializer.Serialize(new {
					success = true,
					selected,
					combos = combos.Select(c => new {
						type = c.GetType().Name,
						itemCount = c.Items.Count,
						selectedItem = ItemLabel(c.SelectedItem),
						items = c.Items.Cast<object>().Take(12).Select(ItemLabel).ToArray(),
						isVisible = c.Visibility == System.Windows.Visibility.Visible,
						isEnabled = c.IsEnabled
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static string ItemLabel(object item)
		{
			return item switch {
				null => null,
				string s => s,
				ICSharpCode.ILSpy.Language language => language.Name,
				ICSharpCode.ILSpyX.LanguageVersion version => version.DisplayName,
				_ => item.ToString()
			};
		}

		static void CollectCombos(System.Windows.DependencyObject root, System.Collections.Generic.List<Commands.IlSpyToolBarComboBoxBase> into)
		{
			if (root is Commands.IlSpyToolBarComboBoxBase combo) {
				into.Add(combo);
				return;
			}
			int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++)
				CollectCombos(System.Windows.Media.VisualTreeHelper.GetChild(root, i), into);
		}

		[DevFlowAction("od.ilspy.api-visibility", Description = "Read/set the hosted ILSpy API-visibility level (LanguageSettings.ShowApiLevel: PublicOnly/PublicAndInternal/All - the radio group behind the three toolbar visibility toggles), and report each toggle's actual IsChecked. Pass an empty string to only read. The UI tree cannot show IsChecked for a CheckBox, hence this action")]
		public static string ApiVisibility(string level)
		{
			try {
				if (!string.IsNullOrEmpty(level)) {
					if (!Enum.TryParse<ICSharpCode.ILSpyX.ApiVisibility>(level, ignoreCase: true, out var parsed))
						return JsonSerializer.Serialize(new { success = false, error = "Unknown level '" + level + "'. Expected PublicOnly, PublicAndInternal or All." });
					IlSpyWorkspaceHost.SetApiVisibility(parsed);
					Commands.IlSpyApiVisibilityToggles.UpdateAll();
				}

				var toggles = new System.Collections.Generic.List<object>();
				var app = System.Windows.Application.Current;
				if (app != null) {
					foreach (System.Windows.Window window in app.Windows) {
						CollectToggles(window, toggles);
					}
				}
				return JsonSerializer.Serialize(new {
					success = true,
					level = IlSpyWorkspaceHost.GetApiVisibility().ToString(),
					toggles
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static void CollectToggles(System.Windows.DependencyObject root, System.Collections.Generic.List<object> into)
		{
			if (root is Commands.IlSpyApiVisibilityToggleBase toggle) {
				into.Add(new { type = toggle.GetType().Name, isChecked = toggle.IsChecked, isEnabled = toggle.IsEnabled });
				return;
			}
			int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++)
				CollectToggles(System.Windows.Media.VisualTreeHelper.GetChild(root, i), into);
		}

		[DevFlowAction("od.ilspy.theme", Description = "Inspect ICSharpCode.ILSpy.Themes.ThemeManager.Current's theme name and IsDarkTheme, to verify the theme bridge (IlSpyWorkspaceHost's IdeThemeService.ThemeChanged subscription) keeps it in sync with OpenDevelop's own IDE theme")]
		public static string GetTheme()
		{
			return JsonSerializer.Serialize(new {
				theme = ICSharpCode.ILSpy.Themes.ThemeManager.Current.Theme,
				isDarkTheme = ICSharpCode.ILSpy.Themes.ThemeManager.Current.IsDarkTheme
			});
		}

		[DevFlowAction("od.ilspy.is-initialized", Description = "Whether IlSpyWorkspaceHost.EnsureInitialized has already run, without triggering it - unlike od.ilspy.status/show-pane/open-assembly, which all initialize as a side effect. Used to verify layout-activation (od.workbench.switch-layout \"ILSpy\") actually initializes the addin by itself, without any other ILSpy action having run first")]
		public static string IsInitialized()
		{
			return JsonSerializer.Serialize(new { initialized = IlSpyWorkspaceHost.IsInitialized });
		}

		[DevFlowAction("od.ilspy.select-node", Description = "Jump to (select) the assembly tree node for the assembly with the given ShortName - the real AssemblyTreeModel.SelectNode path, which scrolls the node into view and refreshes the decompiled view")]
		public static string SelectNode(string shortName)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var assembly = model.AssemblyList.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
				if (assembly == null)
					return JsonSerializer.Serialize(new { success = false, error = "No loaded assembly named '" + shortName + "'." });
				var node = model.FindAssemblyNode(assembly);
				if (node == null)
					return JsonSerializer.Serialize(new { success = false, error = "Assembly tree node not found for '" + shortName + "'." });
				model.SelectNode(node);
				bool selected = model.SelectedItems.Contains(node);
				return JsonSerializer.Serialize(new {
					success = true,
					shortName,
					selected,
					nodeText = node.Text?.ToString(),
					selectedNodes = GetSelectedNodeNames()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.select-nodes", Description = "Select several assembly-root tree nodes at once (comma-separated ShortNames), exercising the real multi-selection path (AssemblyTreeModel.SelectNodes) - used to verify multi-node native document routing (doc/technotes/ilspy.md \"Unify C# document hosting\")")]
		public static string SelectNodes(string commaSeparatedShortNames)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var shortNames = commaSeparatedShortNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
				var nodes = new List<ICSharpCode.ILSpyX.TreeView.SharpTreeNode>();
				foreach (var shortName in shortNames) {
					var assembly = model.AssemblyList.GetAssemblies()
						.FirstOrDefault(a => string.Equals(a.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
					if (assembly == null)
						return JsonSerializer.Serialize(new { success = false, error = "No loaded assembly named '" + shortName + "'." });
					var node = model.FindAssemblyNode(assembly);
					if (node == null)
						return JsonSerializer.Serialize(new { success = false, error = "Assembly tree node not found for '" + shortName + "'." });
					nodes.Add(node);
				}
				model.SelectNodes(nodes);
				return JsonSerializer.Serialize(new {
					success = true,
					requested = shortNames,
					selectedNodes = GetSelectedNodeNames()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static string[] GetSelectedNodeNames()
		{
			return IlSpyWorkspaceHost.AssemblyTreeModel.SelectedItems
				.OfType<ICSharpCode.ILSpy.TreeNodes.AssemblyTreeNode>()
				.Select(n => n.LoadedAssembly.ShortName)
				.ToArray();
		}

		// GetSelectedNodeNames above only reports AssemblyTreeNode, so it goes empty the moment the
		// selection moves to a type/member node - which is exactly what search-result navigation
		// does. Report every selected node's type + rendered text instead.
		static object[] GetSelectedNodeDetails()
		{
			return IlSpyWorkspaceHost.AssemblyTreeModel.SelectedItems
				.Select(n => (object)new { nodeType = n.GetType().Name, text = n.Text?.ToString() })
				.ToArray();
		}

		// Returns the realized SearchPane view, activating the pane only if it isn't realized yet.
		// ShowPane only re-registers the anchorable; WPF still has to run measure/arrange and realize
		// the DataTemplate before the view exists in the visual tree, and since these actions run
		// *on* the UI thread that can only happen if we yield - hence awaiting in the poll loop
		// rather than looking exactly once.
		static async Task<ICSharpCode.ILSpy.Search.SearchPane> EnsureSearchPaneAsync()
		{
			var view = FindInAnyWindow<ICSharpCode.ILSpy.Search.SearchPane>();
			if (view != null)
				return view;

			// The view only exists in the visual tree while the Search tab is the selected one in
			// its docked pane - any intermediate step that activates another pad tears it down, so
			// activate the pane FIRST (non-destructively; the remove-then-re-add ShowPane path would
			// destroy the very view this call is trying to find). ActivatePane just re-selects the
			// existing anchorable, which re-realizes the view.
			IlSpyWorkspaceHost.ActivatePane("Search");
			for (int i = 0; i < 60; i++) {
				await Task.Delay(50);
				view = FindInAnyWindow<ICSharpCode.ILSpy.Search.SearchPane>();
				if (view != null)
					return view;
			}
			return null;
		}

		static string SelectionSignature()
		{
			return string.Join("|", IlSpyWorkspaceHost.AssemblyTreeModel.SelectedItems
				.Select(n => n.GetType().Name + ":" + n.Text));
		}

		static T FindVisualChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
		{
			if (root == null)
				return null;
			if (root is T hit)
				return hit;
			int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++) {
				var found = FindVisualChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
				if (found != null)
					return found;
			}
			return null;
		}

		// Walking down from Application.Current.MainWindow alone is not enough: a docked pane's
		// content can sit under a different visual root (AvalonDock floating windows, and the
		// DevFlow UI tree itself reports these panes under a synthetic root rather than the main
		// window). Search every open window, main window first.
		static T FindInAnyWindow<T>() where T : System.Windows.DependencyObject
		{
			var app = System.Windows.Application.Current;
			if (app == null)
				return null;
			var hit = FindVisualChild<T>(app.MainWindow);
			if (hit != null)
				return hit;
			foreach (System.Windows.Window window in app.Windows) {
				hit = FindVisualChild<T>(window);
				if (hit != null)
					return hit;
			}
			return null;
		}

		[DevFlowAction("od.ilspy.search", Description = "Run a real ILSpy search: activates the Search pane, sets SearchPaneModel.SearchTerm (whose binding drives searchBox.Text -> TextChanged -> SearchPane.StartSearch, the same path typing does) and waits for SearchPane.Results to fill. Returns the result rows. Note the Search pane must be materialized because Results lives on the view and is pumped by CompositionTarget.Rendering")]
		public static async Task<string> SearchAsync(string term)
		{
			try {
				var model = IlSpyWorkspaceHost.Panes.OfType<ICSharpCode.ILSpy.Search.SearchPaneModel>().FirstOrDefault();
				if (model == null)
					return JsonSerializer.Serialize(new { success = false, error = "SearchPaneModel not registered." });

				// Only activate the pane if its view isn't already realized: ShowPane is a
				// remove-then-re-add, so calling it unconditionally would destroy the very view (and
				// its Results collection) a previous od.ilspy.search just materialized - which made
				// a second search call fail outright.
				var view = await EnsureSearchPaneAsync();
				if (view == null)
					return JsonSerializer.Serialize(new { success = false, error = "SearchPane view never appeared in any window's visual tree (pane not materialized?).", windowCount = System.Windows.Application.Current?.Windows.Count, hasMainWindow = System.Windows.Application.Current?.MainWindow != null });

				// Searching the *same* term twice must still re-run: SetProperty ignores an equal
				// value, so no PropertyChanged -> no searchBox TextChanged -> StartSearch never runs.
				// Force the value to actually change first. (Do NOT clear Results here - StartSearch
				// clears them itself, and clearing without re-running left the pane permanently empty.)
				if (string.Equals(model.SearchTerm, term, StringComparison.Ordinal))
					model.SearchTerm = string.Empty;
				model.SearchTerm = term;

				// await Task.Delay (not a blocking spin) so the dispatcher keeps pumping - the search
				// runs async and its results are moved into Results from CompositionTarget.Rendering.
				for (int i = 0; i < 120 && view.Results.Count == 0; i++)
					await Task.Delay(50);

				return JsonSerializer.Serialize(new {
					success = true,
					term,
					count = view.Results.Count,
					results = view.Results.Take(20).Select(r => new {
						name = r.Name,
						location = r.Location,
						resultType = r.GetType().Name,
						hasReference = r.Reference != null
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.search-activate", Description = "Activate the Nth search result exactly as double-clicking it does - SearchPane.JumpToSelectedItem's only action is MessageBus.Send(new NavigateToReferenceEventArgs(result.Reference)), which AssemblyTreeModel subscribes to and turns into JumpToReferenceAsync -> SelectNode. Returns the resulting Assemblies-tree selection, i.e. whether the jump landed")]
		public static async Task<string> SearchActivateAsync(int index)
		{
			try {
				var view = await EnsureSearchPaneAsync();
				if (view == null)
					return JsonSerializer.Serialize(new { success = false, error = "SearchPane view not found - call od.ilspy.search first." });
				if (index < 0 || index >= view.Results.Count)
					return JsonSerializer.Serialize(new { success = false, error = "index " + index + " out of range (" + view.Results.Count + " results)." });

				var result = view.Results[index];
				if (result.Reference == null)
					return JsonSerializer.Serialize(new { success = false, error = "result '" + result.Name + "' has no navigable Reference." });

				var before = GetSelectedNodeDetails();
				// Opening an assembly already leaves its own node selected, so "wait until the
				// selection is non-empty" would return instantly and prove nothing - wait until it
				// actually *changes*.
				string beforeSignature = SelectionSignature();

				ICSharpCode.ILSpy.Util.MessageBus.Send(view,
					new ICSharpCode.ILSpy.Util.NavigateToReferenceEventArgs(result.Reference));

				// JumpToReferenceAsync is async (it may lazy-load tree children to find the node).
				bool changed = false;
				for (int i = 0; i < 120; i++) {
					await Task.Delay(50);
					if (SelectionSignature() != beforeSignature) {
						changed = true;
						break;
					}
				}

				return JsonSerializer.Serialize(new {
					success = true,
					activatedName = result.Name,
					activatedLocation = result.Location,
					selectionChanged = changed,
					selectionBefore = before,
					selectedNodeDetails = GetSelectedNodeDetails()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.navigate-to-type", Description = "Directly exercise NavigateToDecompiledEntityService.NavigateTo (the call IlSpyWorkspaceHost's unwired OnSelectionChangedAsync would make for a single selected TypeTreeNode - see doc/technotes/ilspy.md \"Unify C# document hosting\") - opens/reuses a native DecompiledViewContent document for the given assembly ShortName + fully-qualified type reflection name")]
		public static string NavigateToType(string shortName, string typeReflectionName)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var assembly = model.AssemblyList.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
				if (assembly == null)
					return JsonSerializer.Serialize(new { success = false, error = "No loaded assembly named '" + shortName + "'." });

				var assemblyFile = ICSharpCode.Core.FileName.Create(assembly.FileName);
				NavigateToDecompiledEntityService.NavigateTo(assemblyFile, typeReflectionName, null);

				var view = SD.Workbench.ViewContentCollection.OfType<DecompiledViewContent>()
					.FirstOrDefault(v => v.DecompiledTypeName.AssemblyFile == assemblyFile
						&& v.DecompiledTypeName.Type == new ICSharpCode.TypeSystem.TopLevelTypeName(typeReflectionName));
				string text = (view?.Control as ICSharpCode.AvalonEdit.AddIn.CodeEditor)?.Document.Text;
				return JsonSerializer.Serialize(new {
					success = true,
					shortName,
					typeReflectionName,
					found = view != null,
					titleName = view?.TitleName,
					fileName = view?.PrimaryFileName?.ToString(),
					isReadOnly = view?.IsReadOnly,
					activeViewTitle = SD.Workbench.ActiveViewContent?.TitleName,
					decompiledTextLength = text?.Length ?? 0,
					decompiledTextSnippet = text?.Length > 500 ? text[..500] : text
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.navigate-to-module", Description = "Directly exercise NavigateToDecompiledEntityService.NavigateToModule (the call OnSelectionChangedAsync makes for a single selected AssemblyTreeNode) - opens/reuses the native whole-module DecompiledViewContent document. Unlike routing through od.ilspy.select-node, this bypasses the real ILSpy tree control's own focus/ActiveContent-stealing behavior (interacting with any docked tool pane can leave a document's SelectWindow() unable to reclaim the dock's ActiveContent - a pre-existing ILSpy/AvalonDock quirk, not something this addin's routing introduced), so it reliably re-activates the module document regardless of what else was focused beforehand")]
		public static string NavigateToModule(string shortName)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var assembly = model.AssemblyList.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
				if (assembly == null)
					return JsonSerializer.Serialize(new { success = false, error = "No loaded assembly named '" + shortName + "'." });

				var assemblyFile = ICSharpCode.Core.FileName.Create(assembly.FileName);
				NavigateToDecompiledEntityService.NavigateToModule(assemblyFile);

				var view = SD.Workbench.ViewContentCollection.OfType<DecompiledViewContent>()
					.FirstOrDefault(v => v.DecompiledTypeName.AssemblyFile == assemblyFile && v.DecompiledTypeName.IsWholeModule);
				string text = (view?.Control as ICSharpCode.AvalonEdit.AddIn.CodeEditor)?.Document.Text;
				return JsonSerializer.Serialize(new {
					success = true,
					shortName,
					found = view != null,
					titleName = view?.TitleName,
					fileName = view?.PrimaryFileName?.ToString(),
					activeViewTitle = SD.Workbench.ActiveViewContent?.TitleName,
					decompiledTextLength = text?.Length ?? 0,
					decompiledTextSnippet = text?.Length > 500 ? text[..500] : text
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.click-reference", Description = "Exercise the actual click-a-reference-to-navigate behavior on the currently active native decompiled document (DecompiledViewContent.TryNavigateAtOffset) - finds the given substring's offset in the document text and clicks there, exactly as a real Ctrl+Click would resolve once GetPositionFromPoint has mapped a pixel to that same offset (no synthetic-mouse-event capability exists in this environment, so this verifies everything downstream of that standard AvalonEdit API call). occurrence selects which match of the substring to click (0 = first)")]
		public static async Task<string> ClickReference(string substring, int occurrence)
		{
			try {
				if (SD.Workbench.ActiveViewContent is not DecompiledViewContent view)
					return JsonSerializer.Serialize(new { success = false, error = "Active view is not a DecompiledViewContent.", activeViewType = SD.Workbench.ActiveViewContent?.GetType().FullName });

				string text = (view.Control as ICSharpCode.AvalonEdit.AddIn.CodeEditor)?.Document.Text ?? "";
				int offset = -1;
				for (int i = 0; i <= occurrence; i++) {
					offset = text.IndexOf(substring, offset + 1, StringComparison.Ordinal);
					if (offset < 0)
						return JsonSerializer.Serialize(new { success = false, error = "Occurrence " + occurrence + " of '" + substring + "' not found (found fewer matches)." });
				}

				var before = new { activeViewTitle = SD.Workbench.ActiveViewContent?.TitleName, activeViewFile = SD.Workbench.ActiveViewContent?.PrimaryFileName?.ToString() };
				bool navigated = view.TryNavigateAtOffset(offset);
				string jumpInfo = "";
				int caretLineAfter = -1, caretColAfter = -1;
				for (int i = 0; i < 50; i++) {
					await Task.Delay(100);
					var av = SD.Workbench.ActiveViewContent;
					if (av == null)
						continue;
					var editor = av.GetService<ITextEditor>();
					if (editor?.Caret != null) {
						caretLineAfter = editor.Caret.Line;
						caretColAfter = editor.Caret.Column;
						if (caretLineAfter > 1)
							break;
					}
				}
				if (SD.Workbench.ActiveViewContent is DecompiledViewContent dv) {
					jumpInfo = $"{dv.DecompiledTypeName.Type};locs={dv.MemberLocationCount};";
				}

				return JsonSerializer.Serialize(new {
					success = true,
					substring,
					offset,
					navigated,
					caretLineAfter,
					caretColAfter,
					jumpInfo,
					before,
					after = new { activeViewTitle = SD.Workbench.ActiveViewContent?.TitleName, activeViewFile = SD.Workbench.ActiveViewContent?.PrimaryFileName?.ToString() }
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.decompile-type", Description = "Call ILSpyDecompilerService.DecompileType directly (bypassing the workbench/UI) for the given assembly ShortName + fully-qualified type reflection name, and report the decompiled text plus captured reference spans - used to verify the ReferenceTrackingTextOutput rewrite (doc/technotes/ilspy.md \"Unify C# document hosting\" - reference hyperlink navigation) didn't change decompiled text formatting and correctly captures use-site references")]
		public static string DecompileType(string shortName, string typeReflectionName)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				var assembly = model.AssemblyList.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
				if (assembly == null)
					return JsonSerializer.Serialize(new { success = false, error = "No loaded assembly named '" + shortName + "'." });

				var assemblyFile = ICSharpCode.Core.FileName.Create(assembly.FileName);
				var target = new DecompiledTypeReference(assemblyFile, new ICSharpCode.TypeSystem.TopLevelTypeName(typeReflectionName));
				var result = ILSpyDecompilerService.DecompileType(target);

				return JsonSerializer.Serialize(new {
					success = true,
					outputLength = result.Output.Length,
					outputSnippet = result.Output.Length > 1500 ? result.Output[..1500] : result.Output,
					memberLocationCount = result.MemberLocations.Count,
					referenceCount = result.References.Count,
					debugSymbolCount = result.DebugSymbols.Count,
					debugSymbolSequencePoints = result.DebugSymbols.Values.Sum(s => s.SequencePoints.Count),
					sampleReferences = result.References.Take(10).Select(r => new {
						r.Offset,
						r.Length,
						text = result.Output.Substring(r.Offset, Math.Min(r.Length, Math.Max(0, result.Output.Length - r.Offset))),
						r.TopLevelTypeReflectionName,
						r.MemberKey
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.select-type", Description = "Jump to (select) a top-level type node under the given (or the currently selected) assembly - the real AssemblyTreeModel.SelectNode path, which updates the Decompiled Code tab")]
		public static string SelectType(string typeName, string assemblyShortName = null)
		{
			try {
				var model = IlSpyWorkspaceHost.AssemblyTreeModel;
				ICSharpCode.ILSpy.TreeNodes.AssemblyTreeNode assemblyNode;
				if (!string.IsNullOrWhiteSpace(assemblyShortName)) {
					var assembly = model.AssemblyList.GetAssemblies()
						.FirstOrDefault(a => string.Equals(a.ShortName, assemblyShortName, StringComparison.OrdinalIgnoreCase));
					assemblyNode = assembly != null ? model.FindAssemblyNode(assembly) : null;
					if (assemblyNode == null)
						return JsonSerializer.Serialize(new { success = false, error = "Assembly '" + assemblyShortName + "' not found." });
				} else {
					assemblyNode = model.SelectedItems.OfType<ICSharpCode.ILSpy.TreeNodes.AssemblyTreeNode>().FirstOrDefault()
						?? model.AssemblyList.GetAssemblies().Select(model.FindAssemblyNode).FirstOrDefault();
					if (assemblyNode == null)
						return JsonSerializer.Serialize(new { success = false, error = "No assembly tree node available." });
				}

				assemblyNode.EnsureLazyChildren();
				var typeNode = assemblyNode.Children.OfType<ICSharpCode.ILSpy.TreeNodes.TypeTreeNode>()
					.FirstOrDefault(n => string.Equals(n.Text?.ToString(), typeName, StringComparison.OrdinalIgnoreCase));
				if (typeNode == null)
					return JsonSerializer.Serialize(new { success = false, error = "Type '" + typeName + "' not found in assembly tree.", available = assemblyNode.Children.OfType<ICSharpCode.ILSpy.TreeNodes.TypeTreeNode>().Select(n => n.Text?.ToString()).ToArray() });
				model.SelectNode(typeNode);
				return JsonSerializer.Serialize(new {
					success = true,
					typeName,
					selected = model.SelectedItems.Contains(typeNode),
					nodeText = typeNode.Text?.ToString(),
					selectedNodes = GetSelectedNodeNames()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.search", Description = "Set the hosted ILSpy Search pane's search term - the real SearchBox binding fires the actual search (SearchPane.StartSearch via TextChanged), like typing in the real app")]
		public static string Search(string term)
		{
			try {
				IlSpyWorkspaceHost.SearchPane.SearchTerm = term;
				return JsonSerializer.Serialize(new { success = true, term });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static ICSharpCode.ILSpy.Search.SearchPane FindSearchPaneControl()
		{
			// WindowCollection is not IEnumerable<Window>, so it can't be ?? -ed with an empty
			// sequence - guard the null Application instead.
			var app = System.Windows.Application.Current;
			if (app == null)
				return null;
			foreach (System.Windows.Window window in app.Windows) {
				var found = FindVisualChild<ICSharpCode.ILSpy.Search.SearchPane>(window);
				if (found != null)
					return found;
			}
			return null;
		}

		[DevFlowAction("od.ilspy.search-results", Description = "Return the hosted Search pane's current results (the real SearchPane.Results collection) - poll after od.ilspy.search until count > 0")]
		public static string SearchResults()
		{
			try {
				var pane = FindSearchPaneControl();
				if (pane == null)
					return JsonSerializer.Serialize(new { success = false, error = "Search pane control not materialized yet." });
				return JsonSerializer.Serialize(new {
					success = true,
					count = pane.Results.Count,
					results = pane.Results.Select(r => new { name = r.Name, location = r.Location }).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.analyze", Description = "Run the real ILSpy Analyze command (AnalyzerTreeViewModel.AnalyzeCommand) over the current assembly tree selection, populating the Analyzer pane")]
		public static string Analyze()
		{
			try {
				var analyzer = IlSpyWorkspaceHost.AnalyzerPane;
				var command = analyzer.AssociatedCommand;
				if (command == null)
					return JsonSerializer.Serialize(new { success = false, error = "Analyzer has no associated command." });
				command.Execute(null);
				return JsonSerializer.Serialize(new { success = true });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.analyzer-tree", Description = "Return the Analyzer pane's current tree (AnalyzerRootNode children) - poll after od.ilspy.analyze until count > 0")]
		public static string AnalyzerTree()
		{
			try {
				var root = IlSpyWorkspaceHost.AnalyzerPane.Root;
				root.EnsureLazyChildren();
				return JsonSerializer.Serialize(new {
					success = true,
					count = root.Children.Count,
					nodes = root.Children.Select(n => n.Text?.ToString()).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.ilspy.status", Description = "Inspect the hosted ILSpy pads (Assemblies/Search/Analyzer/Decompiled Code): whether they're registered/visible, the assembly tree's loaded assemblies, the currently selected tree node, and a snippet of the decompiled code pane")]
		public static string GetStatus()
		{
			var panes = IlSpyWorkspaceHost.Panes
				.Select(p => new { title = p.Title, contentId = p.ContentId, isVisible = p.IsVisible, isActive = p.IsActive, position = ReadPanePosition(p.ContentId) })
				.ToArray();

			var assemblyTreeModel = IlSpyWorkspaceHost.AssemblyTreeModel;
			var loadedAssemblies = assemblyTreeModel.AssemblyList.GetAssemblies()
				.Select(a => a.ShortName)
				.ToArray();

			// A tree selection now decompiles through one of several places depending on what was
			// selected (doc/technotes/ilspy.md "Unify C# document hosting"): a native
			// DecompiledViewContent document (single TypeTreeNode/AssemblyTreeNode),
			// DecompiledSelectionViewContent (multi-select and member-/namespace-node selections),
			// or the old bespoke DecompilerTextView pane (only whatever's left, if anything). Report
			// from whichever the active view actually is if it's one of the native documents
			// (Control is a CodeEditor); otherwise DON'T fall straight to the bespoke pane - tree
			// selection is subject to a pre-existing AvalonDock "focus loss" quirk (documented on
			// RefreshDecompiledViewAsync's callers) that can leave a tool pane, not the document,
			// holding ActiveViewContent even though the selection document itself refreshed
			// correctly - so check the reused selection document directly before giving up on native
			// content and reporting whatever the bespoke pane happens to still hold (which could be
			// stale from an earlier, unrelated selection).
			string decompiledText = SD.Workbench.ActiveViewContent?.Control is ICSharpCode.AvalonEdit.AddIn.CodeEditor nativeEditor
				? nativeEditor.Document.Text
				: IlSpyWorkspaceHost.DecompiledSelectionView?.CurrentText
					?? IlSpyWorkspaceHost.DecompilerTextView.textEditor.Text;

			return JsonSerializer.Serialize(new {
				panes,
				loadedAssemblies,
				selectedNodes = GetSelectedNodeNames(),
				selectedNodeDetails = GetSelectedNodeDetails(),
				decompiledTextLength = decompiledText?.Length ?? 0,
				decompiledTextSnippet = decompiledText?.Length > 2000 ? decompiledText[..2000] : decompiledText
			});
		}

		[DevFlowAction("od.ilspy.foldings", Description = "List the hosted DecompilerTextView's current AvalonEdit foldings (offsets/title/IsFolded) - used to verify a folded region's placeholder Title without needing a screenshot (see doc/technotes/ilspy.md \"folded using-block placeholder\")")]
		public static string GetFoldings()
		{
			var fm = IlSpyWorkspaceHost.DecompilerTextView.FoldingManager;
			if (fm == null)
				return JsonSerializer.Serialize(new { foldingManager = (string)null });

			var foldings = fm.AllFoldings
				.Select(f => new { f.StartOffset, f.EndOffset, f.Title, f.IsFolded })
				.ToArray();
			return JsonSerializer.Serialize(new { count = foldings.Length, foldings });
		}
	}
}
