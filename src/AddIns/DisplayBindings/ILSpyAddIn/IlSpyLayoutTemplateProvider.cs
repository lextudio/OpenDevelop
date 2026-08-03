// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Registers the "ILSpy" named workbench layout via the ILayoutTemplateProvider extension point
// (doc/technotes/ilspy.md "Immediate next actions" #4, 2026-08-02) instead of the shell
// hard-coding an ILSpy row in data/layouts/LayoutConfig.xml alongside Default/Debug/Plain - the
// ILSpy AddIn now owns the fact that this named layout exists, not the shell. The template's
// actual content is still today's AvalonDock-XML file (data/layouts/ILSpy.xml, unchanged) - the
// versioned layout DTO described in the doc's Phase 2 is a separate, larger effort blocked on
// AvalonDockLayout.LoadLayout() actually re-enabling layout deserialization (currently stubbed
// out pending legacy-pad-to-MEF migration).
//
// OnActivating wires the AddIn's panes to actually appear when this layout is selected (2026-08-02
// follow-up): without it, picking "ILSpy" from ChooseLayoutComboBox before ever touching the
// AddIn's own menu commands (File > Open > Assembly) would silently restore nothing for the
// assemblyListPane/searchPane/analyzerPane anchorables in ILSpy.xml, since IlSpyWorkspaceHost had
// never registered them with DockWorkspace yet - the layout is data the AddIn owns, so activating
// it is also the AddIn's responsibility, not something the shell should have to know to trigger.

using System.Collections.Generic;

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class IlSpyLayoutTemplateProvider : ILayoutTemplateProvider
	{
		public IEnumerable<LayoutTemplateDescriptor> GetLayoutTemplates()
		{
			yield return new LayoutTemplateDescriptor("ILSpy", "ILSpy", "ILSpy.xml", readOnly: false,
				onActivating: IlSpyWorkspaceHost.EnsureInitialized);
		}
	}
}
