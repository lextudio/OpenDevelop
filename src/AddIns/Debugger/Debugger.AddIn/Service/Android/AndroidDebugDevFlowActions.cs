using System.Linq;
using System.Text.Json;
using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace Debugger.AddIn.Android
{
	[DevFlowUIThread]
	public static class AndroidDebugDevFlowActions
	{
		[DevFlowAction("od.android-debug.choices", Description = "List the Android devices available to F5")]
		public static string Choices()
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null) return JsonSerializer.Serialize(new { success = false, error = "No startup project." });
			string preferred = project.Preferences.Get(AndroidDebugProjectBehavior.DevicePreferenceKey, string.Empty);
			var choices = AndroidDebugDeviceMenuBuilder.BuildChoices(AndroidDebugDeviceMenuBuilder.ListDevices(), preferred, "Automatic");
			return JsonSerializer.Serialize(new { success = true, choices = choices.Select(c => new { label = c.Label, serial = c.Serial, isChecked = c.IsChecked }) });
		}

		[DevFlowAction("od.android-debug.select", Description = "Choose the Android device used by F5 (empty = automatic)")]
		public static string Select(string serial)
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null) return JsonSerializer.Serialize(new { success = false, error = "No startup project." });
			AndroidDebugDeviceMenuBuilder.Select(project, serial);
			return JsonSerializer.Serialize(new { success = true, serial = serial ?? string.Empty });
		}
	}
}
