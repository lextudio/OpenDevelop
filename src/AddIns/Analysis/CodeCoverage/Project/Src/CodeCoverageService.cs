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
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.CodeCoverage
{
	public class CodeCoverageService
	{
		static List<CodeCoverageResults> results = new List<CodeCoverageResults>();
		static CodeCoverageHighlighter codeCoverageHighlighter = new CodeCoverageHighlighter();
		
		CodeCoverageService()
		{
		}
		
		static CodeCoverageService()
		{
			// IWorkbench is not registered yet when this type is first touched from
			// RegisterCodeCoverageOpenLensProviderCommand.Run() (runs during
			// CoreStartup.RunInitialization(), before WorkbenchStartup.InitializeWorkbench()'s
			// `SD.Services.AddService(typeof(IWorkbench), workbench)`). A static constructor that
			// throws is permanently cached by the runtime - every later access (including from
			// menu construction) would keep rethrowing a ServiceNotFoundException for the rest of
			// the process - so this must not touch SD.Workbench directly. TryHookViewOpened()
			// completes the subscription lazily, retried from CodeCoverageHighlighted (touched
			// repeatedly via menu IsChecked checks) once IWorkbench actually exists.
			SD.ProjectService.SolutionOpened += SolutionLoaded;
			TryHookViewOpened();
		}

		static bool viewOpenedHooked;

		static void TryHookViewOpened()
		{
			if (viewOpenedHooked)
				return;
			if (SD.Services.GetService(typeof(IWorkbench)) is not IWorkbench workbench)
				return;
			workbench.ViewOpened += ViewOpened;
			viewOpenedHooked = true;
		}

		/// <summary>
		/// Shows/hides the code coverage in the source code.
		/// </summary>
		public static bool CodeCoverageHighlighted {
			get {
				TryHookViewOpened();
				return CodeCoverageOptions.CodeCoverageHighlighted;
			}
			set {
				TryHookViewOpened();
				CodeCoveragePad pad = CodeCoveragePad.Instance;
				if (CodeCoverageOptions.CodeCoverageHighlighted != value) {
					CodeCoverageOptions.CodeCoverageHighlighted = value;
					if (CodeCoverageResultsExist) {
						if (value) {
							ShowCodeCoverage();
						} else {
							HideCodeCoverage();
						}
					}
				}
				if (pad != null) {
					pad.UpdateToolbar();
				}
			}
		}
		
		/// <summary>
		/// Gets the results from the last code coverage run.
		/// </summary>
		public static CodeCoverageResults[] Results {
			get {
				return results.ToArray();
			}
		}

		/// <summary>
		/// Raised whenever <see cref="Results"/> changes - a run completed (<see cref="ShowResults"/>)
		/// or the results were cleared (<see cref="ClearResults"/>). doc/technotes/openlens.md §10.5:
		/// the coverage lens host uses this to refresh, rather than on every keystroke.
		/// </summary>
		public static event EventHandler ResultsChanged;

		/// <summary>
		/// Clears any code coverage results currently on display.
		/// </summary>
		public static void ClearResults()
		{
			CodeCoveragePad pad = CodeCoveragePad.Instance;
			if (pad != null) {
				pad.ClearCodeCoverageResults();
			}
			HideCodeCoverage();
			results.Clear();
			ResultsChanged?.Invoke(null, EventArgs.Empty);
		}

		/// <summary>
		/// Shows the code coverage results in the code coverage pad and
		/// highlights any source code files that have been profiled.
		/// </summary>
		public static void ShowResults(CodeCoverageResults results)
		{
			CodeCoverageService.results.Add(results);
			CodeCoveragePad pad = CodeCoveragePad.Instance;
			if (pad != null) {
				pad.ShowResults(results);
			}
			RefreshCodeCoverageHighlights();
			ResultsChanged?.Invoke(null, EventArgs.Empty);
		}
		
		/// <summary>
		/// Updates the highlighted code coverage text to reflect any changes
		/// in the configured colours.
		/// </summary>
		public static void RefreshCodeCoverageHighlights()
		{
			if (CodeCoverageOptions.CodeCoverageHighlighted && CodeCoverageResultsExist) {
				HideCodeCoverage();
				ShowCodeCoverage();
			}
		}
		
		public static void ShowCodeCoverage(ITextEditor textEditor, string fileName)
		{
			foreach (CodeCoverageResults results in CodeCoverageService.Results) {
				List<CodeCoverageSequencePoint> sequencePoints = results.GetSequencePoints(fileName);
				if (sequencePoints.Count > 0) {
					codeCoverageHighlighter.AddMarkers(textEditor.Document, sequencePoints);
				}
			}
		}
		
		static void ShowCodeCoverage()
		{
			// Highlight any open files.
			foreach (IViewContent view in SD.Workbench.ViewContentCollection) {
				ShowCodeCoverage(view);
			}
		}
		
		static void HideCodeCoverage()
		{
			foreach (IViewContent view in SD.Workbench.ViewContentCollection) {
				ITextEditor textEditor = view.GetService<ITextEditor>();
				if (textEditor != null) {
					codeCoverageHighlighter.RemoveMarkers(textEditor.Document);
				}
			}
		}
		
		static void ViewOpened(object sender, ViewContentEventArgs e)
		{
			if (CodeCoverageOptions.CodeCoverageHighlighted && CodeCoverageResultsExist) {
				ShowCodeCoverage(e.Content);
			}
		}
		
		static void ShowCodeCoverage(IViewContent view)
		{
			ITextEditor textEditor = view.GetService<ITextEditor>();
			if (textEditor != null && view.PrimaryFileName != null) {
				ShowCodeCoverage(textEditor, view.PrimaryFileName);
			}
		}
		
		static bool CodeCoverageResultsExist {
			get {
				return results.Count > 0;
			}
		}
		
		static void SolutionLoaded(object sender, SolutionEventArgs e)
		{
			var solutionCodeCoverageResults = new SolutionCodeCoverageResults(e.Solution);
			foreach (CodeCoverageResults results in solutionCodeCoverageResults.GetCodeCoverageResultsForAllProjects()) {
				ShowResults(results);
			}
		}
	}
}
