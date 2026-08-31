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
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project.Commands
{
	// EditProjectFile moved to Src/Project/Commands/EditProjectFile.cs: it has no dependency
	// on this legacy pad's types, unlike its neighbors below, so it can be compiled even
	// though this whole file is excluded from the MVP build.

	// The four commands below used to operate on the old WinForms/ExtTreeView
	// AbstractProjectBrowserTreeNode/ProjectBrowserPad.Instance API (out of MVP scope). They now
	// go through ProjectBrowserPad's reflection bridge (Src/Gui/Pads/Stubs/ProjectBrowserPadStub.cs)
	// into the modern WPF Project Browser's IProjectBrowserController, which already implements
	// all four operations (ShowPropertiesForNode/ToggleShowAll/IsShowAllFilesEnabled/CollapseAll) -
	// see ProjectBrowserControllerBase.cs and ProjectBrowserViewModel.cs.

	public class ShowPropertiesForNode : AbstractMenuCommand
	{
		public override void Run()
		{
			ProjectBrowserPad.ShowPropertiesForSelectedNode();
		}
	}

	public class ToggleShowAll : AbstractCheckableMenuCommand
	{
		public override bool IsChecked {
			get {
				return ProjectBrowserPad.IsShowingAllFiles();
			}
			set {
				ProjectBrowserPad.ToggleShowAllFiles();
			}
		}
	}

	public class RefreshProjectBrowser : AbstractMenuCommand
	{
		public override void Run()
		{
			ProjectBrowserPad.RefreshView();
		}
	}

	public class CollapseAllProjectBrowser : AbstractMenuCommand
	{
		public override void Run()
		{
			ProjectBrowserPad.CollapseAll();
		}
	}
}
