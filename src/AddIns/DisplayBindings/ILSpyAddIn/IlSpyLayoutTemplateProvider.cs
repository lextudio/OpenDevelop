// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Registers the "ILSpy" named workbench layout via the ILayoutTemplateProvider extension point
// (doc/technotes/ilspy.md "Immediate next actions" #4, 2026-08-02) instead of the shell
// hard-coding an ILSpy row in data/layouts/LayoutConfig.xml alongside Default/Debug/Plain - the
// ILSpy AddIn now owns the fact that this named layout exists, not the shell. The template file
// itself now lives physically inside this AddIn's own folder too (Layouts/ILSpy.xml, moved out of
// the shell's data/layouts/ - see doc/technotes/ilspy.md "layout file ownership", 2026-08-03),
// matching the ownership this provider already claims declaratively: deleting this AddIn folder
// now also removes its layout template, instead of leaving an orphaned file in the shell's own
// data directory. LayoutConfiguration resolves a rooted TemplateFileName like this one straight
// through rather than combining it with the shell's DataLayoutPath (see
// LayoutConfiguration.LoadAddInContributedLayoutTemplates); per-user customizations of this layout
// still save to the shell's ConfigLayoutPath under a plain "ILSpy.xml", not back into this folder.
// The template's actual content is still today's AvalonDock-XML file format (unchanged) - the
// versioned layout DTO described in the doc's Phase 2 is a separate, larger effort.
//
// OnActivating wires the AddIn's panes to actually appear when this layout is selected (2026-08-02
// follow-up): without it, picking "ILSpy" from ChooseLayoutComboBox before ever touching the
// AddIn's own menu commands (File > Open > Assembly) would silently restore nothing for the
// assemblyListPane/searchPane/analyzerPane anchorables in ILSpy.xml, since IlSpyWorkspaceHost had
// never registered them with DockWorkspace yet - the layout is data the AddIn owns, so activating
// it is also the AddIn's responsibility, not something the shell should have to know to trigger.

using System.Collections.Generic;
using System.IO;
using System.Reflection;

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class IlSpyLayoutTemplateProvider : ILayoutTemplateProvider
	{
		static readonly string TemplateFilePath = Path.Combine(
			Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
			"Layouts", "ILSpy.xml");

		public IEnumerable<LayoutTemplateDescriptor> GetLayoutTemplates()
		{
			// DisplayName matches the shell's own entries ("Default layout"/"Debug layout"/...)
			// whose displayName comes from LayoutConfig.xml resource keys.
			yield return new LayoutTemplateDescriptor("ILSpy", "ILSpy layout", TemplateFilePath, readOnly: false,
				onActivating: IlSpyWorkspaceHost.EnsureInitialized);
		}
	}
}
