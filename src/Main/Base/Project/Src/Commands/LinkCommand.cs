// Extracted from the excluded HelpCommands.cs (excluded because AboutSharpDevelop used the removed
// SD.WinForms.MainWin32Window/CommonAboutDialog). LinkCommand itself has no WinForms dependency and is
// used by CallHelper.cs (CommandWrapper.LinkCommandCreator), so it's kept as a standalone file.
using System;
using System.Diagnostics;
using System.IO;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Commands
{
	public class LinkCommand : AbstractMenuCommand
	{
		string site;

		public LinkCommand(string site)
		{
			this.site = site;
		}

		public override void Run()
		{
			if (site.StartsWith("home://")) {
				string file = Path.Combine(FileUtility.ApplicationRootPath, site.Substring(7).Replace('/', Path.DirectorySeparatorChar));
				try {
					Process.Start(file);
				} catch (Exception) {
					MessageService.ShowError("Can't execute/view " + file + "\n Please check that the file exists and that you can open this file.");
				}
			} else {
				FileService.OpenFile(site);
			}
		}
	}
}
