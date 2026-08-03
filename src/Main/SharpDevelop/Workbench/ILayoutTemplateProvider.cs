// New extension point (doc/technotes/ilspy.md "Immediate next actions" #4, 2026-08-02): lets an
// AddIn contribute a named workbench layout template instead of the shell hard-coding every named
// layout (Default/Debug/Plain) into data/layouts/LayoutConfig.xml alongside AddIn-specific ones
// (ILSpy). The shell still owns the layout DTO/switching mechanism (LayoutConfiguration); AddIns
// own which named layouts exist. Deliberately NOT the full versioned layout DTO the doc's Phase 2
// describes (pane identity/side/group/order/proportions/floating bounds) - that's blocked on
// AvalonDockLayout.LoadLayout() actually re-enabling dockWorkspace.RestoreLayout() (currently
// stubbed out pending legacy-pad-to-MEF migration), a separate, larger effort. This slice reuses
// today's AvalonDock-XML-file format as the template's content, exactly as the doc allows
// ("Treat AvalonDock XML as a WPF serialization detail or import format").

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Contributed by an AddIn (via the <c>/SharpDevelop/Workbench/LayoutTemplates</c> AddInTree
	/// path) to register one or more named workbench layout templates, the same way
	/// <c>ITreeNodeFactory</c>/<c>IMSBuildAdditionalLogger</c> contribute their own extension
	/// points - implementations need only a public parameterless constructor.
	/// </summary>
	public interface ILayoutTemplateProvider
	{
		IEnumerable<LayoutTemplateDescriptor> GetLayoutTemplates();
	}

	/// <summary>
	/// Describes one AddIn-contributed named layout. <see cref="TemplateFileName"/> is either a
	/// bare filename, resolved the same way <see cref="LayoutConfiguration"/> resolves its
	/// XML-configured layouts' <c>file</c> attribute (relative to
	/// <see cref="LayoutConfiguration.DataLayoutPath"/>) - or a rooted absolute path, which lets
	/// the AddIn ship its template physically inside its own AddIn folder instead of the shell's
	/// data/layouts (see doc/technotes/ilspy.md "layout file ownership"; ILSpyAddIn uses this to
	/// keep Layouts/ILSpy.xml alongside its own sources). Either way, this is not (yet) the
	/// versioned layout DTO described in the doc's Phase 2 - still today's AvalonDock-XML file
	/// format as the template's content.
	/// </summary>
	public sealed class LayoutTemplateDescriptor
	{
		public string Name { get; }
		public string DisplayName { get; }
		public string TemplateFileName { get; }
		public bool ReadOnly { get; }

		/// <summary>
		/// Invoked by <see cref="LayoutConfiguration.CurrentLayoutName"/> right before the layout
		/// XML is (re)loaded, whenever this named layout is selected - lets an AddIn whose panes
		/// aren't registered yet (e.g. never touched via its own menu commands) register/show them
		/// on demand, so switching to this layout is what actually surfaces the AddIn's panes
		/// rather than the layout XML silently no-op'ing on ContentIds nothing has registered yet
		/// (<see cref="DockWorkspace"/>'s <c>LayoutSerializationCallback</c> cancels/skips any
		/// serialized anchorable whose ContentId isn't a currently-registered ToolPaneModel).
		/// Optional - null means "nothing to activate" (the common case for a layout that only
		/// arranges already-registered built-in panes).
		/// </summary>
		public Action OnActivating { get; }

		public LayoutTemplateDescriptor(string name, string displayName, string templateFileName, bool readOnly = false, Action onActivating = null)
		{
			Name = name;
			DisplayName = displayName;
			TemplateFileName = templateFileName;
			ReadOnly = readOnly;
			OnActivating = onActivating;
		}
	}
}
