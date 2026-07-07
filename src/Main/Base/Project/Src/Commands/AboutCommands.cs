using System;
using System.Windows;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Commands
{
	public class AboutSharpDevelop : AbstractMenuCommand
	{
		public override void Run()
		{
			var owner = Application.Current.MainWindow;
			var dialog = new Gui.AboutDialog
			{
				Owner = owner,
				WindowStartupLocation = WindowStartupLocation.CenterOwner
			};
			dialog.ShowDialog();
		}
	}
}
