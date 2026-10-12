// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.Linq;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.FormsDesigner.OutOfProcess;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.FormsDesigner
{
	/// <summary>
	/// The out-of-process counterpart of the in-process designer's CustomComponentsSideTab: the
	/// project's own controls join the shared Toolbox, one category per assembly. Which controls
	/// qualify is <see cref="WinFormsProjectControlFinder"/>'s metadata-only decision; this class
	/// only turns them into Toolbox items carrying the type name, which the child resolves against
	/// the same assemblies when the control is dropped.
	/// </summary>
	static class FormsProjectToolbox
	{
		const string WinFormsScope = "winforms";

		static readonly WinFormsProjectControlFinder finder = new WinFormsProjectControlFinder();

		public static void AddProjectControls(DesignerDocumentSnapshot snapshot)
		{
			var items = finder.FindNew(snapshot.ProjectAssemblyPath, snapshot.ReferencedAssemblyPaths,
					typeof(System.Windows.Forms.Control).Assembly.Location)
				.Select(found => CreateItem(found.Category, found.Control))
				.ToList();
			if (items.Count > 0)
				SharedToolbox.Instance.AddItems(items);
		}

		static SharedToolboxItem CreateItem(string category, ToolboxMetadataDiscovery.ControlInfo control)
		{
			// TypeName alone: the drop handler and the child need nothing else, and building a
			// ToolboxItem from a Type would load the project assembly here.
			var toolboxItem = new ToolboxItem { TypeName = control.FullName, DisplayName = control.Name };
			return new SharedToolboxItem(category, control.Name, WinFormsScope,
				icon: DesignerTypeIcons.GetIcon(control.Name, control.IsUserControl ? "UserControl" : DesignerTypeIcons.FallbackIconName),
				payload: toolboxItem,
				packDragData: data => data.SetData(typeof(ToolboxItem), toolboxItem),
				onActivated: () => (SD.Services.GetService(typeof(IToolboxService)) as IToolboxService)?.SetSelectedToolboxItem(toolboxItem),
				filters: control.Filters);
		}
	}
}
