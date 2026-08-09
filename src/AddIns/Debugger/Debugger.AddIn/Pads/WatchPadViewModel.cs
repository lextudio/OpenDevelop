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

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using Debugger.AddIn.Pads.Controls;
using Debugger.AddIn.TreeModel;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.ILSpy.Controls.TreeView;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="WatchPad"/> (AddInTree pad id "WatchPad").
	/// Not a MEF part - Debugger.AddIn's assembly is never scanned by <c>OpenDevelopMefHost</c>
	/// - so it is constructed with a plain <c>new</c> by the <see cref="WatchPad"/> shim on
	/// first real use and registered with the real docking host via <c>IPaneModelHost.Add</c>.
	/// The toolbar commands (<c>AddWatchCommand</c>/<c>RemoveWatchCommand</c>/
	/// <c>ClearWatchesCommand</c>) receive this model as their Owner now - the toolbar is built
	/// here with <c>this</c> - so they cast to <see cref="WatchPadViewModel"/> instead of
	/// <see cref="WatchPad"/>.
	/// </summary>
	sealed class WatchPadViewModel : ToolPaneModel
	{
		readonly Grid panel;
		readonly ToolBar toolBar;
		readonly SharpTreeView tree;

		public SharpTreeView Tree {
			get { return tree; }
		}

		public SharpTreeNodeCollection Items {
			get { return tree.Root.Children; }
		}

		public WatchPadViewModel()
		{
			Title = "Watch";
			ContentId = "WatchPad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(WatchPad).FullName;
			PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Bottom;

			var res = new CommonResources();
			res.InitializeComponent();

			panel = new Grid();

			toolBar = ToolBarService.CreateToolBar(panel, this, "/SharpDevelop/Pads/WatchPad/ToolBar");
			panel.Children.Add(toolBar);

			tree = new SharpTreeView();
			tree.Root = new WatchRootNode();
			tree.ShowRoot = false;
			tree.View = (GridView)res["variableGridView"];
			tree.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "50%;25%;25%");
			tree.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) {
				if (this.tree.SelectedItem == null) {
					AddWatch(focus: true);
				}
			};
			panel.Children.Add(tree);

			panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			Grid.SetRow(tree, 1);
			Content = panel;

			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}

		public void AddWatch(string expression = null, bool focus = false)
		{
			var node = MakeNode(expression);
			this.Items.Add(node);

			if (focus) {
				var view = tree.View as SharpGridView;
				_ = tree.Dispatcher.BeginInvoke(
					DispatcherPriority.Input, (System.Action)delegate {
						var container = tree.ItemContainerGenerator.ContainerFromItem(node) as SharpTreeViewItem;
						if (container == null) return;
						var textBox = container.NodeView.FindAncestor<StackPanel>().FindName("name") as AutoCompleteTextBox;
						if (textBox == null) return;
						textBox.FocusEditor();
					});
			}
		}

		SharpTreeNodeAdapter MakeNode(string name)
		{
			LoggingService.Info("Evaluating watch: " + name);
			TreeNode node = new ValueNode(null, name, name);
			node.CanDelete = true;
			node.CanSetName = true;
			node.PropertyChanged += (s, e) => {
				if (e.PropertyName == "Name") {
					((ValueNode)node).Refresh();
					WindowsDebugger.RefreshPads();
				}
			};
			return node.ToSharpTreeNode();
		}

		void RefreshPad()
		{
			var session = WindowsDebugger.CurrentSession;
			if (session != null && session.IsPaused) {
				var expressions = this.Items.OfType<SharpTreeNodeAdapter>()
					.Select(n => n.Node.Name)
					.ToList();
				this.Items.Clear();
				foreach (var expr in expressions) {
					this.Items.Add(MakeNode(expr));
				}
			}
		}
	}
}
