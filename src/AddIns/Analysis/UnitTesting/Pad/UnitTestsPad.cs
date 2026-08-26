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
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.UnitTesting
{
	public class UnitTestsPad : AbstractPadContent
	{
		// The single instance the workbench shows (doc/technotes/ilspy.md "Legacy pad
		// migration"): the UnitTestsPadToolPaneModel creates it on first resolution (before the
		// AnchorablesSource binding attaches), and TestExecutionManager uses it to reach the pad.
		// A legacy AddInTree <Pad> descriptor that misses the modern routing would CreateObject a
		// second, never-shown instance instead - callers must use SharedInstance, never the pad
		// they constructed themselves.
		static UnitTestsPad sharedInstance;

		internal static UnitTestsPad SharedInstance {
			get { return sharedInstance; }
		}

		ITestService testService;
		TestTreeView treeView;
		UnitTestsPadView view;
		UnitTestsPadViewModel viewModel;
		DispatcherTimer initialLoadTimer;
		bool initialLoadPending;
		List<Tuple<IUnresolvedFile, IUnresolvedFile>> pending = new List<Tuple<IUnresolvedFile, IUnresolvedFile>>();

		public UnitTestsPad()
			: this(SD.GetRequiredService<ITestService>(), deferInitialTreeLoad: true)
		{
		}
		
		public UnitTestsPad(ITestService testService)
			: this(testService, deferInitialTreeLoad: false)
		{
		}

		UnitTestsPad(ITestService testService, bool deferInitialTreeLoad)
		{
			this.testService = testService;
			if (sharedInstance == null)
				sharedInstance = this;

			viewModel = new UnitTestsPadViewModel { IsLoading = true };
			view = new UnitTestsPadView { DataContext = viewModel };
			treeView = view.TreeView; // must exist before CreateToolBar: commands use it as owner
			view.Toolbar = CreateToolBar("/SharpDevelop/Pads/UnitTestsPad/Toolbar");
			
			treeView.ContextMenu = CreateContextMenu("/SharpDevelop/Pads/UnitTestsPad/ContextMenu");
			
			testService.OpenSolutionChanged += testService_OpenSolutionChanged;
			if (deferInitialTreeLoad) {
				// Scheduling from the constructor is too early: the dispatcher can reach ContextIdle
				// before AvalonDock has attached this pad to the visual tree, so the loading state is
				// never actually painted. Wait for the first Loaded event, then yield through render
				// priority before materializing and expanding the lazy test nodes.
				initialLoadPending = true;
				view.TreeHost.Loaded += TreeHostLoaded;
			} else {
				LoadOpenSolution();
			}
		}

		void TreeHostLoaded(object sender, RoutedEventArgs e)
		{
			view.TreeHost.Loaded -= TreeHostLoaded;
			// A low-priority dispatcher callback can still run before macOS presents WPF's first
			// composed frame. A one-shot timer forces the dispatcher to return to the native event
			// loop first, so AvalonDock visibly activates the pad before tree materialization starts.
			initialLoadTimer = new DispatcherTimer(DispatcherPriority.Background) {
				Interval = TimeSpan.FromMilliseconds(100)
			};
			initialLoadTimer.Tick += InitialLoadTimerTick;
			initialLoadTimer.Start();
		}

		void InitialLoadTimerTick(object sender, EventArgs e)
		{
			initialLoadTimer.Stop();
			initialLoadTimer.Tick -= InitialLoadTimerTick;
			initialLoadTimer = null;
			initialLoadPending = false;
			LoadOpenSolution();
		}
		
		public override void Dispose()
		{
			view.TreeHost.Loaded -= TreeHostLoaded;
			if (initialLoadTimer != null) {
				initialLoadTimer.Stop();
				initialLoadTimer.Tick -= InitialLoadTimerTick;
				initialLoadTimer = null;
			}
			initialLoadPending = false;
			testService.OpenSolutionChanged -= testService_OpenSolutionChanged;
			base.Dispose();
		}
		
		public override object Control {
			get { return view; }
		}
		
		public ITestTreeView TreeView {
			get { return treeView; }
		}
		
		public void StartRunStatus(int total)
		{
			viewModel.StartRun(total);
		}
		
		public void RecordRunResult(TestResult result)
		{
			viewModel.RecordResult(result);
		}
		
		void testService_OpenSolutionChanged(object sender, EventArgs e)
		{
			if (!initialLoadPending) {
				LoadOpenSolution();
			}
		}

		void LoadOpenSolution()
		{
			treeView.TestSolution = testService.OpenSolution;
			viewModel.IsLoading = false;
			// The status bar's Total reflects the discovered test count (doc/technotes/ilspy.md,
			// "Total: 0" fix, 2026-08-09): the tree the user sees is this same model, so count its
			// leaf tests now - a pad showing test classes while reading "Total: 0" reads as broken.
			// A run's own StartRunStatus/TestCountDiscovered overrides this when tests actually run.
			int count = testService.OpenSolution == null ? 0 : CountLeafTests(testService.OpenSolution);
			viewModel.StartRun(count);
		}

		static int CountLeafTests(ITest test)
		{
			if (test == null)
				return 0;
			var nestedTests = test.NestedTests;
			if (nestedTests == null || nestedTests.Count == 0)
			{
				// A container node that currently has no loaded children is an empty grouping, not
				// a test - e.g. an empty "All Tests" root when no solution is open. Only a real
				// test-method leaf (MtpTestMethod & friends) counts as 1 here.
				return test is TestSolution or TestNamespace or TestProjectBase or ICSharpCode.UnitTesting.Mtp.MtpTestClass ? 0 : 1;
			}
			int count = 0;
			foreach (var nestedTest in nestedTests) {
				count += CountLeafTests(nestedTest);
			}
			return count;
		}
		
		/// <summary>
		/// Virtual method so we can override this method and return
		/// a dummy ToolBar when testing.
		/// </summary>
		protected virtual ToolBar CreateToolBar(string name)
		{
			Debug.Assert(treeView != null);
			return ToolBarService.CreateToolBar(treeView, treeView, name);
		}
		
		/// <summary>
		/// Virtual method so we can override this method and return
		/// a dummy ContextMenu when testing.
		/// </summary>
		protected virtual ContextMenu CreateContextMenu(string name)
		{
			Debug.Assert(treeView != null);
			return MenuService.CreateContextMenu(treeView, name);
		}
		
	}
}
