using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace Plugins.RegExpTk
{
	public class RegExpTkCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var dialog = new RegExpTkDialog();
			dialog.Owner = SD.Workbench.MainWindow;
			dialog.Show();
		}
	}
}
