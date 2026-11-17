using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Commands;

public class SaveAllFiles : AbstractMenuCommand
{
	public static void SaveAll()
	{
		foreach (IViewContent content in SD.Workbench.ViewContentCollection) {
			var customizedCommands = content.GetService<ICustomizedCommands>();
			if (customizedCommands != null && content.IsDirty) {
				customizedCommands.SaveCommand();
			}
		}
		foreach (OpenedFile file in SD.FileService.OpenedFiles) {
			if (file.IsDirty) {
				SaveFile.Save(file);
			}
		}
	}
	
	public override void Run()
	{
		SaveAll();
	}
}
