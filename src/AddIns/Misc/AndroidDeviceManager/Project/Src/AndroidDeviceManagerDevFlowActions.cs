// DevFlow actions used by integration tests to drive the real Android Device Manager window
// end-to-end: open it non-modally, inspect the parsed AVD list, and open/save the real AVD
// editor - the same state the UI itself uses. See doc/technotes/integration-testing.md for the
// DevFlow action pattern.

using System.Linq;
using System.Text.Json;

using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AndroidDeviceManager
{
	[DevFlowUIThread]
	public static class AndroidDeviceManagerDevFlowActions
	{
		static AndroidDeviceManagerWindow currentWindow;

		[DevFlowAction("od.androiddevice.open-window", Description = "Open the real Android Device Manager window (non-modally, so the DevFlow action returns immediately instead of blocking)")]
		public static string OpenWindow()
		{
			currentWindow?.Close();
			currentWindow = new AndroidDeviceManagerWindow { Owner = SD.Workbench.MainWindow };
			currentWindow.Show();
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androiddevice.close-window", Description = "Close the Android Device Manager window opened by od.androiddevice.open-window")]
		public static string CloseWindow()
		{
			currentWindow?.Close();
			currentWindow = null;
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.androiddevice.list-avds", Description = "List the AVDs currently shown in the open window's list, as parsed from the real `avdmanager list avd` output")]
		public static string ListAvds()
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { open = false });

			var avds = currentWindow.GetAvdsForTesting()
				.Select(a => new { name = a.Name, device = a.Device, target = a.Target, basedOn = a.BasedOn })
				.ToArray();
			return JsonSerializer.Serialize(new { open = true, avds });
		}

		[DevFlowAction("od.androiddevice.refresh", Description = "Re-run `avdmanager list avd` and refresh the currently open window's list")]
		public static string Refresh()
		{
			if (currentWindow == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Android Device Manager window is open (call od.androiddevice.open-window first)" });

			_ = currentWindow.RefreshAsync();
			return JsonSerializer.Serialize(new { success = true });
		}
	}
}
