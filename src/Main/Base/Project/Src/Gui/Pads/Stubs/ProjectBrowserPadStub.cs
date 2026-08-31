// MVP ProjectBrowser entry point. The old WinForms/ExtTreeView ProjectBrowser is out of scope;
// OpenDevelop's WPF ProjectBrowser implementation lives in the executable assembly, so Base uses
// a late-bound service lookup to keep the original ProjectBrowserPad refresh contract.
using System;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	public static class ProjectBrowserPad
	{
		const string ControllerTypeName = "ICSharpCode.SharpDevelop.Services.IProjectBrowserController, OpenDevelop";

		static (Type Type, object Instance)? TryGetController()
		{
			Type controllerType = Type.GetType(ControllerTypeName, throwOnError: false);
			if (controllerType == null) {
				return null;
			}

			object controller = ServiceSingleton.ServiceProvider.GetService(controllerType);
			return controller == null ? null : (controllerType, controller);
		}

		public static void RefreshView()
		{
			var controller = TryGetController();
			controller?.Type.GetMethod("Refresh")?.Invoke(controller.Value.Instance, null);
		}

		/// <summary>Shows the Properties pad for the Project Browser's currently selected node.</summary>
		public static void ShowPropertiesForSelectedNode()
		{
			var controller = TryGetController();
			controller?.Type.GetMethod("ShowPropertiesForNode")?.Invoke(controller.Value.Instance, new object[] { null });
		}

		/// <summary>Toggles the "Show All Files" (include on-disk, not-in-project files) setting.</summary>
		public static void ToggleShowAllFiles()
		{
			var controller = TryGetController();
			controller?.Type.GetMethod("ToggleShowAll")?.Invoke(controller.Value.Instance, null);
		}

		/// <summary>Whether "Show All Files" is currently on, for a checkable menu item's state.</summary>
		public static bool IsShowingAllFiles()
		{
			var controller = TryGetController();
			return controller?.Type.GetProperty("IsShowAllFilesEnabled")?.GetValue(controller.Value.Instance) as bool? ?? false;
		}

		/// <summary>Collapses every node in the Project Browser tree.</summary>
		public static void CollapseAll()
		{
			var controller = TryGetController();
			controller?.Type.GetMethod("CollapseAll")?.Invoke(controller.Value.Instance, null);
		}
	}
}
