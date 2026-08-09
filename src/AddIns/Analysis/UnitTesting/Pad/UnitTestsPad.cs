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
		ITestService testService;
		TestTreeView treeView;
		UnitTestsPadView view;
		UnitTestsPadViewModel viewModel;
		DispatcherOperation initialLoadOperation;
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
			initialLoadOperation = Application.Current.Dispatcher.BeginInvoke(
				DispatcherPriority.ContextIdle,
				new Action(() => {
					initialLoadOperation = null;
					initialLoadPending = false;
					LoadOpenSolution();
				}));
		}
		
		public override void Dispose()
		{
			view.TreeHost.Loaded -= TreeHostLoaded;
			initialLoadOperation?.Abort();
			initialLoadOperation = null;
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
