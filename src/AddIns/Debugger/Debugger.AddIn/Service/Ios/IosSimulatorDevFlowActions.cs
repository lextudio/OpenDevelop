using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Devices;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace Debugger.AddIn.Ios
{
	/// <summary>The Debug &gt; iOS Simulator menu for tests: the same choices and the same selection it makes.</summary>
	[DevFlowUIThread]
	public static class IosSimulatorDevFlowActions
	{
		[DevFlowAction("od.ios-simulator.choices", Description = "List the Debug > iOS Simulator choices for the startup project (refreshes the device list first)")]
		public static async Task<string> GetChoicesAsync()
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No startup project." });
			var devices = await DeviceService.Default.RefreshAsync();
			var preferred = project.Preferences.Get(IosSimulatorProjectBehavior.SimulatorPreferenceKey, string.Empty);
			var choices = IosSimulatorMenuBuilder.BuildChoices(devices, preferred, "Automatic");
			return JsonSerializer.Serialize(new {
				success = true,
				project = project.Name,
				menuVisible = new IosSimulatorStartupProjectCondition().IsValid(null, null),
				choices = choices.Select(c => new { label = c.Label, udid = c.Udid, isChecked = c.IsChecked })
			});
		}

		[DevFlowAction("od.ios-simulator.select", Description = "Choose the simulator F5 uses for the startup project, as Debug > iOS Simulator does (empty = Automatic)")]
		public static string Select(string udid)
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null)
				return JsonSerializer.Serialize(new { success = false, error = "No startup project." });
			IosSimulatorMenuBuilder.Select(project, udid);
			return JsonSerializer.Serialize(new { success = true, project = project.Name, udid = udid ?? string.Empty });
		}
	}
}
