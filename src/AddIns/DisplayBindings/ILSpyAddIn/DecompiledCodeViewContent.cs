// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// A plain OpenDevelop document tab (AbstractViewContentWithoutFile) hosting ILSpy's real
// DecompilerTextView: decompiled output opens like a read-only, virtual file in the workbench
// instead of living in a dedicated pad - the same presentation the legacy ILSpy integration used
// (DecompiledViewContent), so the decompile result behaves like any other document tab (switch
// away, navigate back, close). Unlike the tree/search/analyzer panes, ILSpy's DecompilerTextView
// isn't itself an [ExportToolPane] ToolPaneModel (it's a document/content control that ILSpy
// normally hosts inside its own TabPageModel), so there's no ILSpy ToolPaneModel to wrap via
// IlSpyToolPaneAdapter - this view owns the lifecycle directly.
using System.ComponentModel.Design;

using ICSharpCode.ILSpy.TextView;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class DecompiledCodeViewContent : AbstractViewContentWithoutFile
	{
		readonly DecompilerTextView view;

		public DecompiledCodeViewContent(DecompilerTextView view)
		{
			this.view = view;
			Services = new ServiceContainer();
			TitleName = "Decompiled Code";
			TabPageText = "Decompiled Code";
		}

		public override object Control => view;

		public override void Load()
		{
		}

		public override void Save()
		{
		}
	}
}
