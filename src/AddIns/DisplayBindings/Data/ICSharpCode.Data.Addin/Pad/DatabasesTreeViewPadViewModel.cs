using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;

using ICSharpCode.Data.Core.Interfaces;
using ICSharpCode.Data.Core.UI.UserControls;
using ICSharpCode.SharpDevelop;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.Data.Addin.Pad
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="DatabasesTreeViewPad"/> (AddInTree pad id
	/// "DatabasesTreeViewPad"). Not a MEF part - the AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> - so it is constructed with a plain <c>new</c> by the
	/// <see cref="DatabasesTreeViewPad"/> shim on first real use and registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	sealed class DatabasesTreeViewPadViewModel : ToolPaneModel
	{
		readonly DatabasesTreeViewUserControl _control;
		readonly DatabasesTreeView _databasesTreeView;

		public ObservableCollection<IDatabase> Databases => _databasesTreeView.Databases;

		public DatabasesTreeViewPadViewModel()
		{
			Title = "Database Explorer";
			ContentId = "DatabasesTreeViewPad";
			IsVisible = false;
			IsCloseable = true;
			LegacyPadClass = typeof(DatabasesTreeViewPad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;
			Content = _control = new DatabasesTreeViewUserControl();

			_databasesTreeView = new DatabasesTreeView();
			DockPanel.SetDock(_databasesTreeView, Dock.Top);
			_control.Content.Children.Add(_databasesTreeView);
		}
	}
}
