// Copyright (c) 2019 AlphaSierraPapa for the SharpDevelop Team
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

using System.Windows.Input;

namespace ICSharpCode.SharpDevelop.ViewModels
{
#if CROSS_PLATFORM
	public abstract class ToolPaneModel : Dock.Model.TomsToolbox.Controls.Tool
	{
		protected static DockWorkspace DockWorkspace => App.ExportProvider.GetExportedValue<DockWorkspace>();
#else
	public abstract class ToolPaneModel : PaneModel
	{
#endif
		public virtual void Show()
		{
			this.IsActive = true;
			this.IsVisible = true;
#if CROSS_PLATFORM
			DockWorkspace.ActivateToolPane(ContentId);
#endif
		}

		public KeyGesture ShortcutKey { get; protected set; }

		public string Icon { get; protected set; }

		public ICommand AssociatedCommand { get; set; }

		public object Content { get; protected set; }

		/// <summary>
		/// Host-neutral layout hint (doc/technotes/ilspy.md "Modern pane and document model"):
		/// preferred initial docked size along the pane's docking axis, in DIPs. Null means "no
		/// preference, let the layout/AvalonDock default apply" - existing panes that don't set
		/// this see no behavior change. Read by <see cref="DockWorkspace.AfterInsertAnchorable"/>
		/// to replace what used to be a single `ContentId == "ProjectBrowser"` special case.
		/// </summary>
		public double? PreferredDockSize { get; protected set; }

		/// <summary>
		/// Host-neutral layout hint for which side of the workbench this pane prefers to dock to.
		/// Not yet consulted by <see cref="DockWorkspace"/> (today's layout comes entirely from the
		/// persisted AvalonDock XML) - added now, alongside <see cref="PreferredDockSize"/>, so both
		/// halves of the doc's target `ToolPaneModel` contract exist together; wiring it into layout
		/// placement is follow-on work (see doc's "Docking and layout replacement" Phase 2).
		/// </summary>
		public PreferredDockSide? PreferredDockSide { get; protected set; }
	}

	public enum PreferredDockSide
	{
		Left,
		Right,
		Top,
		Bottom,
	}
}
