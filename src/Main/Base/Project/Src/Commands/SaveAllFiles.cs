using System.IO;
#if !HAS_UNO
using ICSharpCode.Core;
#endif
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Commands;

#if HAS_UNO
public static class SaveAllFiles
{
	public static void SaveAll()
	{
		foreach (var content in SD.Workbench.ViewContentCollection) {
			foreach (var file in content.Files) {
				if (!file.IsDirty || file.FileName is null)
					continue;
				
				using var stream = new FileStream(file.FileName.ToString(), FileMode.Create, FileAccess.Write);
				content.Save(file, stream);
			}
		}
	}
}
#else
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
#endif
