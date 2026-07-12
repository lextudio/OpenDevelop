// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// ILSpy's own pane view-models (AssemblyTreeModel, SearchPaneModel, AnalyzerTreeViewModel, ...)
// derive from ICSharpCode.ILSpy.ViewModels.ToolPaneModel, a different CLR type from OpenDevelop's
// own ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel (see doc/technotes/ilspy.md). Rather than
// reconciling the two type hierarchies, this adapter wraps one real ILSpy pane instance and
// exposes it as an OpenDevelop pad, so it can be added to DockWorkspace.ToolPanes and hosted by
// OpenDevelop's own DockingManager/pads structure like any other tool pane.
using System;
using System.ComponentModel;

using SharpDevelopToolPaneModel = ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel;
using IlSpyToolPaneModel = ICSharpCode.ILSpy.ViewModels.ToolPaneModel;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Wraps a real ILSpy <see cref="IlSpyToolPaneModel"/> (and its already-constructed view,
	/// passed in as <paramref name="content"/>) as an OpenDevelop pad.
	/// </summary>
	public sealed class IlSpyToolPaneAdapter : SharpDevelopToolPaneModel
	{
		private readonly IlSpyToolPaneModel target;
		private bool syncingFromTarget;
		private bool syncingToTarget;

		public IlSpyToolPaneAdapter(IlSpyToolPaneModel target, object content)
		{
			this.target = target ?? throw new ArgumentNullException(nameof(target));

			Title = target.Title;
			ContentId = target.ContentId;
			Content = content;
			IsCloseable = target.IsCloseable;

			target.PropertyChanged += Target_PropertyChanged;
			PropertyChanged += Self_PropertyChanged;
		}

		public override void Show()
		{
			base.Show();
			target.Show();
		}

		private void Target_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (syncingToTarget)
				return;
			syncingFromTarget = true;
			try {
				switch (e.PropertyName) {
					case nameof(Title):
						Title = target.Title;
						break;
					case nameof(IsVisible):
						IsVisible = target.IsVisible;
						break;
					case nameof(IsActive):
						IsActive = target.IsActive;
						break;
				}
			} finally {
				syncingFromTarget = false;
			}
		}

		private void Self_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (syncingFromTarget)
				return;
			syncingToTarget = true;
			try {
				switch (e.PropertyName) {
					case nameof(IsVisible):
						target.IsVisible = IsVisible;
						break;
					case nameof(IsActive):
						target.IsActive = IsActive;
						break;
				}
			} finally {
				syncingToTarget = false;
			}
		}
	}
}
