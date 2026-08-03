// DevFlow actions used by integration tests to drive the real Android SDK Manager window
// end-to-end: open it non-modally, inspect its parsed package tree, toggle checkboxes, and
// apply changes - the same state the UI itself uses. See doc/technotes/integration-testing.md
// for the DevFlow action pattern.

using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

using ICSharpCode.SharpDevelop;
using ICSharpCode.ILSpyX.TreeView;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AndroidSdkManager
{
	[DevFlowUIThread]
	public static class AndroidSdkManagerDevFlowActions
	{
		static AndroidSdkManagerWindow currentWindow;

		[DevFlowAction("od.androidsdk.open-window", Description = "Open the real Android SDK Manager window (non-modally, so the DevFlow action returns immediately instead of blocking)")]
		public static string OpenWindow()
		{
			currentWindow?.Close();
			currentWindow = new AndroidSdkManagerWindow { Owner = SD.Workbench.MainWindow };
			currentWindow.Show();
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androidsdk.close-window", Description = "Close the Android SDK Manager window opened by od.androidsdk.open-window")]
		public static string CloseWindow()
		{
			currentWindow?.Close();
			currentWindow = null;
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androidsdk.set-location", Description = "Set the Android SDK location text box in the currently open window and re-scan it for packages")]
		public static string SetLocation(string path)
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Android SDK Manager window is open (call od.androidsdk.open-window first)" });

			currentWindow.SetSdkLocationForTesting(path);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androidsdk.list-packages", Description = "List the packages currently parsed from sdkmanager output, with their installed/checked/status state, as bound to the real Platforms and Tools trees")]
		public static string ListPackages()
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { open = false });

			var nodes = currentWindow.GetAllPackageNodesForTesting()
				.Select(n => new {
					id = n.Package.Id,
					displayName = n.Package.DisplayName,
					isInstalled = n.Package.IsInstalled,
					hasUpdate = n.Package.HasUpdate,
					isChecked = n.IsChecked,
					status = n.StatusText,
				}).ToArray();
			return JsonSerializer.Serialize(new { open = true, packages = nodes });
		}

		[DevFlowAction("od.androidsdk.set-checked", Description = "Check or uncheck the package row with the given Id in the currently open window, the same as clicking its checkbox")]
		public static string SetChecked(string packageId, bool isChecked)
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Android SDK Manager window is open (call od.androidsdk.open-window first)" });

			var node = currentWindow.GetAllPackageNodesForTesting().FirstOrDefault(n => n.Package.Id == packageId);
			if (node == null)
				return JsonSerializer.Serialize(new { success = false, error = $"No package '{packageId}' in the current tree" });

			node.IsChecked = isChecked;
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androidsdk.apply-changes", Description = "Execute the real Apply Changes command, installing/uninstalling all pending checked/unchecked packages")]
		public static string ApplyChanges()
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Android SDK Manager window is open (call od.androidsdk.open-window first)" });

			currentWindow.ApplyChangesForTesting();
			return JsonSerializer.Serialize(new { success = true });
		}
	}
}
