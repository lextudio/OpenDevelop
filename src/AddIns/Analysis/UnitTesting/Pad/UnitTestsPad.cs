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
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;

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
		DockPanel panel;
		ToolBar toolBar;
		TextBlock totalStatusText;
		TextBlock passedStatusText;
		TextBlock failedStatusText;
		TextBlock skippedStatusText;
		TextBlock notRunStatusText;
		int totalStatusCount;
		int passedStatusCount;
		int failedStatusCount;
		int skippedStatusCount;
		List<Tuple<IUnresolvedFile, IUnresolvedFile>> pending = new List<Tuple<IUnresolvedFile, IUnresolvedFile>>();

		public UnitTestsPad()
			: this(SD.GetRequiredService<ITestService>())
		{
		}
		
		public UnitTestsPad(ITestService testService)
		{
			this.testService = testService;
			
			panel = new DockPanel();
			treeView = new TestTreeView(); // treeView must be created first because it's used by CreateToolBar

			toolBar = CreateToolBar("/SharpDevelop/Pads/UnitTestsPad/Toolbar");
			panel.Children.Add(toolBar);
			DockPanel.SetDock(toolBar, Dock.Top);
			
			var statusBar = CreateStatusBar();
			panel.Children.Add(statusBar);
			DockPanel.SetDock(statusBar, Dock.Bottom);
			
			panel.Children.Add(treeView);
			
			treeView.ContextMenu = CreateContextMenu("/SharpDevelop/Pads/UnitTestsPad/ContextMenu");
			
			testService.OpenSolutionChanged += testService_OpenSolutionChanged;
			testService_OpenSolutionChanged(null, null);
		}
		
		public override void Dispose()
		{
			testService.OpenSolutionChanged -= testService_OpenSolutionChanged;
			base.Dispose();
		}
		
		public override object Control {
			get { return panel; }
		}
		
		public ITestTreeView TreeView {
			get { return treeView; }
		}
		
		public void StartRunStatus(int total)
		{
			totalStatusCount = total;
			passedStatusCount = 0;
			failedStatusCount = 0;
			skippedStatusCount = 0;
			UpdateRunStatusText();
		}
		
		public void RecordRunResult(TestResult result)
		{
			if (result == null)
				return;
			switch (result.ResultType) {
				case TestResultType.Success:
					passedStatusCount++;
					break;
				case TestResultType.Failure:
					failedStatusCount++;
					break;
				case TestResultType.Ignored:
					skippedStatusCount++;
					break;
			}
			UpdateRunStatusText();
		}
		
		void testService_OpenSolutionChanged(object sender, EventArgs e)
		{
			treeView.TestSolution = testService.OpenSolution;
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
		
		UIElement CreateStatusBar()
		{
			var border = new Border {
				BorderThickness = new Thickness(0, 1, 0, 0),
				Padding = new Thickness(8, 4, 8, 4)
			};
			// The status bar must follow the IDE theme (IdeThemeService's semantic resources,
			// swapped by Theme.Light.xaml/Theme.Dark.xaml). The previous hardcoded light colors
			// (background 245,247,250 / border 218,223,230) stayed correct in Light but left the
			// bar stuck in light mode under Dark. DynamicResource (via SetResourceReference) also
			// re-resolves automatically when the user switches themes at runtime.
			border.SetResourceReference(Border.BackgroundProperty, "ToolWindowBackground");
			border.SetResourceReference(Border.BorderBrushProperty, "Border");
			var items = new StackPanel {
				Orientation = Orientation.Horizontal
			};
			border.Child = items;
			
			totalStatusText = CreateStatusText("MutedForeground");
			passedStatusText = CreateStatusItem(items, new SolidColorBrush(Color.FromRgb(29, 128, 73)), "MutedForeground");
			failedStatusText = CreateStatusItem(items, new SolidColorBrush(Color.FromRgb(190, 58, 52)), "MutedForeground");
			skippedStatusText = CreateStatusItem(items, new SolidColorBrush(Color.FromRgb(145, 106, 32)), "MutedForeground");
			notRunStatusText = CreateStatusItem(items, new SolidColorBrush(Color.FromRgb(128, 138, 148)), "MutedForeground");
			
			items.Children.Add(totalStatusText);
			
			UpdateRunStatusText();
			return border;
		}
		
		static TextBlock CreateStatusText(string foregroundResourceKey)
		{
			var text = new TextBlock {
				Margin = new Thickness(0, 0, 14, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			text.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
			return text;
		}
		
		static TextBlock CreateStatusItem(Panel parent, Brush brush, string foregroundResourceKey)
		{
			var item = new StackPanel {
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0, 0, 14, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			item.Children.Add(new Ellipse {
				Width = 8,
				Height = 8,
				Fill = brush,
				Margin = new Thickness(0, 0, 5, 0),
				VerticalAlignment = VerticalAlignment.Center
			});
			var text = new TextBlock {
				VerticalAlignment = VerticalAlignment.Center
			};
			text.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
			item.Children.Add(text);
			parent.Children.Add(item);
			return text;
		}
		
		void UpdateRunStatusText()
		{
			int completed = passedStatusCount + failedStatusCount + skippedStatusCount;
			int notRun = Math.Max(0, totalStatusCount - completed);
			totalStatusText.Text = "Total: " + totalStatusCount;
			passedStatusText.Text = passedStatusCount.ToString();
			failedStatusText.Text = failedStatusCount.ToString();
			skippedStatusText.Text = skippedStatusCount.ToString();
			notRunStatusText.Text = notRun.ToString();
		}
	}
}
