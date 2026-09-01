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
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="CodeCoveragePad"/> (AddInTree pad id
	/// "CodeCoveragePad"). Not a MEF part - the AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> - so it is constructed with a plain <c>new</c> by the
	/// <see cref="CodeCoveragePad"/> shim on first real use and registered with the real docking
	/// host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	sealed class CodeCoveragePadViewModel : ToolPaneModel
	{
		bool disposed;
		readonly CodeCoverageControl codeCoverageControl;

		public CodeCoveragePadViewModel()
		{
			Title = "Code Coverage";
			ContentId = "CodeCoveragePad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(CodeCoveragePad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			codeCoverageControl = new CodeCoverageControl();
			codeCoverageControl.UpdateToolbar();
			Content = codeCoverageControl;

			SD.ProjectService.SolutionClosed += SolutionClosed;
			SD.ProjectService.SolutionOpened += SolutionLoaded;

			ShowSourceCodePanel = CodeCoverageOptions.ShowSourceCodePanel;
			ShowVisitCountPanel = CodeCoverageOptions.ShowVisitCountPanel;
		}

		public void Dispose()
		{
			if (!disposed) {
				disposed = true;
				SD.ProjectService.SolutionClosed -= SolutionClosed;
				SD.ProjectService.SolutionOpened -= SolutionLoaded;
				// CodeCoverageControl is a plain WPF UserControl now (no ElementHost/WinForms
				// child controls needing an explicit Dispose() the way the old version did).
			}
		}

		public void UpdateToolbar()
		{
			codeCoverageControl.UpdateToolbar();
		}

		public void ShowResults(CodeCoverageResults results)
		{
			if (results != null) {
				codeCoverageControl.AddModules(results.Modules);
			}
		}

		public void ClearCodeCoverageResults()
		{
			codeCoverageControl.Clear();
		}

		public bool ShowSourceCodePanel {
			get {
				return codeCoverageControl.ShowSourceCodePanel;
			}
			set {
				codeCoverageControl.ShowSourceCodePanel = value;
			}
		}

		public bool ShowVisitCountPanel {
			get {
				return codeCoverageControl.ShowVisitCountPanel;
			}
			set {
				codeCoverageControl.ShowVisitCountPanel = value;
			}
		}

		void SolutionLoaded(object sender, EventArgs e)
		{
			codeCoverageControl.UpdateToolbar();
		}

		void SolutionClosed(object sender, EventArgs e)
		{
			ClearCodeCoverageResults();
			codeCoverageControl.UpdateToolbar();
		}
	}
}
