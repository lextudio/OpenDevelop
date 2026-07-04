// Extracted/simplified from the excluded FileCommands.cs (which pulled in WinForms/ProjectBrowser
// CreateNewFile/RenameFile classes). Keeps a minimal "save this view content's dirty files" helper for
// AvalonWorkbenchWindow.cs's close-with-unsaved-changes prompt. Simplified relative to the original:
// skips the ICustomizedCommands/"save as" flow for untitled or read-only files (acceptable for an MVP -
// OpenedFile.SaveToDisk() already handles the common case).
using System.Linq;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Commands
{
	public static class SaveFileHelper
	{
		public static void Save(IViewContent content)
		{
			if (content == null || !content.IsDirty)
				return;
			var customizedCommands = content.GetService<ICustomizedCommands>();
			if (customizedCommands != null && customizedCommands.SaveCommand())
				return;
			if (content.IsViewOnly)
				return;
			foreach (OpenedFile file in content.Files.ToArray()) {
				if (file.IsDirty)
					file.SaveToDisk();
			}
		}
	}
}
