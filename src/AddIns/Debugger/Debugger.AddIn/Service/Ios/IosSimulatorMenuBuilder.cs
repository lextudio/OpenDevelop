using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Devices;
using ICSharpCode.SharpDevelop.Project;

namespace Debugger.AddIn.Ios
{
	/// <summary>
	/// Debug &gt; iOS Simulator: which simulator F5 uses for the startup project, stored as its
	/// <see cref="IosSimulatorProjectBehavior.SimulatorPreferenceKey"/> preference. "Automatic" clears it, and
	/// the behavior then takes a booted iPhone, else the newest one.
	/// </summary>
	public sealed class IosSimulatorMenuBuilder : IMenuItemBuilder
	{
		/// <param name="Udid">The simctl UDID to store, or null for "Automatic".</param>
		public sealed record Choice(string Label, string Udid, bool IsChecked);

		public IEnumerable<object> BuildItems(Codon codon, object owner)
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null)
				return Array.Empty<object>();
			var devices = DeviceService.Default.Devices;
			// The menu is built synchronously from the last device snapshot; an empty one is refreshed for next time
			if (!devices.Any(d => d.Platform == DevicePlatform.IosSimulator)) {
				DeviceService.Default.RefreshAsync().FireAndForget();
				return new object[] { new System.Windows.Controls.MenuItem {
					Header = StringParser.Parse("${res:MainWindow.Windows.Debug.IosSimulator.Refreshing}"),
					IsEnabled = false
				} };
			}
			var preferred = project.Preferences.Get(IosSimulatorProjectBehavior.SimulatorPreferenceKey, string.Empty);
			return BuildChoices(devices, preferred, StringParser.Parse("${res:MainWindow.Windows.Debug.IosSimulator.Automatic}"))
				.Select(choice => {
					var item = new System.Windows.Controls.MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.IsChecked };
					item.Click += delegate { Select(project, choice.Udid); };
					return (object)item;
				})
				.ToArray();
		}

		/// <summary>Stores the simulator F5 uses for <paramref name="project"/>; null or empty means "Automatic".</summary>
		public static void Select(IProject project, string udid)
		{
			project.Preferences.Set(IosSimulatorProjectBehavior.SimulatorPreferenceKey, udid ?? string.Empty);
			project.SavePreferences();
		}

		/// <summary>"Automatic" first, then each simulator by name and runtime; the stored one is checked, or
		/// "Automatic" when none is stored or the stored one no longer exists.</summary>
		public static IReadOnlyList<Choice> BuildChoices(IEnumerable<Device> devices, string preferredUdid, string automaticLabel)
		{
			var simulators = devices.Where(d => d.Platform == DevicePlatform.IosSimulator)
				.OrderBy(d => d.Family)
				.ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
				.ThenByDescending(d => Version.TryParse(d.RuntimeVersion, out var version) ? version : new Version(0, 0))
				.ToList();
			var preferred = string.IsNullOrEmpty(preferredUdid)
				? null
				: simulators.FirstOrDefault(d => string.Equals(IosSimulatorProjectBehavior.SimctlUdid(d), preferredUdid, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(d.Id, preferredUdid, StringComparison.OrdinalIgnoreCase));
			var choices = new List<Choice> { new Choice(automaticLabel, null, preferred == null) };
			foreach (var simulator in simulators) {
				var booted = simulator.State == DeviceState.Booted ? " •" : string.Empty;
				choices.Add(new Choice(simulator.Name + " (" + simulator.RuntimeLabel + ")" + booted,
					IosSimulatorProjectBehavior.SimctlUdid(simulator), ReferenceEquals(simulator, preferred)));
			}
			return choices;
		}
	}

	/// <summary>The startup project declares a net11.0-ios or later target framework, on macOS.</summary>
	public sealed class IosSimulatorStartupProjectCondition : IConditionEvaluator
	{
		public bool IsValid(object owner, Condition condition)
		{
			return new IosSimulatorProjectCondition().IsValid(SD.ProjectService.CurrentSolution?.StartupProject, condition);
		}
	}
}
