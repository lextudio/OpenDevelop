// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
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
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="LoadedModulesPad"/> (AddInTree pad id
	/// "LoadedModulesPad"). Not a MEF part - Debugger.AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> - so it is constructed with a plain <c>new</c> by the
	/// <see cref="LoadedModulesPad"/> shim on first real use and registered with the real docking
	/// host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	sealed class LoadedModulesPadViewModel : ToolPaneModel
	{
		readonly ListView listView;

		public LoadedModulesPadViewModel()
		{
			Title = "Loaded Modules";
			ContentId = "LoadedModulesPad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(LoadedModulesPad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			var res = new CommonResources();
			res.InitializeComponent();

			listView = new ListView();
			listView.View = (GridView)res["loadedModulesGridView"];
			listView.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "50%;70;50%;35;120");
			listView.MouseRightButtonUp += OnListViewMouseRightButtonUp;
			Content = listView;

			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}

		void OnListViewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
		{
			var module = listView.SelectedItem as ModuleItem;
			if (module == null)
				return;
			MenuService.ShowContextMenu(listView, module, "/SharpDevelop/Services/DebuggerService/ModuleContextMenu");
			e.Handled = true;
		}

		async void RefreshPad()
		{
			await RefreshPadAsync().ConfigureAwait(true);
		}

		async Task<IReadOnlyList<ModuleItem>> RefreshPadAsync()
		{
			var session = WindowsDebugger.CurrentSession;
			var loadedModules = new List<ModuleItem>();
			if (session != null && session.IsPaused) {
				var modules = await session.GetModulesAsync().ConfigureAwait(true);
				foreach (var module in modules) {
					loadedModules.Add(new ModuleItem(module));
				}
			}
			listView.ItemsSource = loadedModules;
			return loadedModules;
		}

		/// <summary>Used by the DevFlow "od.debug.pad-snapshot" test action.</summary>
		public async Task<IEnumerable<object>> GetSnapshotAsync()
		{
			var items = await RefreshPadAsync().ConfigureAwait(true);
			return items.Select(i => (object)new { i.Name, i.Path });
		}
	}
}
