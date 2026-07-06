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

using System.Collections.Generic;
using System.Windows.Controls;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	public class LoadedModulesPad : AbstractPadContent
	{
		ListView listView;

		public override object Control {
			get { return listView; }
		}

		public LoadedModulesPad()
		{
			var res = new CommonResources();
			res.InitializeComponent();

			listView = new ListView();
			listView.View = (GridView)res["loadedModulesGridView"];
			listView.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "50%;70;50%;35;120");

			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}

		async void RefreshPad()
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
		}
	}

	public class ModuleItem
	{
		public string Name { get; private set; }
		public string Address { get { return string.Empty; } }
		public string Path { get; private set; }
		public string Order { get { return string.Empty; } }
		public string Symbols { get { return string.Empty; } }

		// DAP's "modules" request only gives id/name/path - base address, load order and symbol
		// status (all ICorDebug-specific) are not available, so those columns render blank. Known gap.
		public ModuleItem(DapModuleInfo module)
		{
			this.Name = module.Name;
			this.Path = module.Path;
		}
	}
}
