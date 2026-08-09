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
using System.Threading.Tasks;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09) - the real implementation is now <see cref="LoadedModulesPadViewModel"/>.
	/// Constructed once with a plain <c>new</c> and cached in a static field (Debugger.AddIn's
	/// assembly is never scanned by <c>OpenDevelopMefHost</c>), then registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Must stay a real, constructible
	/// <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in
	/// this migration.
	/// </summary>
	public class LoadedModulesPad : AbstractPadContent
	{
		static LoadedModulesPadViewModel viewModel;

		public LoadedModulesPad()
		{
			if (viewModel == null) {
				viewModel = new LoadedModulesPadViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel?.Content;

		/// <summary>Used by the DevFlow "od.debug.pad-snapshot" test action.</summary>
		public Task<IEnumerable<object>> GetSnapshotAsync()
		{
			if (viewModel == null)
				return Task.FromResult<IEnumerable<object>>(Array.Empty<object>());
			return viewModel.GetSnapshotAsync();
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
