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
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.Workbench;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class ProjectBrowserDevFlowActions
	{
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
