using System;
using ICSharpCode.Core;
using Xceed.Wpf.Toolkit.PropertyGrid.Commands;

namespace ICSharpCode.SharpDevelop.Gui
{
	public class PropertyPadResetCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var grid = PropertyPad.Grid;
			if (grid?.SelectedPropertyItem != null) {
				PropertyItemCommands.ResetValue.Execute(null, grid.SelectedPropertyItem);
			}
		}
	}

	public class PropertyPadShowDescriptionCommand : AbstractCheckableMenuCommand
	{
		public override bool IsChecked {
			get { return false; }
			set { }
		}
	}
}
