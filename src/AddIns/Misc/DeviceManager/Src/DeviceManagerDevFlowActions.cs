// DevFlow actions for the shared Device Manager: open it non-modally, list the same snapshot the
// dialog shows, and refresh it. Replaces the old od.androiddevice.* actions that drove the removed
// standalone Android window.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Devices;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.DeviceManager
{
	[DevFlowUIThread]
	public static class DeviceManagerDevFlowActions
	{
		[DevFlowAction("od.devices.open-window", Description = "Open the shared Device Manager window (non-modally, so the DevFlow action returns immediately)")]
		public static string OpenWindow()
		{
			DeviceManagerWindow.Show(SD.Workbench.MainWindow);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.devices.close-window", Description = "Close the Device Manager window opened by od.devices.open-window")]
		public static string CloseWindow()
		{
			DeviceManagerWindow.Close();
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.devices.list", Description = "List the devices in the shared snapshot the Device Manager shows, with platform, runtime and state")]
		public static string List()
		{
			var devices = DeviceService.Default.Devices
				.Select(device => new {
					platform = device.Platform.ToString(),
					id = device.Id,
					name = device.Name,
					runtime = device.RuntimeLabel,
					state = device.State.ToString(),
				})
				.ToArray();
			return JsonSerializer.Serialize(new { open = DeviceManagerWindow.IsOpen, devices });
		}

		[DevFlowAction("od.devices.refresh", Description = "Re-read every provider (simctl, avdmanager, ...) and publish the new snapshot")]
		public static async Task<string> Refresh()
		{
			await DeviceService.Default.RefreshAsync();
			return JsonSerializer.Serialize(new { success = true, count = DeviceService.Default.Devices.Count });
		}
	}
}
