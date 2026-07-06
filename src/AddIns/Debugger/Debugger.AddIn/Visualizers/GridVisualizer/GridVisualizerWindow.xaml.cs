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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core;

namespace Debugger.AddIn.Visualizers.GridVisualizer
{
	/// <summary>
	/// Interaction logic for GridVisualizerWindow.xaml
	/// </summary>
	public partial class GridVisualizerWindow : Window
	{
		Func<IEnumerable<DapVariableInfo>> getChildren;

		public GridVisualizerWindow(string valueName, Func<IEnumerable<DapVariableInfo>> getChildren)
		{
			InitializeComponent();

			this.Title = valueName;
			this.getChildren = getChildren;
			this.cmbColumns.Visibility = Visibility.Collapsed;

			Refresh();
		}

		public void Refresh()
		{
			listView.ItemsSource = null;

			try {
				var rows = getChildren().ToList();
				InitializeColumns((GridView)this.listView.View);
				this.listView.ItemsSource = rows;
			} catch (Exception ex) {
				MessageService.ShowMessage(ex.Message);
			}
		}

		void InitializeColumns(GridView gridView)
		{
			gridView.Columns.Clear();

			gridView.Columns.Add(new GridViewColumn { Header = "Name", Width = 140, DisplayMemberBinding = new Binding("Name") });
			gridView.Columns.Add(new GridViewColumn { Header = "Value", Width = 260, DisplayMemberBinding = new Binding("Value") });
			gridView.Columns.Add(new GridViewColumn { Header = "Type", Width = 140, DisplayMemberBinding = new Binding("Type") });
		}
	}
}
