// Extracted from the excluded FileCommands.cs (which pulled in the WinForms/ProjectBrowser
// CreateNewFile/RenameFile command classes) so BuildCommands.cs's "save all before build" call keeps
// working. Simplified to call OpenedFile.SaveToDisk() directly rather than going through the excluded
// SaveFile/ICustomizedCommands indirection chain.
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Commands
{
	public class SaveAllFiles : AbstractMenuCommand
	{
		public override void Run()
		{
			SaveAll();
		}

		public static void SaveAll()
		{
			foreach (OpenedFile file in SD.FileService.OpenedFiles) {
				if (file.IsDirty) {
					file.SaveToDisk();
				}
			}
		}
	}
}
