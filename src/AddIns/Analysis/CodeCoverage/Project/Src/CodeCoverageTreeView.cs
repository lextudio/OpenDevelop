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
using ICSharpCode.TreeView;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// WPF replacement for the old ExtTreeView-derived class - rebased onto
	/// ICSharpCode.TreeView.SharpTreeView (see UnitTesting.TestTreeView for the same pattern).
	/// Unlike a WinForms TreeView, SharpTreeView shows one Root node; to display several top-level
	/// module nodes side by side we use a synthetic, invisible root and add modules as its children.
	/// </summary>
	public class CodeCoverageTreeView : SharpTreeView
	{
		SharpTreeNode rootNode;

		public CodeCoverageTreeView()
		{
			rootNode = new SharpTreeNode();
			Root = rootNode;
			ShowRoot = false;
		}

		public void AddModules(List<CodeCoverageModule> modules)
		{
			foreach (CodeCoverageModule module in modules) {
				rootNode.Children.Add(new CodeCoverageModuleTreeNode(module));
			}
		}

		public void Clear()
		{
			rootNode.Children.Clear();
		}

		public CodeCoverageTreeNode SelectedNode {
			get { return SelectedItem as CodeCoverageTreeNode; }
		}
	}
}
