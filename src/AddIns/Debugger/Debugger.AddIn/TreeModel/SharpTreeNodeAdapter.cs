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
using System.Linq;
using Debugger.AddIn.TreeModel;
using ICSharpCode.ILSpyX.TreeView;

namespace Debugger.AddIn.Pads.Controls
{
	public class SharpTreeNodeAdapter : SharpTreeNode
	{
		public SharpTreeNodeAdapter(TreeNode node)
		{
			if (node == null)
				throw new ArgumentNullException("node");
			this.Node = node;
			this.LazyLoading = true;
		}
		
		public TreeNode Node { get; private set; }
		
		public override object Icon {
			get { return this.Node.Image != null ? this.Node.Image.ImageSource : null; }
		}
		
		public override bool ShowExpander {
			get { return this.Node.GetChildren != null; }
		}
		
		// SharpTreeView duplicate resolution (2026-08-02): ILSpyX's SharpTreeNode calls
		// CanDelete()/Delete() once per selected node (see SharpTreeView.cs's
		// GetTopLevelSelection().All(node => node.CanDelete())/node.Delete() loop), rather than
		// passing the whole selection array as OpenDevelop's own control used to.
		public override bool CanDelete()
		{
			return Node.CanDelete;
		}

		public override void Delete()
		{
			Parent.Children.Remove(this);
		}
		
		protected override void LoadChildren()
		{
			if (this.Node.GetChildren != null) {
				Children.AddRange(this.Node.GetChildren().Select(node => node.ToSharpTreeNode()));
			}
		}
	}
}
