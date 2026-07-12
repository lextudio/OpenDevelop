using Microsoft.Win32;

using ICSharpCode.Core;

namespace ICSharpCode.ILSpyAddIn.Commands
{
	/// <summary>
	/// File &gt; Open &gt; Assembly: loads the selected assembly into the real, hosted ILSpy
	/// AssemblyTreeModel and shows its pads (Assemblies/Search/Analyzer/Decompiled Code)
	/// alongside OpenDevelop's own.
	/// </summary>
	public sealed class OpenAssemblyCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var dialog = new OpenFileDialog {
				AddExtension = true,
				Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
				FilterIndex = 0,
				Multiselect = false,
				CheckFileExists = true,
			};

			if (dialog.ShowDialog() == true) {
				_ = IlSpyWorkspaceHost.OpenAssembly(dialog.FileName);
			}
		}
	}
}
