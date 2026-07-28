using System.IO;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Commands;

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
