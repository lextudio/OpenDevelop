#nullable enable
// Headless inspection of the WPF Project Browser's real AddIn-tree context menus for any node kind.
// od.project-context-menu (OpenDevelopDevFlowActions.cs) covers only project nodes and flattens
// labels; this one also expands submenus and reports which .addin contributed each entry, which is
// what telling a legacy ICSharpCode.SharpDevelop.addin duplicate from the new entry needs
// (doc/technotes/solution-explorer.md, "Legacy menu codons").

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.Project.Dialogs;
using ICSharpCode.SharpDevelop.Gui.OptionPanels;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class ProjectBrowserDevFlowActions
	{
		[DevFlowAction("od.project-browser.sticky-layout", Description = "Expand the real Projects tree if requested, optionally scroll it to a precise DIP offset, then return the measured ScrollViewer viewport, sticky overlay and each real/pinned header rectangle. Consecutive calls at the same offset are suitable for layout-stability assertions.")]
		public static async Task<string> GetStickyLayout(double? verticalOffset = null, bool expandAll = false)
		{
			try {
				var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
				await viewModel.WaitForCurrentRefreshAsync();
				if (viewModel.Content is not ProjectBrowserView view)
					return JsonSerializer.Serialize(new { success = false, error = "The Project Browser view is not realized. Show ProjectBrowserPad first." });

				if (expandAll)
					view.StickyTree.ExpandAll();
				if (verticalOffset.HasValue)
					view.StickyTree.ScrollToVerticalOffset(verticalOffset.Value);

				// ScrollChanged schedules its recomputation at Render priority. Awaiting two render turns
				// makes the returned snapshot a post-layout observation, not a transient scroll sample.
				await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
				await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
				var snapshot = view.StickyTree.GetLayoutSnapshot();
				return JsonSerializer.Serialize(new {
					success = true,
					verticalOffset = snapshot.VerticalOffset,
					viewport = DescribeRect(snapshot.Viewport),
					overlayPanel = DescribeRect(snapshot.OverlayPanel),
					rows = snapshot.Rows.Select(row => new {
						name = row.Name,
						realHeader = DescribeRect(row.RealHeader),
						overlay = DescribeRect(row.Overlay),
						leftDelta = row.Overlay.X - row.RealHeader.X,
						heightDelta = row.Overlay.Height - row.RealHeader.Height
					}).ToArray()
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static object DescribeRect(Rect rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };

		[DevFlowAction("od.project-browser.context-menu", Description = "Build the real context menu of the first Project Browser node of the given kind (Solution, Project, Folder, File, Reference, PackageReference), optionally matching its name, and return every visible entry with its submenus and contributing .addin")]
		public static async Task<string> GetContextMenu(string kind, string? name = null)
		{
			try {
				if (!Enum.TryParse<ProjectBrowserNodeKind>(kind, true, out var nodeKind))
					return JsonSerializer.Serialize(new { success = false, error = "Unknown node kind '" + kind + "'." });

				var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
				await viewModel.WaitForCurrentRefreshAsync();
				var node = FindNode(viewModel.RootNodes, n => n.Kind == nodeKind
					&& (string.IsNullOrEmpty(name) || string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)));
				if (node == null)
					return JsonSerializer.Serialize(new { success = false, error = "No " + kind + " node" + (string.IsNullOrEmpty(name) ? "" : " named '" + name + "'") + "." });

				viewModel.SelectedNode = node;
				var context = node.ToContext();
				var items = ICSharpCode.Core.Presentation.MenuService.CreateMenuItems(null, context, context.ContextMenuPath, "ContextMenu");
				return JsonSerializer.Serialize(new {
					success = true,
					node = node.Name,
					path = context.ContextMenuPath,
					items = Describe(items)
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.project-browser.select", Description = "Select the first Project Browser node of the given kind (Solution, Project, SolutionFolder, SolutionItem, Folder, File, ...), optionally matching its name, so a following od.menu.invoke acts on it")]
		public static async Task<string> SelectNode(string kind, string? name = null)
		{
			try {
				if (!Enum.TryParse<ProjectBrowserNodeKind>(kind, true, out var nodeKind))
					return JsonSerializer.Serialize(new { success = false, error = "Unknown node kind '" + kind + "'." });
				var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
				await viewModel.WaitForCurrentRefreshAsync();
				var node = FindNode(viewModel.RootNodes, n => n.Kind == nodeKind
					&& (string.IsNullOrEmpty(name) || string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)));
				if (node == null)
					return JsonSerializer.Serialize(new { success = false, error = "No " + kind + " node" + (string.IsNullOrEmpty(name) ? "" : " named '" + name + "'") + "." });
				viewModel.SelectedNode = node;
				return JsonSerializer.Serialize(new { success = true, node = node.Name, kind = node.Kind.ToString() });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.project-browser.open-selected", Description = "Activate the selected Project Browser node as a double-click does and report any project options view")]
		public static string OpenSelectedNode()
		{
			var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
			viewModel.OpenSelected();
			var options = SD.Workbench.ViewContentCollection.OfType<ProjectOptionsView>()
				.FirstOrDefault(view => view.Project?.FileName == SD.ProjectService.CurrentProject?.FileName);
			return JsonSerializer.Serialize(new {
				projectOptionsOpen = options != null,
				projectName = options?.Project?.Name,
				tabs = (options?.Control as ICSharpCode.SharpDevelop.Gui.TabbedOptions)?.Items
					.OfType<TabItem>().Select(tab => tab.Header?.ToString()).ToArray() ?? Array.Empty<string>(),
				activeView = SD.Workbench.ActiveViewContent?.GetType().Name
			});
		}

		[DevFlowAction("od.project-browser.invoke-project-options", Description = "Invoke Project Options through the Project Browser's real AddIn-tree context-menu item and report the opened options view")]
		public static async Task<string> InvokeProjectOptions(string? projectName = null)
		{
			try {
				var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
				await viewModel.WaitForCurrentRefreshAsync();
				var node = FindNode(viewModel.RootNodes, n => n.Kind == ProjectBrowserNodeKind.Project
					&& (string.IsNullOrEmpty(projectName) || string.Equals(n.Name, projectName, StringComparison.OrdinalIgnoreCase)));
				if (node == null)
					return JsonSerializer.Serialize(new { success = false, error = "No matching project node." });

				viewModel.SelectedNode = node;
				var context = node.ToContext();
				var menuItem = FindMenuItem(ICSharpCode.Core.Presentation.MenuService.CreateMenuItems(
					null, context, context.ContextMenuPath, "ContextMenu"), "Project Options...");
				if (menuItem == null)
					return JsonSerializer.Serialize(new { success = false, error = "Project Options menu item is unavailable." });

				// Click it the way WPF does for the right-click popup: MenuItem.OnClick raises Click AND
				// executes the item's Command. Raising ClickEvent alone runs only Click handlers, so the
				// codon's command (ViewProjectOptions) never ran. Unlike od.menu.invoke, this resolves
				// the class= attribute through this add-in's Runtime imports.
				typeof(MenuItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
					.Invoke(menuItem, null);
				await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
				var project = SD.ProjectService.CurrentProject;
				var options = SD.Workbench.ViewContentCollection.OfType<ProjectOptionsView>()
					.FirstOrDefault(view => view.Project == project);
				return JsonSerializer.Serialize(new {
					success = options != null,
					projectOptionsOpen = options != null,
					projectName = options?.Project?.Name,
					error = options == null ? "Project Options did not open." : null
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static MenuItem? FindMenuItem(IEnumerable items, string header)
		{
			foreach (var item in items.OfType<MenuItem>()) {
				if (string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal))
					return item;
				var nested = FindMenuItem(item.Items, header);
				if (nested != null)
					return nested;
			}
			return null;
		}

		[DevFlowAction("od.project-options.configure-debug-host", Description = "Configure the active project's Debug options through its options panel and save the project")]
		public static async Task<string> ConfigureDebugHost(string program, string workingDirectory, string? arguments = null)
		{
			var project = SD.ProjectService.CurrentProject;
			var options = SD.Workbench.ViewContentCollection.OfType<ProjectOptionsView>()
				.FirstOrDefault(view => view.Project == project);
			if (options?.Control is not ICSharpCode.SharpDevelop.Gui.TabbedOptions tabs)
				return JsonSerializer.Serialize(new { success = false, error = "Project Options is not open for the selected project." });
			var debugTab = tabs.Items.OfType<TabItem>().FirstOrDefault(tab =>
				tab.Header?.ToString()?.Contains("Debug", StringComparison.OrdinalIgnoreCase) == true);
			if (debugTab == null)
				return JsonSerializer.Serialize(new { success = false, error = "Debug options tab is unavailable." });
			tabs.SelectedItem = debugTab;
			await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
			var panel = tabs.OptionPanels.OfType<DebugOptions>().FirstOrDefault();
			if (panel == null)
				return JsonSerializer.Serialize(new { success = false, error = "Debug options panel did not load." });
			panel.StartAction.Value = ICSharpCode.SharpDevelop.Project.StartAction.Program;
			panel.StartProgram.Value = program;
			panel.StartWorkingDirectory.Value = workingDirectory;
			if (arguments != null)
				panel.StartArguments.Value = arguments;
			options.Save();
			return JsonSerializer.Serialize(new {
				success = true,
				project = project.Name,
				libraryHintVisible = (panel.FindName("ClassLibraryHint") as TextBlock)?.Visibility == Visibility.Visible,
				startAction = panel.StartAction.Value.ToString(),
				startProgram = panel.StartProgram.Value,
				workingDirectory = panel.StartWorkingDirectory.Value,
				startable = project.IsStartable
			});
		}

		[DevFlowAction("od.project-options.exercise-page", Description = "Exercise one C# project options page through its loaded panel, optionally save a representative setting, and report the value")]
		public static async Task<string> ExercisePage(string page, string? value = null)
		{
			try {
				var project = SD.ProjectService.CurrentProject;
				var options = SD.Workbench.ViewContentCollection.OfType<ProjectOptionsView>()
					.FirstOrDefault(view => view.Project == project);
				if (options?.Control is not ICSharpCode.SharpDevelop.Gui.TabbedOptions tabs)
					return JsonSerializer.Serialize(new { success = false, error = "Project Options is not open." });
				var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
					item.Header?.ToString()?.Contains(page, StringComparison.OrdinalIgnoreCase) == true);
				if (tab == null)
					return JsonSerializer.Serialize(new { success = false, error = "Page not found: " + page });
				tabs.SelectedItem = tab;
				await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
				var panel = tabs.OptionPanels.FirstOrDefault(item => ReferenceEquals(item.Control, tab.Content));
				if (panel == null)
					return JsonSerializer.Serialize(new { success = false, error = "Page did not load: " + page });
				string? actual;
				switch (panel) {
					case ApplicationSettings application:
						if (value != null) application.AssemblyName.Value = value;
						actual = application.AssemblyName.Value;
						break;
					case ReferencePaths references:
						var editor = references.FindName("editor") as ICSharpCode.SharpDevelop.Gui.StringListEditor;
						if (editor == null) throw new InvalidOperationException("Reference path editor is missing.");
						if (value != null) editor.LoadList(new[] { value });
						actual = string.Join(";", editor.GetList());
						break;
					case Signing signing:
						if (value != null) signing.SignAssembly.Value = bool.Parse(value);
						actual = signing.SignAssembly.Value.ToString();
						break;
					case BuildEvents eventsPanel:
						if (value != null) eventsPanel.PreBuildEvent.Value = value;
						actual = eventsPanel.PreBuildEvent.Value;
						break;
					case ProjectCustomToolOptionsPanel custom:
						if (value != null) custom.FileNames = value;
						actual = custom.FileNames;
						break;
					default:
						return JsonSerializer.Serialize(new { success = false, error = "Unsupported page: " + panel.GetType().Name });
				}
				if (value != null) options.Save();
				return JsonSerializer.Serialize(new { success = true, page = tab.Header?.ToString(), panel = panel.GetType().Name, value = actual });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.project-browser.solution-tree", Description = "The solution level of the Project Browser tree - solution folders, projects and solution items, nested as shown - without the contents of each project")]
		public static async Task<string> GetSolutionTree()
		{
			try {
				var viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
				await viewModel.WaitForCurrentRefreshAsync();
				return JsonSerializer.Serialize(new { success = true, roots = viewModel.RootNodes.Select(DescribeSolutionLevel).ToArray() });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		static object DescribeSolutionLevel(ProjectBrowserNodeModel node) => new {
			name = node.Name,
			kind = node.Kind.ToString(),
			children = node.Kind is ProjectBrowserNodeKind.Solution or ProjectBrowserNodeKind.SolutionFolder
				? node.Children.Select(DescribeSolutionLevel).ToArray()
				: Array.Empty<object>()
		};

		static ProjectBrowserNodeModel? FindNode(IEnumerable<ProjectBrowserNodeModel> nodes, Func<ProjectBrowserNodeModel, bool> predicate)
		{
			foreach (var node in nodes) {
				if (predicate(node))
					return node;
				var match = FindNode(node.Children, predicate);
				if (match != null)
					return match;
			}
			return null;
		}

		static List<object> Describe(IEnumerable items)
		{
			var result = new List<object>();
			foreach (var entry in items.OfType<Control>()) {
				if (entry.Visibility != Visibility.Visible)
					continue;
				if (entry is Separator) {
					result.Add(new { separator = true });
					continue;
				}
				if (entry is not MenuItem item)
					continue;
				var codon = FindCodon(item);
				List<object>? children = null;
				if (item.Items.Count > 0) {
					// Submenus are filled lazily when WPF raises SubmenuOpened (MenuService.CreateMenuItemFromDescriptor).
					item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, item));
					children = Describe(item.Items);
				}
				result.Add(new {
					label = item.Header?.ToString(),
					id = codon?.Id,
					addin = codon?.AddIn?.FileName is string file ? Path.GetFileName(file) : null,
					@class = codon != null && codon.Properties.Contains("class") ? codon.Properties["class"] : null,
					enabled = item.IsEnabled,
					children
				});
			}
			return result;
		}

		static Codon? FindCodon(object item)
		{
			for (var type = item.GetType(); type != null; type = type.BaseType) {
				var field = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
					.FirstOrDefault(f => typeof(Codon).IsAssignableFrom(f.FieldType));
				if (field != null)
					return field.GetValue(item) as Codon;
			}
			return null;
		}
	}
}
