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

		/// <summary>Opens a web address in the system browser. Replaceable so tests need not start one.</summary>
		public static Action<string> WebLauncher { get; set; } =
			url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
			} else if (site.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| site.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
				// A web address is not a file: FileService.OpenFile cannot open it (Help > Web ... items did nothing).
				try {
					WebLauncher(site);
				} catch (Exception ex) {
					MessageService.ShowError("Can't open " + site + "\n" + ex.Message);
				}
			} else {
				FileService.OpenFile(site);
			}
		}
	}
}
