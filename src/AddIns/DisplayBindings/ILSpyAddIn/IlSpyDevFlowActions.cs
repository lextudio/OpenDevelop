// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the hosted ILSpy addin
// without a native file-open dialog (which the WPF-embedded DevFlow agent can't see/control -
// same reasoning as OpenDevelopDevFlowActions.cs/WpfDesignDevFlowActions.cs). Static methods on a
// [DevFlowUIThread]-annotated class are auto-discovered by LeXtudio.DevFlow.Agent.Core and
// dispatched to the UI thread.
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop;
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
				var dockType = Type.GetType("ICSharpCode.SharpDevelop.Workbench.DockWorkspace, OpenDevelop", throwOnError: false);
				if (dockType == null)
					return "dockType not found";
				var current = dockType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
				if (current == null)
					return "Current is null";
				var manager = current.GetType().GetField("dockingManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(current);
				if (manager == null)
					return "dockingManager not found";
				manager.GetType().GetProperty("ActiveContent")?.SetValue(manager, pane);
				return "activated";
			} catch (Exception ex) {
				return "failed: " + ex.Message;
			}
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

		[DevFlowAction("od.ilspy.status", Description = "Inspect the hosted ILSpy pads (Assemblies/Search/Analyzer/Decompiled Code): whether they're registered/visible, the assembly tree's loaded assemblies, and a snippet of the decompiled code pane")]
		public static string GetStatus()
		{
			var panes = IlSpyWorkspaceHost.Panes
				.Select(p => new { title = p.Title, contentId = p.ContentId, isVisible = p.IsVisible, isActive = p.IsActive })
				.ToArray();

			var assemblyTreeModel = IlSpyWorkspaceHost.AssemblyTreeModel;
			var loadedAssemblies = assemblyTreeModel.AssemblyList.GetAssemblies()
				.Select(a => a.ShortName)
				.ToArray();

			string decompiledText = IlSpyWorkspaceHost.DecompilerTextView.textEditor.Text;

			return JsonSerializer.Serialize(new {
				panes,
				loadedAssemblies,
				decompiledTextLength = decompiledText?.Length ?? 0,
				decompiledTextSnippet = decompiledText?.Length > 2000 ? decompiledText[..2000] : decompiledText
			});
		}
	}
}
