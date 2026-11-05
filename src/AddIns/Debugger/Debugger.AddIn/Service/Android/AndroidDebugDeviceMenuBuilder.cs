using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Project;

namespace Debugger.AddIn.Android
{
	/// <summary>Selects the adb serial used by F5 for the startup project.</summary>
	public sealed class AndroidDebugDeviceMenuBuilder : IMenuItemBuilder
	{
		public sealed record Choice(string Label, string Serial, bool IsChecked);

		public IEnumerable<object> BuildItems(Codon codon, object owner)
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null) return Array.Empty<object>();
			string preferred = project.Preferences.Get(AndroidDebugProjectBehavior.DevicePreferenceKey, string.Empty);
			return BuildChoices(ListDevices(), preferred, StringParser.Parse("${res:MainWindow.Windows.Debug.AndroidDevice.Automatic}"))
				.Select(choice => {
					var item = new System.Windows.Controls.MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.IsChecked };
					item.Click += delegate { Select(project, choice.Serial); };
					return (object)item;
				}).ToArray();
		}

		public static IReadOnlyList<Choice> BuildChoices(IEnumerable<string> serials, string preferred, string automatic)
		{
			var devices = serials.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
			var result = new List<Choice> { new Choice(automatic, null, string.IsNullOrEmpty(preferred)) };
			result.AddRange(devices.Select(serial => new Choice(serial, serial, string.Equals(serial, preferred, StringComparison.Ordinal))));
			return result;
		}

		public static void Select(IProject project, string serial)
		{
			project.Preferences.Set(AndroidDebugProjectBehavior.DevicePreferenceKey, serial ?? string.Empty);
			project.SavePreferences();
		}

		public static IReadOnlyList<string> ListDevices()
		{
			string adb = AndroidEmulatorHostProcess.LocateAdb();
			if (adb == null) return Array.Empty<string>();
			try {
				using (var process = Process.Start(new ProcessStartInfo(adb, "devices") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })) {
					if (process == null) return Array.Empty<string>();
					var output = process.StandardOutput.ReadToEndAsync();
					if (!process.WaitForExit(3000)) { try { process.Kill(true); } catch { } return Array.Empty<string>(); }
					return process.ExitCode == 0 ? AndroidEmulatorHostProcess.ParseDevices(output.GetAwaiter().GetResult()) : Array.Empty<string>();
				}
			} catch (Exception) { return Array.Empty<string>(); }
		}
	}
}
