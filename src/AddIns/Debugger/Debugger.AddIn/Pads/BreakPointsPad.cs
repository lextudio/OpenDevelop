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
using System.Linq;
using System.Threading.Tasks;

using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;
using Debugger.AddIn.Breakpoints;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-04) - the real implementation is now <see cref="BreakPointsPadViewModel"/>.
	/// </summary>
	/// <remarks>
	/// Unlike every other migrated pad's shim in this effort, the real ViewModel here is not a MEF
	/// part (Debugger.AddIn's assembly is never scanned by <c>OpenDevelopMefHost</c>) - constructed
	/// once with a plain <c>new</c> and cached in a static field (this shim class itself plays the
	/// role <c>[Shared]</c> MEF composition plays for the App-project-hosted pads), then registered
	/// with the real docking host via <c>IPaneModelHost.Add</c> so it shows up alongside the
	/// MEF-discovered panes (<c>DockWorkspace.ToolPanes</c> already surfaces both kinds - see its
	/// doc comment). Must stay a real, constructible <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in this
	/// migration.
	/// </remarks>
	sealed class BreakPointsPad : AbstractPadContent
	{
		static BreakPointsPadViewModel viewModel;

		public BreakPointsPad()
		{
			if (viewModel == null) {
				viewModel = new BreakPointsPadViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel.Content;

		/// <summary>Used by the DevFlow "od.debug.pad-snapshot" test action.</summary>
		public Task<IEnumerable<object>> GetSnapshotAsync()
		{
			IEnumerable<object> items = SD.BookmarkManager.Bookmarks
				.OfType<BreakpointBookmark>()
				.Select(b => (object)new { File = b.FileName != null ? b.FileName.ToString() : null, Line = b.LineNumber, b.IsEnabled, b.IsHealthy, b.Condition });
			return Task.FromResult(items);
		}
	}
}
