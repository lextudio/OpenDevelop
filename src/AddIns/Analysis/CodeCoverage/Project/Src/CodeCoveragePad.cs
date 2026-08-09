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

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09) - the real implementation is now <see cref="CodeCoveragePadViewModel"/>.
	/// Constructed once with a plain <c>new</c> and cached in a static field (the AddIn's
	/// assembly is never scanned by <c>OpenDevelopMefHost</c>), then registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Must stay a real, constructible
	/// <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in
	/// this migration - and because <c>CodeCoverageService</c>/<c>ShowSourceCodeCommand</c>/
	/// <c>ShowVisitCountCommand</c> still reach the pad through <c>CodeCoveragePad.Instance</c>.
	/// </summary>
	public class CodeCoveragePad : AbstractPadContent
	{
		static CodeCoveragePad instance;
		static CodeCoveragePadViewModel viewModel;

		public static CodeCoveragePad Instance {
			get { return instance; }
		}

		public CodeCoveragePad()
		{
			instance = this;
			if (viewModel == null) {
				viewModel = new CodeCoveragePadViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel?.Content;

		public void UpdateToolbar() => viewModel?.UpdateToolbar();

		public void ShowResults(CodeCoverageResults results) => viewModel?.ShowResults(results);

		public void ClearCodeCoverageResults() => viewModel?.ClearCodeCoverageResults();

		public bool ShowSourceCodePanel {
			get { return viewModel?.ShowSourceCodePanel ?? false; }
			set { if (viewModel != null) viewModel.ShowSourceCodePanel = value; }
		}

		public bool ShowVisitCountPanel {
			get { return viewModel?.ShowVisitCountPanel ?? false; }
			set { if (viewModel != null) viewModel.ShowVisitCountPanel = value; }
		}
	}
}
