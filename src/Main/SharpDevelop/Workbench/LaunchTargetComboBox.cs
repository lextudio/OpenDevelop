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
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Debug toolbar drop-down for the startup project's launch profile, linked to its target framework
	/// (see <see cref="LaunchTargetService"/>). Hidden unless the project is multi-target or its
	/// launchSettings.json profiles name a platform, and disabled while a debug session runs.
	/// </summary>
	class LaunchTargetComboBox : ComboBox
	{
		ISolution solution;
		bool updating;

		public LaunchTargetComboBox()
		{
			// LibreWPF's implicit-style lookup only queries the exact element type (see ChooseLayoutComboBox).
			Loaded += (_, _) => {
				if (Style == null && Application.Current != null)
					Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
			};
			MinWidth = 140;
			Width = 200;
			Visibility = Visibility.Collapsed;
			ToolTip = StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.ToolTip}");
			SD.ProjectService.CurrentSolutionChanged += (_, e) => {
				AttachSolution(e.NewValue);
				RefreshOnUIThread();
			};
			ProjectTargetFrameworkService.ActiveTargetFrameworkChanged += (_, _) => RefreshOnUIThread();
			DropDownOpened += (_, _) => Refresh(); // picks up edits of launchSettings.json made outside the IDE
			// A Custom toolbar item gets no conditions, so follow the debugger here: changing the target
			// mid-session would change the active framework under the running program.
			var debugger = SD.Debugger;
			if (debugger != null) {
				debugger.DebugStarted += (_, _) => UpdateEnabled();
				debugger.DebugStopped += (_, _) => UpdateEnabled();
			}
			AttachSolution(SD.ProjectService.CurrentSolution);
			Refresh();
			UpdateEnabled();
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
			updating = true;
			try {
				Items.Clear();
				Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
				int selectedIndex = -1;
				foreach (var entry in state.Entries) {
					string label = entry.Kind == LaunchTargetEntryKind.AddProfile
						? string.Format(StringParser.Parse("${res:MainWindow.Windows.Debug.LaunchTarget.AddProfile}"), entry.TargetFramework)
						: entry.Label;
					var item = new ComboBoxItem { Content = label, Tag = entry };
					if (entry.Kind == LaunchTargetEntryKind.AddProfile)
						item.FontStyle = FontStyles.Italic;
					if (entry.IsSelected)
						selectedIndex = Items.Count;
					Items.Add(item);
				}
				SelectedIndex = selectedIndex;
			} finally {
				updating = false;
			}
		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
			base.OnSelectionChanged(e);
			if (updating || !(SelectedItem is ComboBoxItem item) || !(item.Tag is LaunchTargetEntry entry))
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
	}
}
