// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// A plain OpenDevelop pad hosting ILSpy's real DecompilerTextView directly. Unlike the tree/
// search/analyzer panes, ILSpy's DecompilerTextView isn't itself an [ExportToolPane] ToolPaneModel
// (it's a document/content control that ILSpy normally hosts inside its own TabPageModel), so
// there's no ILSpy ToolPaneModel to wrap via IlSpyToolPaneAdapter - this pad owns the pane
// lifecycle directly instead.
using ICSharpCode.ILSpy.TextView;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class DecompiledCodeToolPaneModel : ToolPaneModel
	{
		public DecompiledCodeToolPaneModel(DecompilerTextView view)
		{
			Title = "Decompiled Code";
			ContentId = "ilspy.decompiledCode";
			Content = view;
		}
	}
}
