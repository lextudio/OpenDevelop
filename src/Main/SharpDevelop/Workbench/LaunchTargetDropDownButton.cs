// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// The arrow beside the Start button of the Debug toolbar, the way Visual Studio does it: a pop-up menu with the
	/// startup project's launch profiles (the selected one ticked), the devices of its platform and an entry that opens
	/// launchSettings.json (see <see cref="LaunchTargetService"/>). Hidden unless there is something to choose, and
	/// disabled while a debug session runs.
	/// </summary>
	class LaunchTargetDropDownButton : Button
	{
		readonly ContextMenu menu = new ContextMenu();
		ISolution solution;
		int deviceStart = -1;

		public LaunchTargetDropDownButton()
		{
			Content = "\u25BE";
			Padding = new Thickness(2, 0, 2, 0);
			Visibility = Visibility.Collapsed;
			ToolTip = StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.ToolTip}");
			menu.PlacementTarget = this;
			menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
			menu.Opened += (_, _) => {
				foreach (var source in LaunchTargetDeviceSources.All)
					source.RequestRefresh();
			};
			SD.ProjectService.CurrentSolutionChanged += (_, e) => {
				AttachSolution(e.NewValue);
				RefreshOnUIThread();
			};
			ProjectTargetFrameworkService.ActiveTargetFrameworkChanged += (_, _) => RefreshOnUIThread();
			foreach (var source in LaunchTargetDeviceSources.All)
				source.DevicesChanged += (_, _) => RefreshDevicesOnUIThread();
			var debugger = SD.Debugger;
			if (debugger != null) {
				debugger.DebugStarted += (_, _) => UpdateEnabled();
				debugger.DebugStopped += (_, _) => UpdateEnabled();
			}
			AttachSolution(SD.ProjectService.CurrentSolution);
			Refresh();
			UpdateEnabled();
		}

		protected override void OnClick()
		{
			base.OnClick();
			Refresh(); // picks up edits of launchSettings.json made outside the IDE
			if (Visibility == Visibility.Visible)
				menu.IsOpen = true;
		}

		void AttachSolution(ISolution newSolution)
		{
			if (solution != null)
				solution.StartupProjectChanged -= OnStartupProjectChanged;
			solution = newSolution;
			if (solution != null)
				solution.StartupProjectChanged += OnStartupProjectChanged;
		}

		void OnStartupProjectChanged(object sender, EventArgs e) => RefreshOnUIThread();

		void UpdateEnabled()
		{
			if (!Dispatcher.CheckAccess()) {
				Dispatcher.InvokeAsync(UpdateEnabled);
				return;
			}
			IsEnabled = !(SD.Debugger?.IsDebugging ?? false);
		}

		void RefreshOnUIThread()
		{
			if (Dispatcher.CheckAccess())
				Refresh();
			else
				Dispatcher.InvokeAsync(Refresh);
		}

		void Refresh()
		{
			var state = LaunchTargetService.GetState(SD.ProjectService.CurrentSolution?.StartupProject);
			menu.Items.Clear();
			deviceStart = -1;
			Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
			foreach (var entry in state.Entries) {
				if (entry.Kind == LaunchTargetEntryKind.Device)
					continue;
				string label = entry.Kind == LaunchTargetEntryKind.AddProfile
					? string.Format(StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.AddProfile}"), entry.TargetFramework)
					: entry.Label;
				var item = new MenuItem { Header = label, IsChecked = entry.IsSelected, Tag = entry };
				if (entry.Kind == LaunchTargetEntryKind.AddProfile)
					item.FontStyle = FontStyles.Italic;
				item.Click += OnEntryClick;
				menu.Items.Add(item);
			}
			AddDeviceItems(state);
			if (state.Visible) {
				menu.Items.Add(new Separator());
				var edit = new MenuItem { Header = StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.EditLaunchSettings}") };
				edit.Click += (_, _) => OpenLaunchSettings();
				menu.Items.Add(edit);
			}
		}

		// Devices arrive after the menu opened (the first snapshot is empty): replace only that section, in place.
		void RefreshDevices()
		{
			if (!menu.IsOpen) {
				Refresh();
				return;
			}
			var state = LaunchTargetService.GetState(SD.ProjectService.CurrentSolution?.StartupProject);
			if (deviceStart >= 0) {
				// the device section runs from deviceStart up to the trailing separator + edit item
				while (menu.Items.Count > deviceStart)
					menu.Items.RemoveAt(menu.Items.Count - 1);
			} else {
				while (menu.Items.Count > 0 && !(menu.Items[menu.Items.Count - 1] is Separator))
					menu.Items.RemoveAt(menu.Items.Count - 1);
				if (menu.Items.Count > 0)
					menu.Items.RemoveAt(menu.Items.Count - 1);
			}
			AddDeviceItems(state);
			menu.Items.Add(new Separator());
			var edit = new MenuItem { Header = StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.EditLaunchSettings}") };
			edit.Click += (_, _) => OpenLaunchSettings();
			menu.Items.Add(edit);
		}

		void AddDeviceItems(LaunchTargetService.State state)
		{
			deviceStart = -1;
			foreach (var entry in state.Entries) {
				if (entry.Kind != LaunchTargetEntryKind.Device)
					continue;
				if (deviceStart < 0) {
					deviceStart = menu.Items.Count;
					menu.Items.Add(new Separator());
				}
				var item = new MenuItem { Header = entry.Label, IsChecked = entry.IsSelected, Tag = entry };
				item.Click += OnEntryClick;
				menu.Items.Add(item);
			}
		}

		void RefreshDevicesOnUIThread()
		{
			if (Dispatcher.CheckAccess())
				RefreshDevices();
			else
				Dispatcher.InvokeAsync(RefreshDevices);
		}

		void OnEntryClick(object sender, RoutedEventArgs e)
		{
			if (!(sender is MenuItem item) || !(item.Tag is LaunchTargetEntry entry))
				return;
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null)
				return;
			try {
				LaunchTargetService.Apply(project, entry);
			} catch (LaunchSettingsEditException ex) {
				MessageService.ShowError(ex.Message);
			}
			Refresh();
		}

		static void OpenLaunchSettings()
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			if (project == null)
				return;
			string path = LaunchSettingsProfile.GetPath(project.Directory);
			if (File.Exists(path))
				SD.FileService.OpenFile(FileName.Create(path));
		}
	}
}
